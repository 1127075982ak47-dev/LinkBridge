#pragma once
#include <windows.h>
#include <unknwn.h>
#include <propvarutil.h>
#include <ole2.h>
#include <initguid.h>
#include <ks.h>
#include <ksproxy.h>
#include <ksmedia.h>
#include <mfapi.h>
#include <mfidl.h>
#include <mfobjects.h>
#include <mferror.h>
#include <mfreadwrite.h>
#include <mfvirtualcamera.h>
#include <strsafe.h>
#include <memory>
#include <vector>
#include <atomic>
#include <string>
#include <thread>
#include <winrt/base.h>
#include <wil/cppwinrt.h>
#include <wil/result.h>
#include <wil/com.h>
#include "CameraIpc.h"
#include "FrameChannel.h"
#include "SimpleFrameGenerator.h"
#include "SimpleMediaSource.h"
#include "SimpleMediaStream.h"
#include "VirtualCameraMediaSource.h"
#include "VirtualCameraMediaSourceActivate.h"
inline void DebugPrint(LPCWSTR format, ...) { wchar_t b[1024]{}; va_list a; va_start(a,format);StringCchVPrintfW(b,1024,format,a);va_end(a);OutputDebugStringW(b); }
#define DEBUG_MSG(msg,...) DebugPrint(msg, __VA_ARGS__)
namespace wilEx {
 template<class T> wil::unique_cotaskmem_array_ptr<T> make_unique_cotaskmem_array(size_t n) {
  wil::unique_cotaskmem_array_ptr<T> a;auto p=CoTaskMemAlloc(sizeof(wil::details::element_traits<T>::type)*n); if(p){ZeroMemory(p,sizeof(wil::details::element_traits<T>::type)*n);a.reset(reinterpret_cast<typename wil::details::element_traits<T>::type*>(p),n);}return a;
 }
}
namespace winrt {
 template<> bool is_guid_of<IMFMediaSourceEx>(guid const&) noexcept;
 template<> bool is_guid_of<IMFMediaStream2>(guid const&) noexcept;
 template<> bool is_guid_of<IMFActivate>(guid const&) noexcept;
}
