#include <windows.h>
#include <string>
namespace {
HWND editor{}, instructions{};
HFONT font{};
WNDPROC originalEditor{};
LRESULT CALLBACK EditorProcedure(HWND hwnd, UINT message, WPARAM wp, LPARAM lp) {
    // Standard edit-command behavior for the disposable test app, driven by real received keys.
    if (message == WM_KEYDOWN && wp == 'A' && (GetKeyState(VK_CONTROL) & 0x8000)) {
        SendMessageW(hwnd, EM_SETSEL, 0, -1); return 0;
    }
    return CallWindowProcW(originalEditor, hwnd, message, wp, lp);
}
LRESULT CALLBACK WindowProcedure(HWND hwnd, UINT message, WPARAM wp, LPARAM lp) {
    if (message == WM_CREATE) {
        font = CreateFontW(-20, 0, 0, 0, FW_NORMAL, FALSE, FALSE, FALSE, DEFAULT_CHARSET,
            OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, CLEARTYPE_QUALITY, DEFAULT_PITCH, L"Segoe UI");
        instructions = CreateWindowW(L"STATIC", L"Disposable test window · drag its title bar to Mac · type, select, copy and paste here",
            WS_CHILD | WS_VISIBLE, 18, 18, 750, 48, hwnd, nullptr, GetModuleHandleW(nullptr), nullptr);
        editor = CreateWindowExW(WS_EX_CLIENTEDGE, L"EDIT",
            L"LinkBridge Window Roaming Test\r\n\r\nThis is temporary test content, not a user document.\r\n"
            L"Use Ctrl+A, Ctrl+C, Ctrl+V, arrows, mouse selection, and the wheel.\r\n\r\n"
            L"中文和 emoji 🐈 可以在这里验证。\r\n",
            WS_CHILD | WS_VISIBLE | WS_TABSTOP | WS_VSCROLL | ES_MULTILINE | ES_AUTOVSCROLL | ES_WANTRETURN | ES_NOHIDESEL,
            18, 78, 750, 420, hwnd, nullptr, GetModuleHandleW(nullptr), nullptr);
        SendMessageW(instructions, WM_SETFONT, reinterpret_cast<WPARAM>(font), TRUE);
        SendMessageW(editor, WM_SETFONT, reinterpret_cast<WPARAM>(font), TRUE);
        std::wstring initial = L"LinkBridge Window Roaming Test\r\n\r\n"
            L"This is temporary test content, not a user document.\r\n"
            L"Use Ctrl+A, Ctrl+C, Ctrl+V, arrows, mouse selection, and the wheel.\r\n"
            L"中文和 emoji 🐈 可以在这里验证。\r\n\r\n";
        for (int line = 1; line <= 60; ++line)
            initial += L"Scroll verification line " + std::to_wstring(line) + L" · temporary fixture text\r\n";
        SetWindowTextW(editor, initial.c_str());
        originalEditor = reinterpret_cast<WNDPROC>(SetWindowLongPtrW(editor, GWLP_WNDPROC, reinterpret_cast<LONG_PTR>(EditorProcedure)));
        return 0;
    }
    if (message == WM_SIZE) {
        const int width = LOWORD(lp), height = HIWORD(lp);
        MoveWindow(instructions, 18, 18, width > 36 ? width - 36 : 1, 48, TRUE);
        MoveWindow(editor, 18, 78, width > 36 ? width - 36 : 1, height > 96 ? height - 96 : 1, TRUE);
        return 0;
    }
    if (message == WM_SETFOCUS) { SetFocus(editor); return 0; }
    if (message == WM_DESTROY) { if (font) DeleteObject(font); PostQuitMessage(0); return 0; }
    return DefWindowProcW(hwnd, message, wp, lp);
}
}
int WINAPI wWinMain(HINSTANCE instance, HINSTANCE, PWSTR, int show) {
    SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
    WNDCLASSW wc{}; wc.hInstance = instance; wc.lpfnWndProc = WindowProcedure;
    wc.lpszClassName = L"LinkBridge.WindowRoaming.Fixture"; wc.hCursor = LoadCursorW(nullptr, IDC_ARROW);
    wc.hbrBackground = reinterpret_cast<HBRUSH>(COLOR_WINDOW + 1);
    RegisterClassW(&wc);
    // Put our disposable fixture at the primary right edge. This lets bounded UI
    // automation reach the physical edge while dragging only this owned test window.
    const int startX = (std::max)(0, GetSystemMetrics(SM_CXSCREEN) - 780);
    const auto hwnd = CreateWindowW(wc.lpszClassName, L"LinkBridge Window Roaming Test", WS_OVERLAPPEDWINDOW,
        startX, 240, 800, 560, nullptr, nullptr, instance, nullptr);
    if (!hwnd) return 1;
    ShowWindow(hwnd, show); UpdateWindow(hwnd); SetFocus(editor);
    MSG message{};
    while (GetMessageW(&message, nullptr, 0, 0) > 0) {
        TranslateMessage(&message); DispatchMessageW(&message);
    }
    return 0;
}
