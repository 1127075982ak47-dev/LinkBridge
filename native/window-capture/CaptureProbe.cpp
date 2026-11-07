#include "WindowCapture.h"
#include <windows.h>
#include <dwmapi.h>
#include <atomic>
#include <chrono>
#include <cstdio>
#include <vector>
std::atomic<int> frames{0}, lastWidth{0}, lastHeight{0};
std::atomic<bool> pattern{false};
void __stdcall Frame(void*, const uint8_t* pixels, int w, int h, int stride, int, int64_t) {
    lastWidth = w; lastHeight = h; ++frames;
    int red = 0, blue = 0;
    for (int y = 0; y < h; ++y) for (int x = 0; x < w; ++x) {
        auto p = pixels + y * stride + x * 4;
        if (p[2] > 220 && p[1] < 40 && p[0] < 40) ++red;
        if (p[0] > 220 && p[1] < 40 && p[2] < 40) ++blue;
    }
    if (red > 100 && blue > 100) pattern = true;
}
LRESULT CALLBACK WindowProcedure(HWND hwnd, UINT message, WPARAM wp, LPARAM lp) {
    if (message == WM_PAINT) {
        PAINTSTRUCT ps{}; auto dc = BeginPaint(hwnd, &ps); RECT client{}; GetClientRect(hwnd, &client);
        auto red = CreateSolidBrush(RGB(255, 0, 0)), blue = CreateSolidBrush(RGB(0, 0, 255));
        RECT left = client, right = client; left.right /= 2; right.left = left.right;
        FillRect(dc, &left, red); FillRect(dc, &right, blue); DeleteObject(red); DeleteObject(blue); EndPaint(hwnd, &ps); return 0;
    }
    return DefWindowProcW(hwnd, message, wp, lp);
}
int wmain() {
    SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
    WNDCLASSW wc{}; wc.hInstance = GetModuleHandleW(nullptr); wc.lpfnWndProc = WindowProcedure;
    wc.lpszClassName = L"LinkBridge.WindowCapture.Probe"; RegisterClassW(&wc);
    auto hwnd = CreateWindowW(wc.lpszClassName, L"LinkBridge capture API fixture", WS_OVERLAPPEDWINDOW | WS_VISIBLE,
        40, 40, 320, 240, nullptr, nullptr, wc.hInstance, nullptr);
    UpdateWindow(hwnd);
    void* capture{}; auto hr = LBWC_Start(hwnd, Frame, nullptr, 30, &capture);
    if (FAILED(hr)) { std::printf("FAIL native start HRESULT=%08lx\n", static_cast<unsigned long>(hr)); DestroyWindow(hwnd); return 1; }
    const auto deadline = GetTickCount64() + 8000;
    bool resized = false;
    while (GetTickCount64() < deadline) {
        MSG msg{}; while (PeekMessageW(&msg, nullptr, 0, 0, PM_REMOVE)) { TranslateMessage(&msg); DispatchMessageW(&msg); }
        if (pattern && !resized) { SetWindowPos(hwnd, nullptr, 0, 0, 420, 280, SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE); InvalidateRect(hwnd, nullptr, TRUE); resized = true; }
        if (resized && lastWidth >= 400 && lastHeight >= 260 && frames >= 2) break;
        Sleep(10);
    }
    RECT bounds{}; DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, &bounds, sizeof(bounds));
    int status{}; LBWC_GetStatus(capture, &status); LBWC_Stop(capture); DestroyWindow(hwnd);
    std::printf("WGC hwnd fixture frames=%d pixels=%dx%d DWM=%ldx%ld pattern=%d resize=%d status=%d\n", frames.load(), lastWidth.load(), lastHeight.load(), bounds.right - bounds.left, bounds.bottom - bounds.top, pattern.load(), resized, status);
    return pattern && resized && lastWidth >= 400 && frames >= 2 && status == 0 ? 0 : 2;
}
