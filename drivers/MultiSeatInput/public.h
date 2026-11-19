/*
 * MultiSeatInput – shared definitions between the kernel filter driver and user-mode programs.
 *
 * The driver is a keyboard/mouse class upper filter (it sits just below kbdclass / mouclass on every
 * keyboard and mouse). For each physical device it can:
 *   PASS    – input goes to Windows as normal (the main desktop),
 *   CAPTURE – input is taken away from Windows and handed to the MultiSeat manager, which sends it
 *             to the player's seat.
 * A MONITOR switch copies all input to the manager without taking it (for "press a key on your keyboard").
 *
 * Safety: when the manager closes its handle (exit or crash), every device goes back to PASS.
 */
#pragma once

#define MSI_VERSION             1
#define MSI_DEVICE_TYPE         0x8A51
#define MSI_DEVICE_NAME         L"\\Device\\MultiSeatInput"
#define MSI_DOS_NAME            L"\\DosDevices\\MultiSeatInput"
#define MSI_USER_PATH           L"\\\\.\\MultiSeatInput"

/* out: ULONG version */
#define IOCTL_MSI_GET_VERSION   CTL_CODE(MSI_DEVICE_TYPE, 0x800, METHOD_BUFFERED, FILE_READ_ACCESS)
/* out: MSI_DEVICE_INFO[] */
#define IOCTL_MSI_LIST_DEVICES  CTL_CODE(MSI_DEVICE_TYPE, 0x801, METHOD_BUFFERED, FILE_READ_ACCESS)
/* in: MSI_SET_MODE */
#define IOCTL_MSI_SET_MODE      CTL_CODE(MSI_DEVICE_TYPE, 0x802, METHOD_BUFFERED, FILE_WRITE_ACCESS)
/* in: ULONG (0 = off, 1 = copy every event to the manager) */
#define IOCTL_MSI_SET_MONITOR   CTL_CODE(MSI_DEVICE_TYPE, 0x803, METHOD_BUFFERED, FILE_WRITE_ACCESS)
/* out: MSI_EVENT[]; stays pending until at least one event is available */
#define IOCTL_MSI_READ          CTL_CODE(MSI_DEVICE_TYPE, 0x804, METHOD_BUFFERED, FILE_READ_ACCESS)

#define MSI_HWID_CHARS          128

#define MSI_KIND_UNKNOWN        0
#define MSI_KIND_KEYBOARD       1
#define MSI_KIND_MOUSE          2

#define MSI_MODE_PASS           0
#define MSI_MODE_CAPTURE        1

#pragma pack(push, 4)

typedef struct _MSI_DEVICE_INFO {
    ULONG Id;                               /* stable while the device stays plugged in */
    ULONG Kind;                             /* MSI_KIND_* (UNKNOWN until Windows connects it) */
    ULONG Mode;                             /* MSI_MODE_* */
    WCHAR HardwareId[MSI_HWID_CHARS];       /* first hardware id, e.g. HID\VID_046D&PID_C52B&MI_00 */
} MSI_DEVICE_INFO, *PMSI_DEVICE_INFO;

typedef struct _MSI_SET_MODE {
    ULONG Id;
    ULONG Mode;
} MSI_SET_MODE, *PMSI_SET_MODE;

/* One keyboard or mouse event (24 bytes). Fields are copied from KEYBOARD_INPUT_DATA / MOUSE_INPUT_DATA. */
typedef struct _MSI_EVENT {
    ULONG  DeviceId;
    USHORT Kind;            /* MSI_KIND_KEYBOARD or MSI_KIND_MOUSE */
    USHORT Flags;           /* keyboard: KEY_BREAK / KEY_E0 / KEY_E1;  mouse: MOUSE_MOVE_ABSOLUTE ... */
    USHORT MakeCode;        /* keyboard scan code */
    USHORT ButtonFlags;     /* mouse: MOUSE_LEFT_BUTTON_DOWN ... MOUSE_WHEEL / MOUSE_HWHEEL */
    SHORT  ButtonData;      /* mouse wheel delta */
    USHORT Captured;        /* 1 = taken from Windows, 0 = copy only (monitor mode) */
    LONG   X;               /* mouse LastX */
    LONG   Y;               /* mouse LastY */
} MSI_EVENT, *PMSI_EVENT;

#pragma pack(pop)
