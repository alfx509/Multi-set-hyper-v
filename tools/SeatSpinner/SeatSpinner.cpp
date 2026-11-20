// SeatSpinner - Phase 2, experiment B3: makes a session's screen change every frame so we can measure
// how many frames per second Windows (DWM) gives that session.
//
//   SeatSpinner.exe spin [seconds]             full-screen D3D11 window, new colour every frame
//                                              (Present with vsync), logs presents per second
//   SeatSpinner.exe launch <session> [seconds] run as SYSTEM: starts "spin" inside <session> on its
//                                              current desktop (Winlogon before sign-in)
//
// Log: C:\ProgramData\MultiSeat\spinner.log. The real measurement is the remote IDD's own frame counter
// (C:\ProgramData\MultiSeat\idd.log): frames DWM actually delivered to the seat's monitor.
// TEST VM / TEST PC ONLY.

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <d3d11.h>
#include <dxgi1_2.h>
#include <wtsapi32.h>
#include <cstdio>
#include <cstdarg>
#include <cwchar>
#include <cmath>

#pragma comment(lib, "d3d11.lib")
#pragma comment(lib, "dxgi.lib")
#pragma comment(lib, "wtsapi32.lib")

static void Log(const wchar_t* format, ...)
{
    wchar_t message[512];
    va_list args;
    va_start(args, format);
    _vsnwprintf_s(message, _TRUNCATE, format, args);
    va_end(args);

    SYSTEMTIME t;
    GetLocalTime(&t);
    DWORD session = 0;
    ProcessIdToSessionId(GetCurrentProcessId(), &session);
    wchar_t line[700];
    _snwprintf_s(line, _TRUNCATE, L"%02u:%02u:%02u.%03u [pid %lu s%lu] %s\r\n", t.wHour, t.wMinute, t.wSecond,
        t.wMilliseconds, GetCurrentProcessId(), session, message);
    fputws(line, stdout);

    CreateDirectoryW(L"C:\\ProgramData\\MultiSeat", nullptr);
    HANDLE file = CreateFileW(L"C:\\ProgramData\\MultiSeat\\spinner.log", FILE_APPEND_DATA,
        FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file != INVALID_HANDLE_VALUE) {
        char utf8[1400];
        int bytes = WideCharToMultiByte(CP_UTF8, 0, line, -1, utf8, sizeof(utf8), nullptr, nullptr);
        DWORD written = 0;
        if (bytes > 1) {
            WriteFile(file, utf8, static_cast<DWORD>(bytes - 1), &written, nullptr);
        }
        CloseHandle(file);
    }
}

// ------------------------------------------------------------------ spin

static LRESULT CALLBACK WindowProc(HWND hwnd, UINT msg, WPARAM wp, LPARAM lp)
{
    if (msg == WM_DESTROY) {
        PostQuitMessage(0);
        return 0;
    }
    return DefWindowProcW(hwnd, msg, wp, lp);
}

static int Spin(int seconds)
{
    HINSTANCE instance = GetModuleHandleW(nullptr);
    WNDCLASSW wc = {};
    wc.lpfnWndProc = WindowProc;
    wc.hInstance = instance;
    wc.lpszClassName = L"SeatSpinner";
    RegisterClassW(&wc);

    int width = GetSystemMetrics(SM_CXSCREEN);
    int height = GetSystemMetrics(SM_CYSCREEN);
    HWND hwnd = CreateWindowExW(WS_EX_TOPMOST, wc.lpszClassName, L"SeatSpinner", WS_POPUP | WS_VISIBLE, 0, 0, width,
        height, nullptr, nullptr, instance, nullptr);
    if (!hwnd) {
        Log(L"spin: CreateWindow failed (%lu)", GetLastError());
        return 1;
    }

    DXGI_SWAP_CHAIN_DESC desc = {};
    desc.BufferDesc.Width = static_cast<UINT>(width);
    desc.BufferDesc.Height = static_cast<UINT>(height);
    desc.BufferDesc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
    desc.SampleDesc.Count = 1;
    desc.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
    desc.BufferCount = 2;
    desc.OutputWindow = hwnd;
    desc.Windowed = TRUE;
    desc.SwapEffect = DXGI_SWAP_EFFECT_FLIP_DISCARD;

    ID3D11Device* device = nullptr;
    ID3D11DeviceContext* context = nullptr;
    IDXGISwapChain* swapChain = nullptr;
    HRESULT hr = D3D11CreateDeviceAndSwapChain(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, 0, nullptr, 0,
        D3D11_SDK_VERSION, &desc, &swapChain, &device, nullptr, &context);
    if (FAILED(hr)) {
        Log(L"spin: D3D11CreateDeviceAndSwapChain = 0x%08X", static_cast<unsigned>(hr));
        return 1;
    }

    ID3D11Texture2D* backBuffer = nullptr;
    ID3D11RenderTargetView* target = nullptr;
    swapChain->GetBuffer(0, __uuidof(ID3D11Texture2D), reinterpret_cast<void**>(&backBuffer));
    device->CreateRenderTargetView(backBuffer, nullptr, &target);
    backBuffer->Release();

    Log(L"spin: window %dx%d, presenting with vsync for %d s", width, height, seconds);
    ULONGLONG start = GetTickCount64();
    ULONGLONG windowStart = start;
    unsigned frame = 0, framesInWindow = 0;
    MSG msg = {};
    while (GetTickCount64() - start < static_cast<ULONGLONG>(seconds) * 1000) {
        while (PeekMessageW(&msg, nullptr, 0, 0, PM_REMOVE)) {
            TranslateMessage(&msg);
            DispatchMessageW(&msg);
        }

        float t = static_cast<float>(frame++) * 0.05f;
        const float colour[4] = { 0.5f + 0.5f * sinf(t), 0.5f + 0.5f * sinf(t + 2.1f), 0.5f + 0.5f * sinf(t + 4.2f), 1.0f };
        context->OMSetRenderTargets(1, &target, nullptr);
        context->ClearRenderTargetView(target, colour);
        hr = swapChain->Present(1, 0);
        if (FAILED(hr)) {
            Log(L"spin: Present = 0x%08X", static_cast<unsigned>(hr));
            break;
        }
        framesInWindow++;

        ULONGLONG now = GetTickCount64();
        if (now - windowStart >= 5000) {
            Log(L"spin: %u presents in %llu ms = %.1f per second", framesInWindow, now - windowStart,
                framesInWindow * 1000.0 / static_cast<double>(now - windowStart));
            windowStart = now;
            framesInWindow = 0;
        }
    }

    target->Release();
    swapChain->Release();
    context->Release();
    device->Release();
    DestroyWindow(hwnd);
    Log(L"spin: done");
    return 0;
}

// ------------------------------------------------------------------ input test (B4)

// Switches to the session's current input desktop (Winlogon before sign-in), clicks the screen centre and
// types the text with SendInput. Logs how many events Windows accepted.
static int InputTest(const wchar_t* text)
{
    HDESK desktop = OpenInputDesktop(0, FALSE, GENERIC_ALL);
    if (!desktop) {
        Log(L"input: OpenInputDesktop failed (%lu)", GetLastError());
        return 1;
    }
    wchar_t name[128] = {};
    DWORD needed = 0;
    GetUserObjectInformationW(desktop, UOI_NAME, name, sizeof(name), &needed);
    if (!SetThreadDesktop(desktop)) {
        Log(L"input: SetThreadDesktop(%s) failed (%lu)", name, GetLastError());
        return 1;
    }
    Log(L"input: on desktop %s, screen %dx%d", name, GetSystemMetrics(SM_CXSCREEN), GetSystemMetrics(SM_CYSCREEN));

    INPUT click[3] = {};
    click[0].type = INPUT_MOUSE;
    click[0].mi.dx = 32768;
    click[0].mi.dy = 32768;
    click[0].mi.dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE;
    click[1].type = INPUT_MOUSE;
    click[1].mi.dwFlags = MOUSEEVENTF_LEFTDOWN;
    click[2].type = INPUT_MOUSE;
    click[2].mi.dwFlags = MOUSEEVENTF_LEFTUP;
    UINT sent = SendInput(3, click, sizeof(INPUT));
    Log(L"input: mouse move+click -> %u of 3 accepted (%lu)", sent, sent == 3 ? 0 : GetLastError());
    Sleep(1500);

    UINT keys = 0, accepted = 0;
    for (const wchar_t* c = text; *c; c++) {
        INPUT key[2] = {};
        key[0].type = key[1].type = INPUT_KEYBOARD;
        key[0].ki.wScan = key[1].ki.wScan = *c;
        key[0].ki.dwFlags = KEYEVENTF_UNICODE;
        key[1].ki.dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP;
        accepted += SendInput(2, key, sizeof(INPUT));
        keys += 2;
        Sleep(60);
    }
    Log(L"input: typed \"%s\" -> %u of %u key events accepted", text, accepted, keys);
    return 0;
}

// Sign-in test (B4): reads "user\npassword" from a file only Administrators/SYSTEM can read, types them into the
// seat's sign-in screen (user name box has focus), Tab between them, Enter at the end. Never logs the text.
static UINT TypeText(const wchar_t* text)
{
    UINT accepted = 0;
    for (const wchar_t* c = text; *c; c++) {
        INPUT key[2] = {};
        key[0].type = key[1].type = INPUT_KEYBOARD;
        key[0].ki.wScan = key[1].ki.wScan = *c;
        key[0].ki.dwFlags = KEYEVENTF_UNICODE;
        key[1].ki.dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP;
        accepted += SendInput(2, key, sizeof(INPUT));
        Sleep(80);
    }
    return accepted;
}

static UINT PressKey(WORD vk)
{
    INPUT key[2] = {};
    key[0].type = key[1].type = INPUT_KEYBOARD;
    key[0].ki.wVk = key[1].ki.wVk = vk;
    key[1].ki.dwFlags = KEYEVENTF_KEYUP;
    return SendInput(2, key, sizeof(INPUT));
}

static int SignInTest(const wchar_t* path)
{
    char buffer[512] = {};
    HANDLE file = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, 0, nullptr);
    DWORD read = 0;
    if (file == INVALID_HANDLE_VALUE || !ReadFile(file, buffer, sizeof(buffer) - 1, &read, nullptr)) {
        Log(L"signin: cannot read the credential file (%lu)", GetLastError());
        if (file != INVALID_HANDLE_VALUE) {
            CloseHandle(file);
        }
        return 1;
    }
    CloseHandle(file);

    wchar_t text[512] = {};
    MultiByteToWideChar(CP_ACP, 0, buffer, -1, text, 512);
    SecureZeroMemory(buffer, sizeof(buffer));
    wchar_t* context = nullptr;
    wchar_t* user = wcstok_s(text, L"\r\n", &context);
    wchar_t* password = wcstok_s(nullptr, L"\r\n", &context);
    if (!user || !password) {
        Log(L"signin: the file needs two lines (user, password)");
        return 1;
    }

    HDESK desktop = OpenInputDesktop(0, FALSE, GENERIC_ALL);
    if (!desktop || !SetThreadDesktop(desktop)) {
        Log(L"signin: cannot attach to the input desktop (%lu)", GetLastError());
        return 1;
    }

    // B4: typing right after Tab lost characters (logon failed with 0xC000006A, wrong password), so give the
    // sign-in screen time to move the focus.
    UINT u = TypeText(user);
    Sleep(400);
    UINT tab = PressKey(VK_TAB);
    Sleep(800);
    UINT p = TypeText(password);
    Sleep(400);
    UINT enter = PressKey(VK_RETURN);
    Log(L"signin: user %zu chars (%u events), tab %u, password %zu chars (%u events), enter %u", wcslen(user), u, tab,
        wcslen(password), p, enter);
    SecureZeroMemory(text, sizeof(text));
    return 0;
}

// Moves the mouse 1 pixel and back on the session's current input desktop. A still screen sends no frames;
// this forces one, so a snapshot can be taken without changing what is shown.
static int Nudge()
{
    HDESK desktop = OpenInputDesktop(0, FALSE, GENERIC_ALL);
    if (!desktop || !SetThreadDesktop(desktop)) {
        Log(L"nudge: cannot attach to the input desktop (%lu)", GetLastError());
        return 1;
    }
    wchar_t name[128] = {};
    DWORD needed = 0;
    GetUserObjectInformationW(desktop, UOI_NAME, name, sizeof(name), &needed);
    for (int i = 0; i < 6; i++) {
        INPUT move = {};
        move.type = INPUT_MOUSE;
        move.mi.dx = (i % 2) ? -1 : 1;
        move.mi.dwFlags = MOUSEEVENTF_MOVE;
        SendInput(1, &move, sizeof(INPUT));
        Sleep(150);
    }
    Log(L"nudge: mouse moved on desktop %s", name);
    return 0;
}

// ------------------------------------------------------------------ launch into another session (needs SYSTEM)

static int Launch(DWORD session, const wchar_t* childArgs, const wchar_t* otherExe = nullptr)
{
    HANDLE self = nullptr, primary = nullptr;
    if (!OpenProcessToken(GetCurrentProcess(), TOKEN_DUPLICATE | TOKEN_QUERY, &self) ||
        !DuplicateTokenEx(self, MAXIMUM_ALLOWED, nullptr, SecurityImpersonation, TokenPrimary, &primary)) {
        Log(L"launch: token duplicate failed (%lu)", GetLastError());
        return 1;
    }
    CloseHandle(self);

    if (!SetTokenInformation(primary, TokenSessionId, &session, sizeof(session))) {
        Log(L"launch: SetTokenInformation(session %lu) failed (%lu) - run as SYSTEM", session, GetLastError());
        CloseHandle(primary);
        return 1;
    }

    wchar_t exe[MAX_PATH];
    GetModuleFileNameW(nullptr, exe, MAX_PATH);
    wchar_t commandLine[MAX_PATH + 256];
    if (otherExe) {
        swprintf_s(commandLine, L"\"%s\" %s", otherExe, childArgs);   // run: any program in the session
    } else {
        swprintf_s(commandLine, L"\"%s\" %s", exe, childArgs);
    }

    // Before sign-in the session shows the Winlogon desktop; after sign-in the user's Default desktop.
    const wchar_t* desktops[] = { L"winsta0\\winlogon", L"winsta0\\default" };
    for (const wchar_t* desktop : desktops) {
        STARTUPINFOW si = {};
        si.cb = sizeof(si);
        si.lpDesktop = const_cast<wchar_t*>(desktop);
        PROCESS_INFORMATION pi = {};
        if (CreateProcessAsUserW(primary, nullptr, commandLine, nullptr, nullptr, FALSE, CREATE_NO_WINDOW, nullptr,
                nullptr, &si, &pi)) {
            Log(L"launch: started \"%s\" in session %lu on %s (pid %lu)", childArgs, session, desktop, pi.dwProcessId);
            CloseHandle(pi.hThread);
            CloseHandle(pi.hProcess);
            CloseHandle(primary);
            return 0;
        }
        Log(L"launch: CreateProcessAsUser on %s failed (%lu)", desktop, GetLastError());
    }

    CloseHandle(primary);
    return 1;
}

int wmain(int argc, wchar_t** argv)
{
    if (argc >= 2 && _wcsicmp(argv[1], L"spin") == 0) {
        return Spin(argc >= 3 ? _wtoi(argv[2]) : 20);
    }
    if (argc >= 3 && _wcsicmp(argv[1], L"input") == 0) {
        return InputTest(argv[2]);
    }
    if (argc >= 3 && _wcsicmp(argv[1], L"signin") == 0) {
        return SignInTest(argv[2]);
    }
    if (argc >= 2 && _wcsicmp(argv[1], L"nudge") == 0) {
        return Nudge();
    }
    if (argc >= 4 && _wcsicmp(argv[1], L"run") == 0) {
        // run <session> <exe> [one argument string]  -> start any program inside the session as SYSTEM
        wchar_t rest[512] = {};
        for (int i = 4; i < argc; i++) {
            wcscat_s(rest, i > 4 ? L" " : L"");
            wcscat_s(rest, argv[i]);
        }
        return Launch(static_cast<DWORD>(_wtoi(argv[2])), rest, argv[3]);
    }
    if (argc >= 3 && _wcsicmp(argv[1], L"launch") == 0) {
        // launch <session> [seconds]            -> spin
        // launch <session> spin|input <arg>     -> that mode inside the session
        wchar_t childArgs[256];
        if (argc >= 5) {
            swprintf_s(childArgs, L"%s %s", argv[3], argv[4]);
        } else {
            swprintf_s(childArgs, L"spin %d", argc >= 4 ? _wtoi(argv[3]) : 20);
        }
        return Launch(static_cast<DWORD>(_wtoi(argv[2])), childArgs);
    }

    fputws(L"Usage: SeatSpinner spin [seconds] | input <text> | launch <session> [seconds | spin <s> | input <text>]\n", stdout);
    return 2;
}
