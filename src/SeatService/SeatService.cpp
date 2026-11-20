// SeatService.exe - "MultiSeat seat agents" Windows service (LocalSystem), Phase 2 B4.
//
// The MultiSeat Local protocol provider runs inside TermService as Network Service and cannot start programs in
// another session. This service can: every second it looks for MultiSeat Local seat sessions (WinStation name
// "multiseat-local#N") and makes sure each one runs exactly one SeatAgent in seat mode:
//
//     SeatAgent.exe --seat <settings.json>     as SYSTEM inside the seat session, from before sign-in
//
// Seat settings are the files C:\ProgramData\MultiSeat\agents\local-*.json (seat name, UDP port, key; written by
// the MultiSeat manager or a test script, readable by Administrators/SYSTEM only). A new seat session gets the
// first file that no other seat session uses. A crashed agent is started again (at most every 5 s).
// When a seat session ends or disconnects, its agent is stopped and its settings are free again.
// If this service stops or crashes, every agent stops too: each agent gets "--parent <service pid>" and exits
// (releasing held keys) when the service is gone; agents are also put in a kill-on-close job object when Windows
// allows it (it can refuse for a process in another session).
//
// Settings (optional): HKLM\SOFTWARE\MultiSeat\SeatService
//     AgentPath    REG_SZ  default: SeatAgent.exe next to SeatService.exe
//     SettingsDir  REG_SZ  default: C:\ProgramData\MultiSeat\agents
// Log: C:\ProgramData\MultiSeat\seatsvc.log
// State for the MultiSeat manager: <SettingsDir>\state.txt, one line per seat session:
//     local-01.json <session id> <agent pid, 0 = not running>
//
// SeatService.exe console   runs the same loop in a console window (tests; Ctrl+C stops).

#include <windows.h>
#include <wtsapi32.h>
#include <cstdio>
#include <cwchar>
#include <map>
#include <string>
#include <vector>
#include <algorithm>

static const wchar_t* const kServiceName = L"MultiSeatSeats";
static const wchar_t* const kLogFile = L"C:\\ProgramData\\MultiSeat\\seatsvc.log";
static const wchar_t* const kSessionPrefix = L"multiseat-local#";
static const ULONGLONG kRestartDelayMs = 5000;

static SERVICE_STATUS_HANDLE g_statusHandle;
static SERVICE_STATUS g_status = { SERVICE_WIN32_OWN_PROCESS, SERVICE_START_PENDING, 0, NO_ERROR, 0, 0, 0 };
static HANDLE g_stop;
static bool g_console;

static void Log(const wchar_t* format, ...)
{
    wchar_t message[1024];
    va_list args;
    va_start(args, format);
    vswprintf_s(message, format, args);
    va_end(args);

    SYSTEMTIME t;
    GetLocalTime(&t);
    wchar_t line[1200];
    swprintf_s(line, L"%04u-%02u-%02u %02u:%02u:%02u %s\r\n", t.wYear, t.wMonth, t.wDay, t.wHour, t.wMinute, t.wSecond, message);
    if (g_console) {
        fputws(line, stdout);
    }

    CreateDirectoryW(L"C:\\ProgramData\\MultiSeat", nullptr);
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
}

static std::wstring RegString(const wchar_t* name, const std::wstring& fallback)
{
    wchar_t value[MAX_PATH] = {};
    DWORD size = sizeof(value);
    if (RegGetValueW(HKEY_LOCAL_MACHINE, L"SOFTWARE\\MultiSeat\\SeatService", name, RRF_RT_REG_SZ, nullptr, value,
            &size) == ERROR_SUCCESS && value[0]) {
        return value;
    }
    return fallback;
}

// ------------------------------------------------------------------ seat sessions and agents

struct Seat
{
    std::wstring settings;     // agents\local-N.json used by this session
    HANDLE process = nullptr;  // running agent, or null
    ULONGLONG lastStart = 0;
    DWORD starts = 0;
};

class Supervisor
{
public:
    Supervisor()
    {
        wchar_t exe[MAX_PATH];
        GetModuleFileNameW(nullptr, exe, MAX_PATH);
        std::wstring dir(exe);
        dir.resize(dir.find_last_of(L'\\') + 1);
        _agent = RegString(L"AgentPath", dir + L"SeatAgent.exe");
        _settingsDir = RegString(L"SettingsDir", L"C:\\ProgramData\\MultiSeat\\agents");

        // Every agent is in this job: closing the job (service stop or crash) stops them all.
        _job = CreateJobObjectW(nullptr, nullptr);
        JOBOBJECT_EXTENDED_LIMIT_INFORMATION limits = {};
        limits.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        SetInformationJobObject(_job, JobObjectExtendedLimitInformation, &limits, sizeof(limits));
        Log(L"Agent: %s; seat settings: %s\\local-*.json", _agent.c_str(), _settingsDir.c_str());
    }

    ~Supervisor()
    {
        for (auto& [session, seat] : _seats) {
            Stop(session, seat, L"service stopping");
        }
        _seats.clear();
        WriteState(); // no seats while the service is stopped
        CloseHandle(_job);
    }

    void Tick()
    {
        std::vector<DWORD> live = SeatSessions();

        // Seat sessions that ended or disconnected: stop their agents, free their settings.
        for (auto it = _seats.begin(); it != _seats.end();) {
            if (std::find(live.begin(), live.end(), it->first) == live.end()) {
                Stop(it->first, it->second, L"seat session gone");
                it = _seats.erase(it);
            } else {
                ++it;
            }
        }

        for (DWORD session : live) {
            auto it = _seats.find(session);
            if (it == _seats.end()) {
                std::wstring settings = FreeSettings();
                if (settings.empty()) {
                    if (_warned.insert_or_assign(session, true).second) {
                        Log(L"Session %lu: no free seat settings (%s\\local-*.json)", session, _settingsDir.c_str());
                    }
                    continue;
                }
                _warned.erase(session);
                it = _seats.emplace(session, Seat{ settings }).first;
                Log(L"Session %lu: seat session found, settings %s", session, settings.c_str());
            }
            Ensure(session, it->second);
        }

        WriteState();
    }

private:
    // Rewritten only when it changes. Same folder as the seat keys: Administrators/SYSTEM only.
    void WriteState()
    {
        std::wstring text;
        for (const auto& [session, seat] : _seats) {
            const std::wstring file = seat.settings.substr(seat.settings.find_last_of(L'\\') + 1);
            const DWORD pid = seat.process && WaitForSingleObject(seat.process, 0) == WAIT_TIMEOUT ? GetProcessId(seat.process) : 0;
            text += file + L" " + std::to_wstring(session) + L" " + std::to_wstring(pid) + L"\r\n";
        }
        if (text == _state && _stateWritten) {
            return;
        }

        const std::wstring path = _settingsDir + L"\\state.txt";
        const std::wstring temp = path + L".new";
        HANDLE file = CreateFileW(temp.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (file == INVALID_HANDLE_VALUE) {
            return;
        }
        std::string ascii;  // file names and numbers only
        for (wchar_t c : text) {
            ascii += c < 128 ? static_cast<char>(c) : '?';
        }
        DWORD written = 0;
        BOOL ok = WriteFile(file, ascii.data(), static_cast<DWORD>(ascii.size()), &written, nullptr);
        CloseHandle(file);
        if (ok && MoveFileExW(temp.c_str(), path.c_str(), MOVEFILE_REPLACE_EXISTING)) {
            _state = text;
            _stateWritten = true;
        }
    }

    static std::vector<DWORD> SeatSessions()
    {
        std::vector<DWORD> result;
        WTS_SESSION_INFOW* sessions = nullptr;
        DWORD count = 0;
        if (!WTSEnumerateSessionsW(WTS_CURRENT_SERVER_HANDLE, 0, 1, &sessions, &count)) {
            return result;
        }
        const size_t prefix = wcslen(kSessionPrefix);
        for (DWORD i = 0; i < count; i++) {
            const WTS_SESSION_INFOW& s = sessions[i];
            if (s.pWinStationName && _wcsnicmp(s.pWinStationName, kSessionPrefix, prefix) == 0 &&
                (s.State == WTSActive || s.State == WTSConnected)) {
                result.push_back(s.SessionId);
            }
        }
        WTSFreeMemory(sessions);
        return result;
    }

    std::wstring FreeSettings() const
    {
        std::vector<std::wstring> files;
        WIN32_FIND_DATAW data;
        HANDLE find = FindFirstFileW((_settingsDir + L"\\local-*.json").c_str(), &data);
        if (find != INVALID_HANDLE_VALUE) {
            do {
                if (!(data.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY)) {
                    files.push_back(_settingsDir + L"\\" + data.cFileName);
                }
            } while (FindNextFileW(find, &data));
            FindClose(find);
        }
        std::sort(files.begin(), files.end());
        for (const auto& file : files) {
            bool used = std::any_of(_seats.begin(), _seats.end(), [&](const auto& s) { return s.second.settings == file; });
            if (!used) {
                return file;
            }
        }
        return {};
    }

    void Ensure(DWORD session, Seat& seat)
    {
        if (seat.process) {
            if (WaitForSingleObject(seat.process, 0) == WAIT_TIMEOUT) {
                return; // still running
            }
            DWORD code = 0;
            GetExitCodeProcess(seat.process, &code);
            CloseHandle(seat.process);
            seat.process = nullptr;
            Log(L"Session %lu: agent exited (code %lu)", session, code);
        }

        ULONGLONG now = GetTickCount64();
        if (seat.starts > 0 && now - seat.lastStart < kRestartDelayMs) {
            return;
        }
        seat.lastStart = now;
        seat.starts++;
        seat.process = Launch(session, seat.settings);
    }

    void Stop(DWORD session, Seat& seat, const wchar_t* why)
    {
        if (seat.process) {
            TerminateProcess(seat.process, 0);
            CloseHandle(seat.process);
            seat.process = nullptr;
            Log(L"Session %lu: agent stopped (%s)", session, why);
        }
    }

    // SYSTEM token moved into the seat session, then the agent on the session's Winlogon desktop. SeatAgent follows
    // the input desktop itself (sign-in screen -> user desktop -> UAC).
    HANDLE Launch(DWORD session, const std::wstring& settings)
    {
        HANDLE self = nullptr, primary = nullptr;
        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_DUPLICATE | TOKEN_QUERY, &self) ||
            !DuplicateTokenEx(self, MAXIMUM_ALLOWED, nullptr, SecurityImpersonation, TokenPrimary, &primary)) {
            Log(L"Session %lu: token duplicate failed (%lu)", session, GetLastError());
            if (self) {
                CloseHandle(self);
            }
            return nullptr;
        }
        CloseHandle(self);

        if (!SetTokenInformation(primary, TokenSessionId, &session, sizeof(session))) {
            Log(L"Session %lu: SetTokenInformation failed (%lu) - the service must run as LocalSystem", session, GetLastError());
            CloseHandle(primary);
            return nullptr;
        }

        std::wstring commandLine = L"\"" + _agent + L"\" --seat \"" + settings + L"\" --parent " +
            std::to_wstring(GetCurrentProcessId());
        std::vector<wchar_t> buffer(commandLine.begin(), commandLine.end());
        buffer.push_back(0);

        STARTUPINFOW si = {};
        si.cb = sizeof(si);
        si.lpDesktop = const_cast<wchar_t*>(L"winsta0\\winlogon");
        PROCESS_INFORMATION pi = {};
        BOOL ok = CreateProcessAsUserW(primary, _agent.c_str(), buffer.data(), nullptr, nullptr, FALSE,
            CREATE_NO_WINDOW | CREATE_SUSPENDED, nullptr, nullptr, &si, &pi);
        DWORD error = GetLastError();
        CloseHandle(primary);
        if (!ok) {
            Log(L"Session %lu: starting %s failed (%lu)", session, _agent.c_str(), error);
            return nullptr;
        }

        if (!AssignProcessToJobObject(_job, pi.hProcess)) {
            Log(L"Session %lu: agent not in job (%lu); it still exits with the service (--parent)", session, GetLastError());
        }
        ResumeThread(pi.hThread);
        CloseHandle(pi.hThread);
        Log(L"Session %lu: agent started (pid %lu)", session, pi.dwProcessId);
        return pi.hProcess;
    }

    std::wstring _agent;
    std::wstring _settingsDir;
    HANDLE _job = nullptr;
    std::map<DWORD, Seat> _seats;
    std::map<DWORD, bool> _warned;
    std::wstring _state;
    bool _stateWritten = false;
};

static void Run()
{
    Log(L"MultiSeat seat agents: started");
    {
        Supervisor supervisor;
        while (WaitForSingleObject(g_stop, 1000) == WAIT_TIMEOUT) {
            supervisor.Tick();
        }
    }
    Log(L"MultiSeat seat agents: stopped");
}

// ------------------------------------------------------------------ service plumbing

static void SetState(DWORD state)
{
    g_status.dwCurrentState = state;
    g_status.dwControlsAccepted = state == SERVICE_RUNNING ? SERVICE_ACCEPT_STOP | SERVICE_ACCEPT_SHUTDOWN : 0;
    SetServiceStatus(g_statusHandle, &g_status);
}

static DWORD WINAPI Control(DWORD control, DWORD, void*, void*)
{
    if (control == SERVICE_CONTROL_STOP || control == SERVICE_CONTROL_SHUTDOWN) {
        SetState(SERVICE_STOP_PENDING);
        SetEvent(g_stop);
    }
    return NO_ERROR;
}

static void WINAPI ServiceMain(DWORD, wchar_t**)
{
    g_statusHandle = RegisterServiceCtrlHandlerExW(kServiceName, Control, nullptr);
    if (!g_statusHandle) {
        return;
    }
    SetState(SERVICE_RUNNING);
    Run();
    SetState(SERVICE_STOPPED);
}

static BOOL WINAPI ConsoleCtrl(DWORD)
{
    SetEvent(g_stop);
    return TRUE;
}

int wmain(int argc, wchar_t** argv)
{
    g_stop = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    if (argc >= 2 && _wcsicmp(argv[1], L"console") == 0) {
        g_console = true;
        SetConsoleCtrlHandler(ConsoleCtrl, TRUE);
        Run();
        return 0;
    }

    SERVICE_TABLE_ENTRYW table[] = { { const_cast<wchar_t*>(kServiceName), ServiceMain }, { nullptr, nullptr } };
    if (!StartServiceCtrlDispatcherW(table)) {
        fputws(L"SeatService is a Windows service (MultiSeatSeats). For a test run: SeatService.exe console\n", stderr);
        return 1;
    }
    return 0;
}
