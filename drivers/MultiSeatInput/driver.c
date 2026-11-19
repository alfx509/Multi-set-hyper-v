/*
 * MultiSeatInput.sys – KMDF keyboard/mouse class upper filter for MultiSeat.
 *
 * Position in the device stack (per physical keyboard/mouse):
 *
 *      kbdclass / mouclass          <- Windows input (main desktop)
 *      MultiSeatInput   (this)      <- installed in the class UpperFilters list BEFORE kbdclass/mouclass
 *      kbdhid / mouhid / i8042prt   <- function driver
 *
 * Like the WDK kbfiltr/moufiltr samples, we intercept IOCTL_INTERNAL_KEYBOARD_CONNECT /
 * IOCTL_INTERNAL_MOUSE_CONNECT, remember the class driver's service callback and put ours in its place.
 * Every input packet then passes through MsiKeyboardService / MsiMouseService, where we either pass it
 * up to Windows or capture it for the MultiSeat manager (user mode, via \\.\MultiSeatInput).
 *
 * STATUS: prototype, written without a Windows build machine. Build and test ONLY in a test VM with
 * test signing on (see drivers/MultiSeatInput/README.md). A broken class filter can disable every
 * keyboard and mouse.
 */

#include <ntddk.h>
#include <wdf.h>
#include <ntddkbd.h>
#include <ntddmou.h>
#include <kbdmou.h>
#include "public.h"

#define MSI_POOL_TAG        'iSsM'
#define MSI_RING_SIZE       4096            /* events buffered for the manager */
#define MSI_KEY_STATES      512             /* scan code + 256 for E0-prefixed keys */

DRIVER_INITIALIZE DriverEntry;
EVT_WDF_DRIVER_DEVICE_ADD MsiEvtDeviceAdd;
EVT_WDF_DEVICE_CONTEXT_CLEANUP MsiEvtFilterCleanup;
EVT_WDF_IO_QUEUE_IO_INTERNAL_DEVICE_CONTROL MsiEvtInternalDeviceControl;
EVT_WDF_IO_QUEUE_IO_DEVICE_CONTROL MsiEvtControlDeviceControl;
EVT_WDF_FILE_CLEANUP MsiEvtControlFileCleanup;
EVT_WDF_DRIVER_UNLOAD MsiEvtDriverUnload;

typedef struct _FILTER_CONTEXT {
    LIST_ENTRY      Link;
    WDFDEVICE       Device;
    ULONG           Id;
    ULONG           Kind;
    volatile LONG   Mode;
    CONNECT_DATA    Upper;                          /* kbdclass/mouclass service callback */
    WCHAR           HardwareId[MSI_HWID_CHARS];
    UCHAR           KeyDown[MSI_KEY_STATES];        /* keys currently held (keyboard) */
    USHORT          ButtonsDown;                    /* MOUSE_*_BUTTON_DOWN bits held (mouse) */
} FILTER_CONTEXT, *PFILTER_CONTEXT;

WDF_DECLARE_CONTEXT_TYPE_WITH_NAME(FILTER_CONTEXT, FilterGetContext)

/* ---------------------------------------------------------------- globals */

static WDFWAITLOCK      g_ListLock;                 /* protects g_Filters and g_Control creation */
static LIST_ENTRY       g_Filters;
static WDFDEVICE        g_Control;
static WDFQUEUE         g_ReadQueue;                /* pending IOCTL_MSI_READ requests */
static volatile LONG    g_NextId;
static volatile LONG    g_Monitor;                  /* copy all events (pairing) */

static KSPIN_LOCK       g_RingLock;                 /* protects the ring and g_ReadQueue use */
static PMSI_EVENT       g_Ring;
static ULONG            g_RingHead;                 /* next write */
static ULONG            g_RingCount;

/* ---------------------------------------------------------------- event ring */

static VOID MsiCompletePendingRead(VOID)
{
    /* Caller holds g_RingLock (at DISPATCH_LEVEL). */
    WDFREQUEST request;
    PMSI_EVENT out;
    size_t outBytes;
    ULONG n, i, tail;

    if (g_ReadQueue == NULL || g_RingCount == 0) {
        return;
    }

    if (!NT_SUCCESS(WdfIoQueueRetrieveNextRequest(g_ReadQueue, &request))) {
        return;
    }

    if (!NT_SUCCESS(WdfRequestRetrieveOutputBuffer(request, sizeof(MSI_EVENT), (PVOID*)&out, &outBytes))) {
        WdfRequestComplete(request, STATUS_BUFFER_TOO_SMALL);
        return;
    }

    n = (ULONG)min(outBytes / sizeof(MSI_EVENT), (size_t)g_RingCount);
    tail = (g_RingHead + MSI_RING_SIZE - g_RingCount) % MSI_RING_SIZE;
    for (i = 0; i < n; i++) {
        out[i] = g_Ring[(tail + i) % MSI_RING_SIZE];
    }

    g_RingCount -= n;
    WdfRequestCompleteWithInformation(request, STATUS_SUCCESS, (ULONG_PTR)n * sizeof(MSI_EVENT));
}

static VOID MsiPushEvent(_In_ const MSI_EVENT* e)
{
    KIRQL irql;
    KeAcquireSpinLock(&g_RingLock, &irql);
    if (g_Ring != NULL) {
        g_Ring[g_RingHead] = *e;
        g_RingHead = (g_RingHead + 1) % MSI_RING_SIZE;
        if (g_RingCount < MSI_RING_SIZE) {
            g_RingCount++;          /* when full, the oldest event is overwritten */
        }

        MsiCompletePendingRead();
    }

    KeReleaseSpinLock(&g_RingLock, irql);
}

/* ---------------------------------------------------------------- service callbacks (DISPATCH_LEVEL) */

static __forceinline USHORT MsiKeyIndex(_In_ const KEYBOARD_INPUT_DATA* k)
{
    return (USHORT)((k->MakeCode & 0xFF) + ((k->Flags & KEY_E0) ? 256 : 0));
}

VOID MsiKeyboardService(
    _In_ PDEVICE_OBJECT DeviceObject,
    _In_ PKEYBOARD_INPUT_DATA InputDataStart,
    _In_ PKEYBOARD_INPUT_DATA InputDataEnd,
    _Inout_ PULONG InputDataConsumed)
{
    PFILTER_CONTEXT ctx = FilterGetContext(WdfWdmDeviceGetWdfDeviceHandle(DeviceObject));
    LONG mode = InterlockedCompareExchange(&ctx->Mode, 0, 0);
    BOOLEAN capture = (mode == MSI_MODE_CAPTURE);
    BOOLEAN copy = capture || InterlockedCompareExchange(&g_Monitor, 0, 0) != 0;
    PKEYBOARD_INPUT_DATA k;

    for (k = InputDataStart; k < InputDataEnd; k++) {
        /* Track held keys only while they go to Windows, so we can release them before capturing. */
        if (!capture) {
            ctx->KeyDown[MsiKeyIndex(k)] = (k->Flags & KEY_BREAK) ? 0 : 1;
        }

        if (copy) {
            MSI_EVENT e;
            RtlZeroMemory(&e, sizeof(e));
            e.DeviceId = ctx->Id;
            e.Kind = MSI_KIND_KEYBOARD;
            e.Flags = k->Flags;
            e.MakeCode = k->MakeCode;
            e.Captured = capture ? 1 : 0;
            MsiPushEvent(&e);
        }
    }

    if (capture) {
        *InputDataConsumed = (ULONG)(InputDataEnd - InputDataStart);
        return;
    }

    (*(PSERVICE_CALLBACK_ROUTINE)(ULONG_PTR)ctx->Upper.ClassService)(
        ctx->Upper.ClassDeviceObject, InputDataStart, InputDataEnd, InputDataConsumed);
}

VOID MsiMouseService(
    _In_ PDEVICE_OBJECT DeviceObject,
    _In_ PMOUSE_INPUT_DATA InputDataStart,
    _In_ PMOUSE_INPUT_DATA InputDataEnd,
    _Inout_ PULONG InputDataConsumed)
{
    static const USHORT downBits = MOUSE_LEFT_BUTTON_DOWN | MOUSE_RIGHT_BUTTON_DOWN | MOUSE_MIDDLE_BUTTON_DOWN |
                                   MOUSE_BUTTON_4_DOWN | MOUSE_BUTTON_5_DOWN;
    PFILTER_CONTEXT ctx = FilterGetContext(WdfWdmDeviceGetWdfDeviceHandle(DeviceObject));
    LONG mode = InterlockedCompareExchange(&ctx->Mode, 0, 0);
    BOOLEAN capture = (mode == MSI_MODE_CAPTURE);
    BOOLEAN copy = capture || InterlockedCompareExchange(&g_Monitor, 0, 0) != 0;
    PMOUSE_INPUT_DATA m;

    for (m = InputDataStart; m < InputDataEnd; m++) {
        if (!capture) {
            /* Each *_UP bit is the matching *_DOWN bit shifted left by one. */
            ctx->ButtonsDown |= (m->ButtonFlags & downBits);
            ctx->ButtonsDown &= (USHORT)~((m->ButtonFlags >> 1) & downBits);
        }

        if (copy) {
            MSI_EVENT e;
            RtlZeroMemory(&e, sizeof(e));
            e.DeviceId = ctx->Id;
            e.Kind = MSI_KIND_MOUSE;
            e.Flags = m->Flags;
            e.ButtonFlags = m->ButtonFlags;
            e.ButtonData = (SHORT)m->ButtonData;
            e.X = m->LastX;
            e.Y = m->LastY;
            e.Captured = capture ? 1 : 0;
            MsiPushEvent(&e);
        }
    }

    if (capture) {
        *InputDataConsumed = (ULONG)(InputDataEnd - InputDataStart);
        return;
    }

    (*(PSERVICE_CALLBACK_ROUTINE)(ULONG_PTR)ctx->Upper.ClassService)(
        ctx->Upper.ClassDeviceObject, InputDataStart, InputDataEnd, InputDataConsumed);
}

/*
 * Before a device is captured, send "release" for every key/button Windows still thinks is held,
 * otherwise the main desktop would see e.g. Ctrl stuck down forever.
 */
static VOID MsiReleaseHeld(_In_ PFILTER_CONTEXT ctx)
{
    KIRQL irql;
    ULONG consumed;

    if (ctx->Upper.ClassService == NULL) {
        return;
    }

    KeRaiseIrql(DISPATCH_LEVEL, &irql);   /* class service callbacks expect DISPATCH_LEVEL */

    if (ctx->Kind == MSI_KIND_KEYBOARD) {
        USHORT i;
        for (i = 0; i < MSI_KEY_STATES; i++) {
            if (ctx->KeyDown[i]) {
                KEYBOARD_INPUT_DATA k;
                RtlZeroMemory(&k, sizeof(k));
                k.MakeCode = (USHORT)(i & 0xFF);
                k.Flags = (USHORT)(KEY_BREAK | ((i >= 256) ? KEY_E0 : 0));
                consumed = 0;
                (*(PSERVICE_CALLBACK_ROUTINE)(ULONG_PTR)ctx->Upper.ClassService)(
                    ctx->Upper.ClassDeviceObject, &k, &k + 1, &consumed);
                ctx->KeyDown[i] = 0;
            }
        }
    } else if (ctx->Kind == MSI_KIND_MOUSE && ctx->ButtonsDown != 0) {
        MOUSE_INPUT_DATA m;
        RtlZeroMemory(&m, sizeof(m));
        m.Flags = MOUSE_MOVE_RELATIVE;
        m.ButtonFlags = (USHORT)(ctx->ButtonsDown << 1);   /* the matching *_UP bits */
        consumed = 0;
        (*(PSERVICE_CALLBACK_ROUTINE)(ULONG_PTR)ctx->Upper.ClassService)(
            ctx->Upper.ClassDeviceObject, &m, &m + 1, &consumed);
        ctx->ButtonsDown = 0;
    }

    KeLowerIrql(irql);
}

/* ---------------------------------------------------------------- filter device: connect interception */

VOID MsiEvtInternalDeviceControl(
    _In_ WDFQUEUE Queue,
    _In_ WDFREQUEST Request,
    _In_ size_t OutputBufferLength,
    _In_ size_t InputBufferLength,
    _In_ ULONG IoControlCode)
{
    WDFDEVICE device = WdfIoQueueGetDevice(Queue);
    PFILTER_CONTEXT ctx = FilterGetContext(device);
    NTSTATUS status = STATUS_SUCCESS;
    PCONNECT_DATA connect;
    size_t length;
    WDF_REQUEST_SEND_OPTIONS options;

    UNREFERENCED_PARAMETER(OutputBufferLength);
    UNREFERENCED_PARAMETER(InputBufferLength);

    switch (IoControlCode) {
    case IOCTL_INTERNAL_KEYBOARD_CONNECT:
    case IOCTL_INTERNAL_MOUSE_CONNECT:
        if (ctx->Upper.ClassService != NULL) {
            status = STATUS_SHARING_VIOLATION;     /* only one class driver may connect */
            break;
        }

        status = WdfRequestRetrieveInputBuffer(Request, sizeof(CONNECT_DATA), (PVOID*)&connect, &length);
        if (!NT_SUCCESS(status)) {
            break;
        }

        ctx->Upper = *connect;
        ctx->Kind = (IoControlCode == IOCTL_INTERNAL_KEYBOARD_CONNECT) ? MSI_KIND_KEYBOARD : MSI_KIND_MOUSE;
        connect->ClassDeviceObject = WdfDeviceWdmGetDeviceObject(device);
        connect->ClassService = (IoControlCode == IOCTL_INTERNAL_KEYBOARD_CONNECT)
            ? (PVOID)(ULONG_PTR)MsiKeyboardService
            : (PVOID)(ULONG_PTR)MsiMouseService;
        break;

    case IOCTL_INTERNAL_KEYBOARD_DISCONNECT:
    case IOCTL_INTERNAL_MOUSE_DISCONNECT:
        status = STATUS_NOT_IMPLEMENTED;           /* same as the WDK samples */
        break;

    default:
        break;
    }

    if (!NT_SUCCESS(status)) {
        WdfRequestComplete(Request, status);
        return;
    }

    WdfRequestFormatRequestUsingCurrentType(Request);
    WDF_REQUEST_SEND_OPTIONS_INIT(&options, WDF_REQUEST_SEND_OPTION_SEND_AND_FORGET);
    if (!WdfRequestSend(Request, WdfDeviceGetIoTarget(device), &options)) {
        WdfRequestComplete(Request, WdfRequestGetStatus(Request));
    }
}

/* ---------------------------------------------------------------- control device (\\.\MultiSeatInput) */

static PFILTER_CONTEXT MsiFindFilter(_In_ ULONG id)
{
    /* Caller holds g_ListLock. */
    PLIST_ENTRY entry;
    for (entry = g_Filters.Flink; entry != &g_Filters; entry = entry->Flink) {
        PFILTER_CONTEXT ctx = CONTAINING_RECORD(entry, FILTER_CONTEXT, Link);
        if (ctx->Id == id) {
            return ctx;
        }
    }

    return NULL;
}

static VOID MsiReleaseAllDevices(VOID)
{
    PLIST_ENTRY entry;
    InterlockedExchange(&g_Monitor, 0);
    WdfWaitLockAcquire(g_ListLock, NULL);
    for (entry = g_Filters.Flink; entry != &g_Filters; entry = entry->Flink) {
        PFILTER_CONTEXT ctx = CONTAINING_RECORD(entry, FILTER_CONTEXT, Link);
        InterlockedExchange(&ctx->Mode, MSI_MODE_PASS);
    }

    WdfWaitLockRelease(g_ListLock);
}

VOID MsiEvtControlDeviceControl(
    _In_ WDFQUEUE Queue,
    _In_ WDFREQUEST Request,
    _In_ size_t OutputBufferLength,
    _In_ size_t InputBufferLength,
    _In_ ULONG IoControlCode)
{
    NTSTATUS status = STATUS_INVALID_DEVICE_REQUEST;
    ULONG_PTR info = 0;
    PVOID buffer;
    size_t length;

    UNREFERENCED_PARAMETER(Queue);
    UNREFERENCED_PARAMETER(OutputBufferLength);
    UNREFERENCED_PARAMETER(InputBufferLength);

    switch (IoControlCode) {
    case IOCTL_MSI_GET_VERSION:
        status = WdfRequestRetrieveOutputBuffer(Request, sizeof(ULONG), &buffer, &length);
        if (NT_SUCCESS(status)) {
            *(PULONG)buffer = MSI_VERSION;
            info = sizeof(ULONG);
        }
        break;

    case IOCTL_MSI_LIST_DEVICES:
        status = WdfRequestRetrieveOutputBuffer(Request, sizeof(MSI_DEVICE_INFO), &buffer, &length);
        if (NT_SUCCESS(status)) {
            PMSI_DEVICE_INFO out = (PMSI_DEVICE_INFO)buffer;
            size_t max = length / sizeof(MSI_DEVICE_INFO);
            size_t n = 0;
            PLIST_ENTRY entry;
            WdfWaitLockAcquire(g_ListLock, NULL);
            for (entry = g_Filters.Flink; entry != &g_Filters && n < max; entry = entry->Flink, n++) {
                PFILTER_CONTEXT ctx = CONTAINING_RECORD(entry, FILTER_CONTEXT, Link);
                RtlZeroMemory(&out[n], sizeof(MSI_DEVICE_INFO));
                out[n].Id = ctx->Id;
                out[n].Kind = ctx->Kind;
                out[n].Mode = (ULONG)ctx->Mode;
                RtlCopyMemory(out[n].HardwareId, ctx->HardwareId, sizeof(ctx->HardwareId));
            }

            WdfWaitLockRelease(g_ListLock);
            info = n * sizeof(MSI_DEVICE_INFO);
        }
        break;

    case IOCTL_MSI_SET_MODE:
        status = WdfRequestRetrieveInputBuffer(Request, sizeof(MSI_SET_MODE), &buffer, &length);
        if (NT_SUCCESS(status)) {
            PMSI_SET_MODE set = (PMSI_SET_MODE)buffer;
            PFILTER_CONTEXT ctx;
            if (set->Mode != MSI_MODE_PASS && set->Mode != MSI_MODE_CAPTURE) {
                status = STATUS_INVALID_PARAMETER;
                break;
            }

            WdfWaitLockAcquire(g_ListLock, NULL);
            ctx = MsiFindFilter(set->Id);
            if (ctx == NULL) {
                status = STATUS_NOT_FOUND;
            } else {
                if (set->Mode == MSI_MODE_CAPTURE && ctx->Mode != MSI_MODE_CAPTURE) {
                    MsiReleaseHeld(ctx);
                }

                InterlockedExchange(&ctx->Mode, (LONG)set->Mode);
            }

            WdfWaitLockRelease(g_ListLock);
        }
        break;

    case IOCTL_MSI_SET_MONITOR:
        status = WdfRequestRetrieveInputBuffer(Request, sizeof(ULONG), &buffer, &length);
        if (NT_SUCCESS(status)) {
            InterlockedExchange(&g_Monitor, (*(PULONG)buffer) ? 1 : 0);
        }
        break;

    case IOCTL_MSI_READ:
        status = WdfRequestRetrieveOutputBuffer(Request, sizeof(MSI_EVENT), &buffer, &length);
        if (NT_SUCCESS(status)) {
            KIRQL irql;
            status = WdfRequestForwardToIoQueue(Request, g_ReadQueue);
            if (NT_SUCCESS(status)) {
                KeAcquireSpinLock(&g_RingLock, &irql);
                MsiCompletePendingRead();           /* complete at once if events are waiting */
                KeReleaseSpinLock(&g_RingLock, irql);
                return;                             /* the request now belongs to the read queue */
            }
        }
        break;

    default:
        break;
    }

    WdfRequestCompleteWithInformation(Request, status, info);
}

/* The manager closed its handle (exit or crash): give every keyboard and mouse back to Windows. */
VOID MsiEvtControlFileCleanup(_In_ WDFFILEOBJECT FileObject)
{
    UNREFERENCED_PARAMETER(FileObject);
    MsiReleaseAllDevices();
}

static NTSTATUS MsiCreateControlDevice(_In_ WDFDRIVER Driver)
{
    /* Caller holds g_ListLock. Only Administrators and SYSTEM may open the control device. */
    DECLARE_CONST_UNICODE_STRING(sddl, L"D:P(A;;GA;;;SY)(A;;GA;;;BA)");
    DECLARE_CONST_UNICODE_STRING(deviceName, MSI_DEVICE_NAME);
    DECLARE_CONST_UNICODE_STRING(dosName, MSI_DOS_NAME);
    PWDFDEVICE_INIT init;
    WDF_FILEOBJECT_CONFIG fileConfig;
    WDF_IO_QUEUE_CONFIG queueConfig;
    WDF_OBJECT_ATTRIBUTES attributes;
    WDFDEVICE control;
    NTSTATUS status;

    if (g_Control != NULL) {
        return STATUS_SUCCESS;
    }

    init = WdfControlDeviceInitAllocate(Driver, &sddl);
    if (init == NULL) {
        return STATUS_INSUFFICIENT_RESOURCES;
    }

    WdfDeviceInitSetExclusive(init, TRUE);         /* one manager at a time */
    WdfDeviceInitSetIoType(init, WdfDeviceIoBuffered);
    status = WdfDeviceInitAssignName(init, &deviceName);
    if (!NT_SUCCESS(status)) {
        WdfDeviceInitFree(init);
        return status;
    }

    WDF_FILEOBJECT_CONFIG_INIT(&fileConfig, WDF_NO_EVENT_CALLBACK, WDF_NO_EVENT_CALLBACK, MsiEvtControlFileCleanup);
    WdfDeviceInitSetFileObjectConfig(init, &fileConfig, WDF_NO_OBJECT_ATTRIBUTES);

    WDF_OBJECT_ATTRIBUTES_INIT(&attributes);
    attributes.ExecutionLevel = WdfExecutionLevelPassive;
    status = WdfDeviceCreate(&init, &attributes, &control);
    if (!NT_SUCCESS(status)) {
        WdfDeviceInitFree(init);
        return status;
    }

    status = WdfDeviceCreateSymbolicLink(control, &dosName);
    if (NT_SUCCESS(status)) {
        WDF_IO_QUEUE_CONFIG_INIT_DEFAULT_QUEUE(&queueConfig, WdfIoQueueDispatchSequential);
        queueConfig.EvtIoDeviceControl = MsiEvtControlDeviceControl;
        status = WdfIoQueueCreate(control, &queueConfig, WDF_NO_OBJECT_ATTRIBUTES, WDF_NO_HANDLE);
    }

    if (NT_SUCCESS(status)) {
        WDFQUEUE readQueue;
        WDF_IO_QUEUE_CONFIG_INIT(&queueConfig, WdfIoQueueDispatchManual);
        status = WdfIoQueueCreate(control, &queueConfig, WDF_NO_OBJECT_ATTRIBUTES, &readQueue);
        if (NT_SUCCESS(status)) {
            KIRQL irql;
            KeAcquireSpinLock(&g_RingLock, &irql);
            g_ReadQueue = readQueue;
            KeReleaseSpinLock(&g_RingLock, irql);
        }
    }

    if (!NT_SUCCESS(status)) {
        WdfObjectDelete(control);
        return status;
    }

    WdfControlFinishInitializing(control);
    g_Control = control;
    return STATUS_SUCCESS;
}

static VOID MsiDeleteControlDevice(VOID)
{
    /* Caller holds g_ListLock. */
    KIRQL irql;
    if (g_Control == NULL) {
        return;
    }

    KeAcquireSpinLock(&g_RingLock, &irql);
    g_ReadQueue = NULL;
    g_RingCount = 0;
    KeReleaseSpinLock(&g_RingLock, irql);

    WdfObjectDelete(g_Control);
    g_Control = NULL;
}

/* ---------------------------------------------------------------- filter device lifetime */

NTSTATUS MsiEvtDeviceAdd(_In_ WDFDRIVER Driver, _Inout_ PWDFDEVICE_INIT DeviceInit)
{
    WDF_OBJECT_ATTRIBUTES attributes;
    WDF_IO_QUEUE_CONFIG queueConfig;
    WDFDEVICE device;
    PFILTER_CONTEXT ctx;
    WDFMEMORY hwidMemory = NULL;
    NTSTATUS status;

    PAGED_CODE();

    WdfFdoInitSetFilter(DeviceInit);

    /* First hardware id of the device (REG_MULTI_SZ), to recognise the same keyboard/mouse later. */
    (VOID)WdfFdoInitAllocAndQueryProperty(DeviceInit, DevicePropertyHardwareID, NonPagedPoolNx,
                                          WDF_NO_OBJECT_ATTRIBUTES, &hwidMemory);

    WDF_OBJECT_ATTRIBUTES_INIT_CONTEXT_TYPE(&attributes, FILTER_CONTEXT);
    attributes.EvtCleanupCallback = MsiEvtFilterCleanup;
    status = WdfDeviceCreate(&DeviceInit, &attributes, &device);
    if (!NT_SUCCESS(status)) {
        if (hwidMemory != NULL) {
            WdfObjectDelete(hwidMemory);
        }
        return status;
    }

    ctx = FilterGetContext(device);
    RtlZeroMemory(ctx, sizeof(*ctx));
    InitializeListHead(&ctx->Link);
    ctx->Device = device;
    ctx->Id = (ULONG)InterlockedIncrement(&g_NextId);
    ctx->Mode = MSI_MODE_PASS;
    if (hwidMemory != NULL) {
        size_t bytes;
        PWCHAR hwid = (PWCHAR)WdfMemoryGetBuffer(hwidMemory, &bytes);
        size_t chars = min(bytes / sizeof(WCHAR), (size_t)(MSI_HWID_CHARS - 1));
        RtlCopyMemory(ctx->HardwareId, hwid, chars * sizeof(WCHAR));
        ctx->HardwareId[MSI_HWID_CHARS - 1] = L'\0';   /* first string of the multi-sz, truncated */
        WdfObjectDelete(hwidMemory);
    }

    WDF_IO_QUEUE_CONFIG_INIT_DEFAULT_QUEUE(&queueConfig, WdfIoQueueDispatchParallel);
    queueConfig.EvtIoInternalDeviceControl = MsiEvtInternalDeviceControl;
    status = WdfIoQueueCreate(device, &queueConfig, WDF_NO_OBJECT_ATTRIBUTES, WDF_NO_HANDLE);
    if (!NT_SUCCESS(status)) {
        return status;
    }

    WdfWaitLockAcquire(g_ListLock, NULL);
    InsertTailList(&g_Filters, &ctx->Link);
    (VOID)MsiCreateControlDevice(Driver);
    WdfWaitLockRelease(g_ListLock);

    /* A missing control device must never stop the keyboard/mouse from working: ignore that failure. */
    return STATUS_SUCCESS;
}

VOID MsiEvtFilterCleanup(_In_ WDFOBJECT Object)
{
    PFILTER_CONTEXT ctx = FilterGetContext((WDFDEVICE)Object);

    WdfWaitLockAcquire(g_ListLock, NULL);
    if (!IsListEmpty(&ctx->Link)) {
        RemoveEntryList(&ctx->Link);
        InitializeListHead(&ctx->Link);
    }

    if (IsListEmpty(&g_Filters)) {
        MsiDeleteControlDevice();
    }

    WdfWaitLockRelease(g_ListLock);
}

/* ---------------------------------------------------------------- driver entry / unload */

VOID MsiEvtDriverUnload(_In_ WDFDRIVER Driver)
{
    UNREFERENCED_PARAMETER(Driver);
    if (g_Ring != NULL) {
        ExFreePoolWithTag(g_Ring, MSI_POOL_TAG);
        g_Ring = NULL;
    }
}


NTSTATUS DriverEntry(_In_ PDRIVER_OBJECT DriverObject, _In_ PUNICODE_STRING RegistryPath)
{
    WDF_DRIVER_CONFIG config;
    NTSTATUS status;

    InitializeListHead(&g_Filters);
    KeInitializeSpinLock(&g_RingLock);

    g_Ring = (PMSI_EVENT)ExAllocatePool2(POOL_FLAG_NON_PAGED, sizeof(MSI_EVENT) * MSI_RING_SIZE, MSI_POOL_TAG);
    if (g_Ring == NULL) {
        return STATUS_INSUFFICIENT_RESOURCES;
    }

    WDF_DRIVER_CONFIG_INIT(&config, MsiEvtDeviceAdd);
    config.EvtDriverUnload = MsiEvtDriverUnload;
    status = WdfDriverCreate(DriverObject, RegistryPath, WDF_NO_OBJECT_ATTRIBUTES, &config, WDF_NO_HANDLE);
    if (NT_SUCCESS(status)) {
        status = WdfWaitLockCreate(WDF_NO_OBJECT_ATTRIBUTES, &g_ListLock);
    }

    if (!NT_SUCCESS(status)) {
        ExFreePoolWithTag(g_Ring, MSI_POOL_TAG);
        g_Ring = NULL;
    }

    return status;
}
