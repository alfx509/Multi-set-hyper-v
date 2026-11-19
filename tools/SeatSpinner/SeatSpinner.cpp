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

// ------------------------------------------------------------------ launch into another session (needs SYSTEM)

static int Launch(DWORD session, int seconds)
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
    wchar_t commandLine[MAX_PATH + 64];
    swprintf_s(commandLine, L"\"%s\" spin %d", exe, seconds);

    // Before sign-in the session shows the Winlogon desktop; after sign-in the user's Default desktop.
    const wchar_t* desktops[] = { L"winsta0\\winlogon", L"winsta0\\default" };
    for (const wchar_t* desktop : desktops) {
        STARTUPINFOW si = {};
        si.cb = sizeof(si);
        si.lpDesktop = const_cast<wchar_t*>(desktop);
        PROCESS_INFORMATION pi = {};
        if (CreateProcessAsUserW(primary, nullptr, commandLine, nullptr, nullptr, FALSE, CREATE_NO_WINDOW, nullptr,
                nullptr, &si, &pi)) {
            Log(L"launch: started spin in session %lu on %s (pid %lu)", session, desktop, pi.dwProcessId);
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
    if (argc >= 3 && _wcsicmp(argv[1], L"launch") == 0) {
        return Launch(static_cast<DWORD>(_wtoi(argv[2])), argc >= 4 ? _wtoi(argv[3]) : 20);
    }

    fputws(L"Usage: SeatSpinner spin [seconds] | launch <session> [seconds]\n", stdout);
    return 2;
}
