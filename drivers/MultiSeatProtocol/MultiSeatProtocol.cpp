// MultiSeatProtocol.dll - "MultiSeat Local" Remote Desktop Protocol Provider (Phase 2, experiment B1).
//
// Loaded by Remote Desktop Services (TermService) because the listener key
//   HKLM\SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations\MultiSeat-Local
// has LoadableProtocol_Object = CLSID_MultiSeatProtocolManager.
//
// B1 goal: find out whether Windows creates a real session for our provider, and in which order it
// calls us. Every call is logged to C:\ProgramData\MultiSeat\protocol.log (and OutputDebugString).
// A seat "connects" when someone signals the event Global\MultiSeatLocalConnect
// (setup\dev\Connect-LocalSeat.ps1). No video, no input yet.
//
// Rule from the docs: never call Remote Desktop Services APIs from inside these callbacks
// (deadlock) - calls back into the service (OnConnected, OnReady) run on our own threads.
//
// TEST VM ONLY. Remove with setup\dev\Install-ProtocolProvider.ps1 -Uninstall.

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <sddl.h>
#include <wtsprotocol.h>
#include "Licensing.h"
#include <atomic>
#include <cstdio>
#include <cwchar>
#include <new>

// {2372A2FF-3F7C-4CFD-B896-1FA8163924FF}
static const CLSID CLSID_MultiSeatProtocolManager =
    { 0x2372a2ff, 0x3f7c, 0x4cfd, { 0xb8, 0x96, 0x1f, 0xa8, 0x16, 0x39, 0x24, 0xff } };

static const wchar_t* const kConnectEvent = L"Global\\MultiSeatLocalConnect";
static const wchar_t* const kLogDir = L"C:\\ProgramData\\MultiSeat";
static const wchar_t* const kLogFile = L"C:\\ProgramData\\MultiSeat\\protocol.log";
// Hardware id of the (future, B2) remote IDD; logged now so we see whether Windows asks for it.
static const wchar_t* const kIddHardwareId = L"MultiSeatRemoteIdd";   // no backslash: Windows makes SWD\REMOTEDISPLAYENUM\<id>&SESSIONID_n

static HMODULE g_module;
static std::atomic<long> g_objects{ 0 };
static std::atomic<long> g_locks{ 0 };
static SRWLOCK g_logLock = SRWLOCK_INIT;

// ------------------------------------------------------------------ logging

void Log(const wchar_t* format, ...)
{
    wchar_t message[1024];
    va_list args;
    va_start(args, format);
    _vsnwprintf_s(message, _TRUNCATE, format, args);
    va_end(args);

    SYSTEMTIME t;
    GetLocalTime(&t);
    wchar_t line[1200];
    _snwprintf_s(line, _TRUNCATE, L"%02u:%02u:%02u.%03u [pid %lu tid %lu] %s\r\n", t.wHour, t.wMinute, t.wSecond,
        t.wMilliseconds, GetCurrentProcessId(), GetCurrentThreadId(), message);
    OutputDebugStringW(line);

    AcquireSRWLockExclusive(&g_logLock);
    CreateDirectoryW(kLogDir, nullptr);
    HANDLE file = CreateFileW(kLogFile, FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr, OPEN_ALWAYS,
        FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file != INVALID_HANDLE_VALUE) {
        char utf8[2400];
        int bytes = WideCharToMultiByte(CP_UTF8, 0, line, -1, utf8, sizeof(utf8), nullptr, nullptr);
        DWORD written = 0;
        if (bytes > 1) {
            WriteFile(file, utf8, static_cast<DWORD>(bytes - 1), &written, nullptr);
        }
        CloseHandle(file);
    }
    ReleaseSRWLockExclusive(&g_logLock);
}

// Copies text into a fixed WTS field and cuts it if it is too long (wcscpy_s would end the process).
template <size_t N>
static void CopyText(wchar_t (&dst)[N], const wchar_t* src)
{
    wcsncpy_s(dst, N, src ? src : L"", _TRUNCATE);
}

// Who may use the listener and its sessions, in WINSTATION_* rights (generic rights like GA are NOT mapped:
// with GA nobody could query the seat session, WTS hid it and explorer crashed). SYSTEM/Administrators all
// (0xF03FF); signed-in users QUERY|VIRTUAL|LOGON|LOGOFF|MSGBOX|CONNECT|DISCONNECT (0x3E9).
// An empty descriptor makes OnConnected fail with ERROR_UNKNOWN_REVISION (found in B1).
// Returned in CoTaskMemAlloc memory (the caller frees it).
static bool MakeListenerSecurity(PBYTE* descriptor, ULONG* size)
{
    *descriptor = nullptr;
    *size = 0;
    PSECURITY_DESCRIPTOR sd = nullptr;
    ULONG length = 0;
    if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(
            L"O:SYG:SYD:(A;;0xF03FF;;;SY)(A;;0xF03FF;;;BA)(A;;0x3E9;;;AU)(A;;0x3E9;;;IU)", SDDL_REVISION_1, &sd, &length)) {
        return false;
    }
    *descriptor = static_cast<PBYTE>(CoTaskMemAlloc(length));
    if (*descriptor) {
        memcpy(*descriptor, sd, length);
        *size = length;
    }
    LocalFree(sd);
    return *descriptor != nullptr;
}

static void GuidText(REFGUID g, wchar_t (&out)[40])
{
    if (StringFromGUID2(g, out, 40) == 0) {
        wcscpy_s(out, L"{?}");
    }
}

// ------------------------------------------------------------------ small COM helper

template <class T>
class RefCounted
{
public:
    RefCounted() { ++g_objects; }
    virtual ~RefCounted() { --g_objects; }

protected:
    ULONG AddRefImpl() { return static_cast<ULONG>(++_refs); }
    ULONG ReleaseImpl()
    {
        long refs = --_refs;
        if (refs == 0) {
            delete static_cast<T*>(this);
        }
        return static_cast<ULONG>(refs);
    }

private:
    std::atomic<long> _refs{ 1 };
};

// Logs a binary blob as hex, 32 bytes per line (licensing PDUs for the MS-RDPELE work; no secrets in them).
void LogHex(const wchar_t* label, const BYTE* data, ULONG size)
{
    Log(L"%s: %lu bytes", label, size);
    for (ULONG offset = 0; data && offset < size; offset += 32) {
        wchar_t line[3 * 32 + 1] = {};
        for (ULONG i = 0; i < 32 && offset + i < size; i++) {
            swprintf_s(line + i * 3, 4, L"%02X ", data[offset + i]);
        }
        Log(L"  %04lX  %s", offset, line);
    }
}

// ------------------------------------------------------------------ licensing handshake (one per connection)
//
// Without it the service closes the connection (B1: GetLicenseConnection E_NOTIMPL -> SetErrorInfo(0x106)).
// A local seat has no client licence store: we report basic capabilities and accept what the service
// issues. RD Session Host licensing itself (grace period / RDS CALs) is handled by Windows.

class LicenseConnection final : public IWRdsProtocolLicenseConnection, public RefCounted<LicenseConnection>
{
public:
    // B4 licensing experiments: HKLM\SOFTWARE\MultiSeat\Protocol\LicenseMode (DWORD)
    //   0 = report capabilities, no stored licence (default)   1 = RequestClientLicense -> E_NOTIMPL
    //   2 = RequestLicensingCapabilities -> E_NOTIMPL          3 = both E_NOTIMPL
    //   +8 = capabilities say proprietary certificate. fAuthenticateServer is always TRUE (needed for MS-RDPELE).
    explicit LicenseConnection(ULONG seat) : _seat(seat)
    {
        DWORD size = sizeof(_mode);
        RegGetValueW(HKEY_LOCAL_MACHINE, L"SOFTWARE\\MultiSeat\\Protocol", L"LicenseMode", RRF_RT_REG_DWORD, nullptr, &_mode, &size);
        Log(L"License[%lu] mode %lu", _seat, _mode);
    }

    STDMETHODIMP QueryInterface(REFIID riid, void** ppv) override
    {
        if (!ppv) {
            return E_POINTER;
        }
        if (riid == IID_IUnknown || riid == __uuidof(IWRdsProtocolLicenseConnection)) {
            *ppv = static_cast<IWRdsProtocolLicenseConnection*>(this);
            AddRef();
            return S_OK;
        }
        *ppv = nullptr;
        return E_NOINTERFACE;
    }

    STDMETHODIMP_(ULONG) AddRef() override { return AddRefImpl(); }
    STDMETHODIMP_(ULONG) Release() override { return ReleaseImpl(); }

    STDMETHODIMP RequestLicensingCapabilities(PWRDS_LICENSE_CAPABILITIES caps, ULONG* size) override
    {
        Log(L"License[%lu] RequestLicensingCapabilities(size %lu)", _seat, size ? *size : 0);
        if (_mode & 2) {
            Log(L"License[%lu]   -> E_NOTIMPL (mode)", _seat);
            return E_NOTIMPL;
        }
        if (!caps || !size || *size < sizeof(WRDS_LICENSE_CAPABILITIES)) {
            return E_INVALIDARG;
        }
        ZeroMemory(caps, sizeof(*caps));
        caps->KeyExchangeAlg = WRDS_KEY_EXCHANGE_ALG_RSA;
        caps->ProtocolVer = WRDS_LICENSE_CURRENT_PROTOCOL_VERSION;
        caps->fAuthenticateServer = TRUE;   // the licence request then carries the server certificate (RSA key)
        caps->CertType = (_mode & 8) ? WRDS_CERT_TYPE_PROPRIETORY : WRDS_CERT_TYPE_X509;   // mode bit 8
        char name[16];
        int length = sprintf_s(name, "SEAT%lu", _seat);
        if (length > 0) {
            memcpy(caps->rgbClientName, name, static_cast<size_t>(length) + 1);
            caps->cbClientName = static_cast<DWORD>(length) + 1;
        }
        *size = sizeof(WRDS_LICENSE_CAPABILITIES);
        return S_OK;
    }

    STDMETHODIMP SendClientLicense(PBYTE data, ULONG bytes) override
    {
        Log(L"License[%lu] SendClientLicense(%lu bytes)", _seat, bytes);
        LogHex(L"  server -> client licence PDU", data, bytes);
        _client.Receive(data, bytes);
        return S_OK;
    }

    STDMETHODIMP RequestClientLicense(PBYTE request, ULONG reserved, PBYTE response, ULONG* bytes) override
    {
        LogHex(L"  server licence request", request, reserved);
        if (_mode & 1) {
            Log(L"License[%lu] RequestClientLicense -> E_NOTIMPL (mode)", _seat);
            if (bytes) {
                *bytes = 0;
            }
            return E_NOTIMPL;
        }
        if (!bytes) {
            return E_POINTER;
        }

        // [MS-RDPELE] client side: answer the server PDU (licence request, platform challenge).
        std::vector<BYTE> answer;
        if (!_client.Respond(request, reserved, answer)) {
            Log(L"License[%lu] RequestClientLicense(reserved %lu) -> no answer", _seat, reserved);
            *bytes = 0;
            return S_OK;
        }
        if (!response || *bytes < answer.size()) {
            Log(L"License[%lu] RequestClientLicense: buffer %lu too small for %zu", _seat, *bytes, answer.size());
            *bytes = static_cast<ULONG>(answer.size());
            return HRESULT_FROM_WIN32(ERROR_INSUFFICIENT_BUFFER);
        }
        memcpy(response, answer.data(), answer.size());
        *bytes = static_cast<ULONG>(answer.size());
        LogHex(L"  client licence PDU", response, *bytes);
        return S_OK;
    }

    STDMETHODIMP ProtocolComplete(ULONG complete) override
    {
        Log(L"License[%lu] ProtocolComplete(%lu) %s", _seat, complete, complete == 1 ? L"= success" : L"= FAILED");
        return S_OK;
    }

private:
    ULONG _seat;
    DWORD _mode = 0;
    LicensingClient _client{ _seat };
};

// ------------------------------------------------------------------ shadow connection (stub)
//
// B2: returning E_NOTIMPL from GetShadowConnection was followed at once by PreDisconnect(18). This object
// exists so the service gets one; actual shadowing (an admin watching the seat) is not supported.

class ShadowConnection final : public IWRdsProtocolShadowConnection, public RefCounted<ShadowConnection>
{
public:
    explicit ShadowConnection(ULONG seat) : _seat(seat) {}

    STDMETHODIMP QueryInterface(REFIID riid, void** ppv) override
    {
        if (!ppv) {
            return E_POINTER;
        }
        if (riid == IID_IUnknown || riid == __uuidof(IWRdsProtocolShadowConnection)) {
            *ppv = static_cast<IWRdsProtocolShadowConnection*>(this);
            AddRef();
            return S_OK;
        }
        *ppv = nullptr;
        return E_NOINTERFACE;
    }

    STDMETHODIMP_(ULONG) AddRef() override { return AddRefImpl(); }
    STDMETHODIMP_(ULONG) Release() override { return ReleaseImpl(); }

    STDMETHODIMP Start(WCHAR*, DWORD target, BYTE, USHORT, IWRdsProtocolShadowCallback*) override
    {
        Log(L"Shadow[%lu] Start(target session %lu) -> E_NOTIMPL", _seat, target);
        return E_NOTIMPL;
    }

    STDMETHODIMP Stop() override
    {
        Log(L"Shadow[%lu] Stop", _seat);
        return S_OK;
    }

    STDMETHODIMP DoTarget(PBYTE, DWORD, PBYTE, DWORD, PBYTE, DWORD, PBYTE, DWORD, WCHAR*) override
    {
        Log(L"Shadow[%lu] DoTarget -> E_NOTIMPL", _seat);
        return E_NOTIMPL;
    }

private:
    ULONG _seat;
};

// ------------------------------------------------------------------ connection (one per seat)

class Connection final : public IWRdsProtocolConnection, public IWRdsWddmIddProps, public IWRdsWddmIddProps1,
                         public RefCounted<Connection>
{
public:
    explicit Connection(ULONG seat) : _seat(seat) { Log(L"Connection[%lu] created", _seat); }
    ~Connection() override
    {
        if (_callback) {
            _callback->Release();
        }
        Log(L"Connection[%lu] destroyed", _seat);
    }

    void SetCallback(IWRdsProtocolConnectionCallback* callback)
    {
        _callback = callback;
        if (_callback) {
            _callback->AddRef();
        }
    }

    // IUnknown
    STDMETHODIMP QueryInterface(REFIID riid, void** ppv) override
    {
        if (!ppv) {
            return E_POINTER;
        }

        if (riid == IID_IUnknown || riid == __uuidof(IWRdsProtocolConnection)) {
            *ppv = static_cast<IWRdsProtocolConnection*>(this);
        } else if (riid == __uuidof(IWRdsWddmIddProps)) {
            Log(L"Connection[%lu] QueryInterface(IWRdsWddmIddProps) -> yes", _seat);
            *ppv = static_cast<IWRdsWddmIddProps*>(this);
        } else if (riid == __uuidof(IWRdsWddmIddProps1)) {
            Log(L"Connection[%lu] QueryInterface(IWRdsWddmIddProps1) -> yes", _seat);
            *ppv = static_cast<IWRdsWddmIddProps1*>(this);
        } else {
            wchar_t text[40];
            GuidText(riid, text);
            Log(L"Connection[%lu] QueryInterface(%s) -> E_NOINTERFACE", _seat, text);
            *ppv = nullptr;
            return E_NOINTERFACE;
        }

        AddRef();
        return S_OK;
    }

    STDMETHODIMP_(ULONG) AddRef() override { return AddRefImpl(); }
    STDMETHODIMP_(ULONG) Release() override { return ReleaseImpl(); }

    // IWRdsProtocolConnection
    STDMETHODIMP GetLogonErrorRedirector(IWRdsProtocolLogonErrorRedirector** redirector) override
    {
        Log(L"Connection[%lu] GetLogonErrorRedirector -> none", _seat);
        if (redirector) {
            *redirector = nullptr;
        }

        SignalReady(L"after GetLogonErrorRedirector");
        return S_OK;
    }

    // The service waits for OnReady before AcceptConnection. Never call it from inside a service callback:
    // it runs on our own thread, and only once per connection.
    void SignalReady(const wchar_t* why)
    {
        if (_readySent.exchange(true)) {
            return;
        }
        Log(L"Connection[%lu] calling OnReady (%s)", _seat, why);
        AddRef();
        HANDLE thread = CreateThread(nullptr, 0, [](void* p) -> DWORD {
            auto* self = static_cast<Connection*>(p);
            HRESULT hr = self->_callback ? self->_callback->OnReady() : E_UNEXPECTED;
            Log(L"Connection[%lu] OnReady() = 0x%08X", self->_seat, static_cast<unsigned>(hr));
            self->Release();
            return 0;
        }, this, 0, nullptr);
        if (thread) {
            CloseHandle(thread);
        } else {
            Release();
        }
    }

    STDMETHODIMP AcceptConnection() override
    {
        Log(L"Connection[%lu] AcceptConnection", _seat);
        return S_OK;
    }

    STDMETHODIMP GetClientData(WRDS_CLIENT_DATA* data) override
    {
        Log(L"Connection[%lu] GetClientData", _seat);
        if (!data) {
            return E_POINTER;
        }

        ZeroMemory(data, sizeof(*data));
        data->fEnableWindowsKey = TRUE;
        data->fInheritAutoLogon = FALSE;             // show the Windows sign-in screen on the seat
        data->fInheritInitialProgram = FALSE;
        data->fInheritColorDepth = FALSE;
        data->HRes = 1920;
        data->VRes = 1080;
        data->ColorDepth = 32;
        data->fMouse = TRUE;
        data->KeyboardLayout = 0x0409;
        data->KeyboardType = 4;
        data->KeyboardFunctionKey = 12;
        data->ActiveInputLocale = 0x0409;
        data->fNoAudioPlayback = TRUE;
        CopyText(data->ProtocolName, L"MSLocal");   // max 8 chars (WTS_PROTOCOL_NAME_LENGTH)
        swprintf_s(data->ClientName, L"SEAT%lu", _seat);
        data->ClientBuildNumber = 26100;
        data->fDisableCpm = data->fDisableCdm = data->fDisableCcm = data->fDisableLPT = data->fDisableClip = TRUE;
        return S_OK;
    }

    STDMETHODIMP GetClientMonitorData(UINT* monitors, UINT* primary) override
    {
        Log(L"Connection[%lu] GetClientMonitorData -> 1 monitor", _seat);
        if (monitors) {
            *monitors = 1;
        }
        if (primary) {
            *primary = 0;
        }
        return S_OK;
    }

    STDMETHODIMP GetUserCredentials(WRDS_USER_CREDENTIAL* creds) override
    {
        Log(L"Connection[%lu] GetUserCredentials -> none (sign-in screen)", _seat);
        if (creds) {
            ZeroMemory(creds, sizeof(*creds));
        }
        return E_NOTIMPL;
    }

    STDMETHODIMP GetLicenseConnection(IWRdsProtocolLicenseConnection** license) override
    {
        if (!license) {
            return E_POINTER;
        }
        *license = new (std::nothrow) LicenseConnection(_seat);   // one reference, owned by the caller
        Log(L"Connection[%lu] GetLicenseConnection -> %s", _seat, *license ? L"ok" : L"out of memory");
        return *license ? S_OK : E_OUTOFMEMORY;
    }

    STDMETHODIMP AuthenticateClientToSession(WRDS_SESSION_ID* sessionId) override
    {
        Log(L"Connection[%lu] AuthenticateClientToSession -> E_NOTIMPL (new session)", _seat);
        if (sessionId) {
            ZeroMemory(sessionId, sizeof(*sessionId));
        }
        return E_NOTIMPL;
    }

    STDMETHODIMP NotifySessionId(WRDS_SESSION_ID* sessionId, HANDLE_PTR sessionHandle) override
    {
        _sessionId = sessionId ? sessionId->SessionId : 0;
        Log(L"Connection[%lu] NotifySessionId -> session %lu (handle 0x%p)", _seat, _sessionId,
            reinterpret_cast<void*>(sessionHandle));
        return S_OK;
    }

    STDMETHODIMP GetInputHandles(HANDLE_PTR* keyboard, HANDLE_PTR* mouse, HANDLE_PTR* beep) override
    {
        Log(L"Connection[%lu] GetInputHandles -> none yet (B4)", _seat);
        if (keyboard) {
            *keyboard = 0;
        }
        if (mouse) {
            *mouse = 0;
        }
        if (beep) {
            *beep = 0;
        }
        return S_OK;
    }

    STDMETHODIMP GetVideoHandle(HANDLE_PTR* video) override
    {
        Log(L"Connection[%lu] GetVideoHandle -> none (WDDM IDD mode expected)", _seat);
        if (video) {
            *video = 0;
        }
        return S_OK;
    }

    STDMETHODIMP ConnectNotify(ULONG sessionId) override
    {
        Log(L"Connection[%lu] ConnectNotify(session %lu)", _seat, sessionId);
        return S_OK;
    }

    STDMETHODIMP IsUserAllowedToLogon(ULONG sessionId, HANDLE_PTR, WCHAR* domain, WCHAR* user) override
    {
        Log(L"Connection[%lu] IsUserAllowedToLogon(session %lu, %s\\%s) -> yes", _seat, sessionId,
            domain ? domain : L"", user ? user : L"");
        return S_OK;
    }

    STDMETHODIMP SessionArbitrationEnumeration(HANDLE_PTR, BOOL singlePerUser, ULONG*, ULONG* count) override
    {
        Log(L"Connection[%lu] SessionArbitrationEnumeration(singlePerUser=%d) -> none", _seat, singlePerUser);
        if (count) {
            *count = 0;
        }
        return S_OK;
    }

    STDMETHODIMP LogonNotify(HANDLE_PTR, WCHAR* user, WCHAR* domain, WRDS_SESSION_ID* sessionId,
        PWRDS_CONNECTION_SETTINGS) override
    {
        Log(L"Connection[%lu] LogonNotify(%s\\%s, session %lu)", _seat, domain ? domain : L"", user ? user : L"",
            sessionId ? sessionId->SessionId : 0);
        return S_OK;
    }

    STDMETHODIMP PreDisconnect(ULONG reason) override
    {
        Log(L"Connection[%lu] PreDisconnect(reason %lu)", _seat, reason);
        return S_OK;
    }

    STDMETHODIMP DisconnectNotify() override
    {
        Log(L"Connection[%lu] DisconnectNotify", _seat);
        return S_OK;
    }

    STDMETHODIMP Close() override
    {
        Log(L"Connection[%lu] Close", _seat);
        return S_OK;
    }

    STDMETHODIMP GetProtocolStatus(WRDS_PROTOCOL_STATUS* status) override
    {
        if (status) {
            ZeroMemory(status, sizeof(*status));
        }
        return S_OK;
    }

    STDMETHODIMP GetLastInputTime(ULONG64* time) override
    {
        if (time) {
            FILETIME now;
            GetSystemTimeAsFileTime(&now);
            *time = (static_cast<ULONG64>(now.dwHighDateTime) << 32) | now.dwLowDateTime;
        }
        return S_OK;
    }

    STDMETHODIMP SetErrorInfo(ULONG error) override
    {
        Log(L"Connection[%lu] SetErrorInfo(0x%08X)", _seat, error);
        return S_OK;
    }

    STDMETHODIMP CreateVirtualChannel(CHAR* name, BOOL isStatic, ULONG, ULONG_PTR* channel) override
    {
        Log(L"Connection[%lu] CreateVirtualChannel(%S, static=%d) -> E_NOTIMPL", _seat, name ? name : "", isStatic);
        if (channel) {
            *channel = 0;
        }
        return E_NOTIMPL;
    }

    STDMETHODIMP QueryProperty(GUID queryType, ULONG, ULONG, PWRDS_PROPERTY_VALUE, PWRDS_PROPERTY_VALUE) override
    {
        wchar_t text[40];
        GuidText(queryType, text);
        Log(L"Connection[%lu] QueryProperty(%s) -> E_NOTIMPL", _seat, text);
        return E_NOTIMPL;
    }

    STDMETHODIMP GetShadowConnection(IWRdsProtocolShadowConnection** shadow) override
    {
        if (!shadow) {
            return E_POINTER;
        }
        *shadow = new (std::nothrow) ShadowConnection(_seat);   // one reference, owned by the caller
        Log(L"Connection[%lu] GetShadowConnection -> stub object", _seat);
        return *shadow ? S_OK : E_OUTOFMEMORY;
    }

    STDMETHODIMP NotifyCommandProcessCreated(ULONG sessionId) override
    {
        Log(L"Connection[%lu] NotifyCommandProcessCreated(session %lu)", _seat, sessionId);
        return S_OK;
    }

    // IWRdsWddmIddProps (B2 uses it to load our remote IDD into the session)
    STDMETHODIMP GetHardwareId(WCHAR* hardwareId, DWORD count) override
    {
        Log(L"Connection[%lu] GetHardwareId(count %lu) -> %s", _seat, count, kIddHardwareId);
        if (!hardwareId || count == 0) {
            return E_POINTER;
        }
        if (wcslen(kIddHardwareId) >= count) {
            return E_NOT_SUFFICIENT_BUFFER;
        }
        wcsncpy_s(hardwareId, count, kIddHardwareId, _TRUNCATE);
        return S_OK;
    }

    STDMETHODIMP OnDriverLoad(ULONG sessionId, HANDLE_PTR driverHandle) override
    {
        Log(L"Connection[%lu] OnDriverLoad(session %lu, handle 0x%p)", _seat, sessionId,
            reinterpret_cast<void*>(driverHandle));
        if (driverHandle) {
            CloseHandle(reinterpret_cast<HANDLE>(driverHandle)); // we own it; nothing to talk to yet
        }
        return S_OK;
    }

    // IWRdsWddmIddProps1 (newer): Windows passes the device instance instead of an opened handle.
    // GetHardwareId and OnDriverUnload have the same signature in both interfaces and are shared.
    STDMETHODIMP OnDriverLoad(ULONG sessionId, LPCWSTR deviceInstance) override
    {
        Log(L"Connection[%lu] OnDriverLoad1(session %lu, device %s)", _seat, sessionId,
            deviceInstance ? deviceInstance : L"(null)");
        return S_OK;
    }

    STDMETHODIMP OnDriverUnload(ULONG sessionId) override
    {
        Log(L"Connection[%lu] OnDriverUnload(session %lu)", _seat, sessionId);
        return S_OK;
    }

    STDMETHODIMP EnableWddmIdd(BOOL enabled) override
    {
        Log(L"Connection[%lu] EnableWddmIdd(%d)", _seat, enabled);
        return S_OK;
    }

private:
    ULONG _seat;
    ULONG _sessionId = 0;
    std::atomic<bool> _readySent{ false };
    IWRdsProtocolConnectionCallback* _callback = nullptr;
};

// ------------------------------------------------------------------ listener

class Listener final : public IWRdsProtocolListener, public RefCounted<Listener>
{
public:
    explicit Listener(const wchar_t* name)
    {
        CopyText(_name, name);
        Log(L"Listener created (%s)", _name);
    }

    ~Listener() override
    {
        StopListen();
        Log(L"Listener destroyed");
    }

    STDMETHODIMP QueryInterface(REFIID riid, void** ppv) override
    {
        if (!ppv) {
            return E_POINTER;
        }
        if (riid == IID_IUnknown || riid == __uuidof(IWRdsProtocolListener)) {
            *ppv = static_cast<IWRdsProtocolListener*>(this);
            AddRef();
            return S_OK;
        }
        *ppv = nullptr;
        return E_NOINTERFACE;
    }

    STDMETHODIMP_(ULONG) AddRef() override { return AddRefImpl(); }
    STDMETHODIMP_(ULONG) Release() override { return ReleaseImpl(); }

    STDMETHODIMP GetSettings(WRDS_LISTENER_SETTING_LEVEL level, PWRDS_LISTENER_SETTINGS settings) override
    {
        Log(L"Listener GetSettings(level %d)", static_cast<int>(level));
        if (!settings) {
            return E_POINTER;
        }
        ZeroMemory(settings, sizeof(*settings));
        settings->WRdsListenerSettingLevel = WRDS_LISTENER_SETTING_LEVEL_1;
        auto& l = settings->WRdsListenerSetting.WRdsListenerSettings1;
        l.MaxProtocolListenerConnectionCount = 8;
        if (!MakeListenerSecurity(&l.pSecurityDescriptor, &l.SecurityDescriptorSize)) {
            Log(L"Listener GetSettings: security descriptor failed (%lu)", GetLastError());
        }
        return S_OK;
    }

    STDMETHODIMP StartListen(IWRdsProtocolListenerCallback* callback) override
    {
        Log(L"Listener StartListen");
        if (!callback) {
            return E_POINTER;
        }
        if (_thread) {
            return S_OK;
        }

        _callback = callback;
        _callback->AddRef();

        // Administrators, SYSTEM and NETWORK SERVICE (TermService) may signal "connect a seat".
        SECURITY_ATTRIBUTES sa = { sizeof(sa), nullptr, FALSE };
        ConvertStringSecurityDescriptorToSecurityDescriptorW(L"D:(A;;GA;;;SY)(A;;GA;;;BA)(A;;GA;;;NS)",
            SDDL_REVISION_1, &sa.lpSecurityDescriptor, nullptr);
        _connect = CreateEventW(&sa, FALSE, FALSE, kConnectEvent);
        LocalFree(sa.lpSecurityDescriptor);
        _stop = CreateEventW(nullptr, TRUE, FALSE, nullptr);
        if (!_connect || !_stop) {
            Log(L"Listener: CreateEvent failed (%lu)", GetLastError());
            return HRESULT_FROM_WIN32(GetLastError());
        }

        AddRef();
        _thread = CreateThread(nullptr, 0, [](void* p) -> DWORD {
            static_cast<Listener*>(p)->Run();
            static_cast<Listener*>(p)->Release();
            return 0;
        }, this, 0, nullptr);
        Log(L"Listener waiting for %s", kConnectEvent);
        return S_OK;
    }

    STDMETHODIMP StopListen() override
    {
        if (_thread) {
            Log(L"Listener StopListen");
            SetEvent(_stop);
            if (GetCurrentThreadId() != GetThreadId(_thread)) {
                WaitForSingleObject(_thread, 5000);
            }
            CloseHandle(_thread);
            _thread = nullptr;
        }
        if (_connect) {
            CloseHandle(_connect);
            _connect = nullptr;
        }
        if (_stop) {
            CloseHandle(_stop);
            _stop = nullptr;
        }
        if (_callback) {
            _callback->Release();
            _callback = nullptr;
        }
        return S_OK;
    }

private:
    void Run()
    {
        HANDLE waits[2] = { _stop, _connect };
        while (WaitForMultipleObjects(2, waits, FALSE, INFINITE) == WAIT_OBJECT_0 + 1) {
            ConnectSeat();
        }
    }

    void ConnectSeat()
    {
        ULONG seat = ++_seats;
        Log(L"Listener: connect requested -> seat %lu, calling OnConnected", seat);
        auto* connection = new (std::nothrow) Connection(seat);
        auto* settings = static_cast<WRDS_CONNECTION_SETTINGS*>(HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY,
            sizeof(WRDS_CONNECTION_SETTINGS)));
        if (!connection || !settings) {
            Log(L"Listener: out of memory");
            if (connection) {
                connection->Release();
            }
            return;
        }

        settings->WRdsConnectionSettingLevel = WRDS_CONNECTION_SETTING_LEVEL_1;
        auto& s = settings->WRdsConnectionSetting.WRdsConnectionSettings1;
        s.fMouse = TRUE;
        s.fEnableWindowsKey = TRUE;
        s.fDisableCpm = s.fDisableCdm = s.fDisableCcm = s.fDisableLPT = s.fDisableClip = TRUE;
        s.fNoAudioPlayback = TRUE;
        s.WRdsListenerSettings.WRdsListenerSettingLevel = WRDS_LISTENER_SETTING_LEVEL_1;
        auto& ls = s.WRdsListenerSettings.WRdsListenerSetting.WRdsListenerSettings1;
        ls.MaxProtocolListenerConnectionCount = 8;
        MakeListenerSecurity(&ls.pSecurityDescriptor, &ls.SecurityDescriptorSize);
        s.ColorDepth = 32;
        s.HRes = 1920;
        s.VRes = 1080;
        s.KeyboardLayout = 0x0409;
        s.ActiveInputLocale = 0x0409;
        s.KeyboardType = 4;
        s.KeyboardFunctionKey = 12;
        s.ClientBuildNumber = 26100;
        CopyText(s.ProtocolName, L"MSLocal");        // max 8 chars (WTS_PROTOCOL_NAME_LENGTH)
        swprintf_s(s.ClientName, L"SEAT%lu", seat);
        CopyText(s.ClientAddress, L"127.0.0.1");

        IWRdsProtocolConnectionCallback* callback = nullptr;
        HRESULT hr = _callback->OnConnected(connection, settings, &callback);
        Log(L"Listener: OnConnected(seat %lu) = 0x%08X, callback %p", seat, static_cast<unsigned>(hr),
            static_cast<void*>(callback));
        if (SUCCEEDED(hr) && callback) {
            connection->SetCallback(callback);
            connection->SignalReady(L"right after OnConnected");
            callback->Release();
        }

        CoTaskMemFree(ls.pSecurityDescriptor);
        HeapFree(GetProcessHeap(), 0, settings);
        connection->Release(); // the service holds its own reference if it accepted the connection
    }

    wchar_t _name[128] = {};
    IWRdsProtocolListenerCallback* _callback = nullptr;
    HANDLE _thread = nullptr;
    HANDLE _connect = nullptr;
    HANDLE _stop = nullptr;
    ULONG _seats = 0;
};

// ------------------------------------------------------------------ protocol manager

class ProtocolManager final : public IWRdsProtocolManager, public RefCounted<ProtocolManager>
{
public:
    ProtocolManager() { Log(L"ProtocolManager created"); }
    ~ProtocolManager() override { Log(L"ProtocolManager destroyed"); }

    STDMETHODIMP QueryInterface(REFIID riid, void** ppv) override
    {
        if (!ppv) {
            return E_POINTER;
        }
        if (riid == IID_IUnknown || riid == __uuidof(IWRdsProtocolManager)) {
            *ppv = static_cast<IWRdsProtocolManager*>(this);
            AddRef();
            return S_OK;
        }
        wchar_t text[40];
        GuidText(riid, text);
        Log(L"ProtocolManager QueryInterface(%s) -> E_NOINTERFACE", text);
        *ppv = nullptr;
        return E_NOINTERFACE;
    }

    STDMETHODIMP_(ULONG) AddRef() override { return AddRefImpl(); }
    STDMETHODIMP_(ULONG) Release() override { return ReleaseImpl(); }

    STDMETHODIMP Initialize(IWRdsProtocolSettings*, PWRDS_SETTINGS) override
    {
        Log(L"ProtocolManager Initialize");
        return S_OK;
    }

    STDMETHODIMP CreateListener(WCHAR* name, IWRdsProtocolListener** listener) override
    {
        Log(L"ProtocolManager CreateListener(%s)", name ? name : L"");
        if (!listener) {
            return E_POINTER;
        }
        *listener = new (std::nothrow) Listener(name); // starts with one reference, owned by the caller
        return *listener ? S_OK : E_OUTOFMEMORY;
    }

    STDMETHODIMP NotifyServiceStateChange(WRDS_SERVICE_STATE*) override
    {
        Log(L"ProtocolManager NotifyServiceStateChange");
        return S_OK;
    }

    STDMETHODIMP NotifySessionOfServiceStart(WRDS_SESSION_ID* id) override
    {
        Log(L"ProtocolManager NotifySessionOfServiceStart(session %lu)", id ? id->SessionId : 0);
        return S_OK;
    }

    STDMETHODIMP NotifySessionOfServiceStop(WRDS_SESSION_ID* id) override
    {
        Log(L"ProtocolManager NotifySessionOfServiceStop(session %lu)", id ? id->SessionId : 0);
        return S_OK;
    }

    STDMETHODIMP NotifySessionStateChange(WRDS_SESSION_ID* id, ULONG eventId) override
    {
        Log(L"ProtocolManager NotifySessionStateChange(session %lu, event %lu)", id ? id->SessionId : 0, eventId);
        return S_OK;
    }

    STDMETHODIMP NotifySettingsChange(PWRDS_SETTINGS) override
    {
        Log(L"ProtocolManager NotifySettingsChange");
        return S_OK;
    }

    STDMETHODIMP Uninitialize() override
    {
        Log(L"ProtocolManager Uninitialize");
        return S_OK;
    }
};

// ------------------------------------------------------------------ class factory + DLL exports

class ClassFactory final : public IClassFactory
{
public:
    STDMETHODIMP QueryInterface(REFIID riid, void** ppv) override
    {
        if (!ppv) {
            return E_POINTER;
        }
        if (riid == IID_IUnknown || riid == IID_IClassFactory) {
            *ppv = static_cast<IClassFactory*>(this);
            return S_OK;
        }
        *ppv = nullptr;
        return E_NOINTERFACE;
    }

    STDMETHODIMP_(ULONG) AddRef() override { return 2; }   // static object
    STDMETHODIMP_(ULONG) Release() override { return 1; }

    STDMETHODIMP CreateInstance(IUnknown* outer, REFIID riid, void** ppv) override
    {
        if (outer) {
            return CLASS_E_NOAGGREGATION;
        }
        auto* manager = new (std::nothrow) ProtocolManager();
        if (!manager) {
            return E_OUTOFMEMORY;
        }
        HRESULT hr = manager->QueryInterface(riid, ppv);
        manager->Release();
        return hr;
    }

    STDMETHODIMP LockServer(BOOL lock) override
    {
        lock ? ++g_locks : --g_locks;
        return S_OK;
    }
};

static ClassFactory g_factory;

extern "C" BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, void*)
{
    if (reason == DLL_PROCESS_ATTACH) {
        g_module = instance;
        DisableThreadLibraryCalls(instance);
    }
    return TRUE;
}

STDAPI DllGetClassObject(REFCLSID clsid, REFIID riid, void** ppv)
{
    if (clsid != CLSID_MultiSeatProtocolManager) {
        return CLASS_E_CLASSNOTAVAILABLE;
    }
    Log(L"DllGetClassObject");
    return g_factory.QueryInterface(riid, ppv);
}

STDAPI DllCanUnloadNow()
{
    return (g_objects == 0 && g_locks == 0) ? S_OK : S_FALSE;
}

static const wchar_t* const kClsidKey = L"SOFTWARE\\Classes\\CLSID\\{2372A2FF-3F7C-4CFD-B896-1FA8163924FF}";

STDAPI DllRegisterServer()
{
    wchar_t path[MAX_PATH];
    if (!GetModuleFileNameW(g_module, path, MAX_PATH)) {
        return HRESULT_FROM_WIN32(GetLastError());
    }

    wchar_t serverKey[200];
    swprintf_s(serverKey, L"%s\\InprocServer32", kClsidKey);
    HKEY key;
    LSTATUS status = RegCreateKeyExW(HKEY_LOCAL_MACHINE, kClsidKey, 0, nullptr, 0, KEY_WRITE, nullptr, &key, nullptr);
    if (status == ERROR_SUCCESS) {
        const wchar_t name[] = L"MultiSeat Local protocol manager";
        RegSetValueExW(key, nullptr, 0, REG_SZ, reinterpret_cast<const BYTE*>(name), sizeof(name));
        RegCloseKey(key);
        status = RegCreateKeyExW(HKEY_LOCAL_MACHINE, serverKey, 0, nullptr, 0, KEY_WRITE, nullptr, &key, nullptr);
    }
    if (status == ERROR_SUCCESS) {
        const wchar_t model[] = L"Both";
        RegSetValueExW(key, nullptr, 0, REG_SZ, reinterpret_cast<const BYTE*>(path),
            static_cast<DWORD>((wcslen(path) + 1) * sizeof(wchar_t)));
        RegSetValueExW(key, L"ThreadingModel", 0, REG_SZ, reinterpret_cast<const BYTE*>(model), sizeof(model));
        RegCloseKey(key);
    }
    return HRESULT_FROM_WIN32(status);
}

STDAPI DllUnregisterServer()
{
    LSTATUS status = RegDeleteTreeW(HKEY_LOCAL_MACHINE, kClsidKey);
    return (status == ERROR_SUCCESS || status == ERROR_FILE_NOT_FOUND) ? S_OK : HRESULT_FROM_WIN32(status);
}
