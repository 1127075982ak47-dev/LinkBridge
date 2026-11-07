#pragma once
#include <windows.h>
#include <cstdint>
// The BGRA pointer is valid only until the callback returns. Rows are tightly packed, top-down.
using LBWCFrameCallback = void(__stdcall*)(void* context, const uint8_t* pixels, int width, int height,
                                         int stride, int sequence, int64_t timestamp100ns);
#ifdef LBWC_EXPORTS
#define LBWC_API extern "C" __declspec(dllexport)
#else
#define LBWC_API extern "C" __declspec(dllimport)
#endif
LBWC_API HRESULT __stdcall LBWC_Start(HWND target, LBWCFrameCallback callback, void* context, int maximumFps, void** capture);
LBWC_API HRESULT __stdcall LBWC_Stop(void* capture);
LBWC_API HRESULT __stdcall LBWC_GetStatus(void* capture, int* status);
