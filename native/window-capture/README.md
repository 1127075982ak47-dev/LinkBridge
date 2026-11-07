# LinkBridge per-window capture bridge

This is an original C++/WinRT implementation of a small Win32 ABI for Windows Graphics Capture.
Conceptual reference: Microsoft's MIT-licensed `ScreenCaptureforHWND` sample, pinned to commit
[`ee50e2ea137dcef7b82ba504eff7435e5ebf5294`](https://github.com/microsoft/Windows.UI.Composition-Win32-Samples/tree/ee50e2ea137dcef7b82ba504eff7435e5ebf5294/cpp/ScreenCaptureforHWND).
No sample implementation source is copied into this module. The upstream reference license is retained
in `REFERENCE-LICENSE-Microsoft.txt`; LinkBridge's own implementation uses the repository license.

## Build

From the repository root, with Visual Studio 2022 C++ Build Tools, CMake, and Windows SDK 10.0.26100:

```powershell
pwsh -NoProfile -File tools/build-window-capture.ps1
pwsh -NoProfile -File tools/build-window-capture.ps1 -RunCaptureProbe
```

The output is `artifacts/window-capture/LinkBridge.WindowCapture.dll`. Static CRT linking avoids an
additional VC redistributable install. The DLL uses Windows system D3D11/DXGI/WinRT APIs.

`LBWC_Start` binds one HWND and source PID, creates a private MTA worker and free-threaded WGC frame
pool, and disables captured cursor drawing. Frame callback bytes are tightly packed, top-down SDR
BGRA8; width/height are the valid `ContentSize`, stride is width × 4, and timestamp is WGC QPC-based
100 ns time. The pointer is valid only while the callback runs. Resize recreates the frame pool and
readback texture. No capture frame references survive callback processing.

The callback must only copy/queue; native shutdown waits for active callbacks. C# exposes an owned
pooled `WindowCaptureFrame` lease. Its sole consumer disposes superseded and encoded frames.
Closed windows or device errors are reported via status; minimized windows do not produce usable
frames. Source windows remain normally drawn. HDR/protected/elevated content is outside this first
SDR proxy scope.

## Executed native capture probe

On 2026-10-02, SDK 10.0.26100, MSVC 19.44.35229, the native probe created its own visible HWND,
painted known red/blue regions, captured real WGC pixels, resized its own window, and confirmed:

```text
WGC hwnd fixture frames=2 pixels=402x271 DWM=402x271 pattern=1 resize=1 status=0
```

WGC may emit only changed frames; two valid initial/resize frames are sufficient for this check.
This is an API fixture, not a GUI automation driver. It never invokes `SendInput` or manipulates
another application's window. It does not prove the Mac proxy's real gestures or editor/browser
compatibility; those remain integration acceptance checks.

Native DLL SHA-256 after this check:
`D539A8B8513F41F2C56084B2A67585E9345088E692C6FFE45BB15627A9364487`.
