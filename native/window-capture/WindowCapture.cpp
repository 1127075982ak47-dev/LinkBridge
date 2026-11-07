#define LBWC_EXPORTS
#include "WindowCapture.h"
#include <d3d11.h>
#include <d3d11_4.h>
#include <dxgi.h>
#include <windows.graphics.capture.interop.h>
#include <windows.graphics.directx.direct3d11.interop.h>
#include <winrt/base.h>
#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.Graphics.Capture.h>
#include <winrt/Windows.Graphics.DirectX.h>
#include <winrt/Windows.Graphics.DirectX.Direct3D11.h>
#include <atomic>
#include <chrono>
#include <condition_variable>
#include <memory>
#include <mutex>
#include <thread>
#include <vector>
#include <cstring>

using namespace winrt;
using namespace winrt::Windows::Graphics::Capture;
using namespace winrt::Windows::Graphics::DirectX;
using namespace winrt::Windows::Graphics::DirectX::Direct3D11;
using winrt::Windows::Graphics::SizeInt32;

namespace {
constexpr int64_t MaximumBytes = 64LL * 1024 * 1024;
bool ValidSize(SizeInt32 size) { return size.Width > 0 && size.Height > 0 && size.Width <= 8192 &&
    size.Height <= 8192 && static_cast<int64_t>(size.Width) * size.Height * 4 <= MaximumBytes; }

struct Capture : std::enable_shared_from_this<Capture> {
    HWND hwnd{}; DWORD pid{}; LBWCFrameCallback callback{}; void* context{}; int fps{};
    std::atomic<bool> stopping{false}; std::atomic<int> status{0};
    std::thread worker; std::mutex readyMutex, frameMutex; std::condition_variable ready;
    bool initialized{}; HRESULT startResult{E_PENDING};
    HANDLE stopEvent{CreateEventW(nullptr, TRUE, FALSE, nullptr)};
    com_ptr<ID3D11Device> device; com_ptr<ID3D11DeviceContext> d3d;
    com_ptr<ID3D11Texture2D> staging;
    IDirect3DDevice runtimeDevice{nullptr}; GraphicsCaptureItem item{nullptr};
    Direct3D11CaptureFramePool pool{nullptr}; GraphicsCaptureSession session{nullptr};
    event_token frameToken{}, closedToken{}; SizeInt32 poolSize{}, stagingSize{};
    std::vector<uint8_t> pixels; int sequence{};
    std::chrono::steady_clock::time_point lastFrame{};
    ~Capture() { if (stopEvent) CloseHandle(stopEvent); }
    void CompleteStart(HRESULT result) {
        { std::lock_guard guard(readyMutex); startResult = result; initialized = true; }
        ready.notify_all();
    }
    void Run() noexcept {
        bool apartment = false;
        try {
            init_apartment(apartment_type::multi_threaded); apartment = true;
            if (!GraphicsCaptureSession::IsSupported()) throw hresult_error(E_NOTIMPL);
            check_hresult(D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr,
                D3D11_CREATE_DEVICE_BGRA_SUPPORT, nullptr, 0, D3D11_SDK_VERSION,
                device.put(), nullptr, d3d.put()));
            com_ptr<ID3D11Multithread> multithread;
            if (SUCCEEDED(d3d->QueryInterface(__uuidof(ID3D11Multithread), multithread.put_void()))) multithread->SetMultithreadProtected(TRUE);
            auto dxgi = device.as<IDXGIDevice>();
            com_ptr<IInspectable> wrapper;
            check_hresult(CreateDirect3D11DeviceFromDXGIDevice(dxgi.get(), wrapper.put()));
            runtimeDevice = wrapper.as<IDirect3DDevice>();
            auto factory = get_activation_factory<GraphicsCaptureItem, IGraphicsCaptureItemInterop>();
            check_hresult(factory->CreateForWindow(hwnd, guid_of<GraphicsCaptureItem>(), put_abi(item)));
            poolSize = item.Size();
            if (!ValidSize(poolSize)) throw hresult_error(E_INVALIDARG);
            pool = Direct3D11CaptureFramePool::CreateFreeThreaded(runtimeDevice,
                DirectXPixelFormat::B8G8R8A8UIntNormalized, 2, poolSize);
            const std::weak_ptr<Capture> weak = shared_from_this();
            frameToken = pool.FrameArrived([weak](auto const& sender, auto const&) {
                if (auto self = weak.lock()) self->OnFrame(sender);
            });
            closedToken = item.Closed([weak](auto const&, auto const&) {
                if (auto self = weak.lock()) { self->status = 1; self->stopping = true; SetEvent(self->stopEvent); }
            });
            session = pool.CreateCaptureSession(item);
            session.IsCursorCaptureEnabled(false);
            session.StartCapture();
            CompleteStart(S_OK);
            WaitForSingleObject(stopEvent, INFINITE);
        }
        catch (...) {
            auto error = to_hresult(); status = error;
            CompleteStart(error);
        }
        stopping = true;
        // Unsubscribe before closing; never hold frameMutex while Close waits for a callback.
        try { if (pool) pool.FrameArrived(frameToken); } catch (...) { }
        try { if (item) item.Closed(closedToken); } catch (...) { }
        { std::lock_guard guard(frameMutex); }
        try { if (session) session.Close(); } catch (...) { }
        try { if (pool) pool.Close(); } catch (...) { }
        session = nullptr; pool = nullptr; item = nullptr; runtimeDevice = nullptr;
        staging = nullptr; d3d = nullptr; device = nullptr;
        if (apartment) uninit_apartment();
    }
    void OnFrame(Direct3D11CaptureFramePool const& sender) noexcept {
        if (stopping) return;
        std::lock_guard guard(frameMutex);
        if (stopping) return;
        try {
            Direct3D11CaptureFrame frame{nullptr};
            while (auto next = sender.TryGetNextFrame()) { if (frame) frame.Close(); frame = std::move(next); }
            if (!frame) return;
            auto closeFrame = [&frame] { if (frame) { frame.Close(); frame = nullptr; } };
            const auto size = frame.ContentSize();
            if (!ValidSize(size) || !IsWindow(hwnd) || IsIconic(hwnd)) { closeFrame(); return; }
            DWORD currentPid{}; GetWindowThreadProcessId(hwnd, &currentPid);
            if (currentPid != pid) { closeFrame(); status = 1; stopping = true; SetEvent(stopEvent); return; }
            const bool resize = size.Width != poolSize.Width || size.Height != poolSize.Height;
            auto access = frame.Surface().as<::Windows::Graphics::DirectX::Direct3D11::IDirect3DDxgiInterfaceAccess>();
            com_ptr<ID3D11Texture2D> texture;
            check_hresult(access->GetInterface(__uuidof(ID3D11Texture2D), texture.put_void()));
            D3D11_TEXTURE2D_DESC source{}; texture->GetDesc(&source);
            const auto now = std::chrono::steady_clock::now();
            const bool due = lastFrame.time_since_epoch().count() == 0 ||
                now - lastFrame >= std::chrono::microseconds(1000000 / fps);
            if (due && static_cast<UINT>(size.Width) <= source.Width && static_cast<UINT>(size.Height) <= source.Height) {
                if (!staging || stagingSize.Width != size.Width || stagingSize.Height != size.Height) {
                    D3D11_TEXTURE2D_DESC desc{}; desc.Width = size.Width; desc.Height = size.Height;
                    desc.MipLevels = 1; desc.ArraySize = 1; desc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
                    desc.SampleDesc.Count = 1; desc.Usage = D3D11_USAGE_STAGING; desc.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
                    staging = nullptr; check_hresult(device->CreateTexture2D(&desc, nullptr, staging.put())); stagingSize = size;
                }
                D3D11_BOX valid{0, 0, 0, static_cast<UINT>(size.Width), static_cast<UINT>(size.Height), 1};
                d3d->CopySubresourceRegion(staging.get(), 0, 0, 0, 0, texture.get(), 0, &valid);
                D3D11_MAPPED_SUBRESOURCE mapped{};
                const int stride = size.Width * 4;
                pixels.resize(static_cast<size_t>(stride) * size.Height);
                check_hresult(d3d->Map(staging.get(), 0, D3D11_MAP_READ, 0, &mapped));
                for (int row = 0; row < size.Height; ++row)
                    std::memcpy(pixels.data() + static_cast<size_t>(row) * stride,
                        static_cast<uint8_t*>(mapped.pData) + static_cast<size_t>(row) * mapped.RowPitch, stride);
                d3d->Unmap(staging.get(), 0);
                ++sequence; lastFrame = now;
                auto timestamp = frame.SystemRelativeTime().count();
                callback(context, pixels.data(), size.Width, size.Height, stride, sequence, timestamp);
            }
            closeFrame();
            if (resize && !stopping) {
                sender.Recreate(runtimeDevice, DirectXPixelFormat::B8G8R8A8UIntNormalized, 2, size); poolSize = size;
            }
        }
        catch (...) { status = to_hresult(); stopping = true; SetEvent(stopEvent); }
    }
};
struct Handle { std::shared_ptr<Capture> value; };
}

HRESULT __stdcall LBWC_Start(HWND target, LBWCFrameCallback callback, void* context, int maximumFps, void** output) {
    if (!output || !callback || !IsWindow(target) || maximumFps < 1 || maximumFps > 120) return E_INVALIDARG;
    *output = nullptr;
    try {
        auto capture = std::make_shared<Capture>(); capture->hwnd = target; capture->callback = callback;
        capture->context = context; capture->fps = maximumFps; GetWindowThreadProcessId(target, &capture->pid);
        if (!capture->stopEvent) return HRESULT_FROM_WIN32(GetLastError());
        capture->worker = std::thread([capture] { capture->Run(); });
        std::unique_lock lock(capture->readyMutex);
        if (!capture->ready.wait_for(lock, std::chrono::seconds(10), [&] { return capture->initialized; })) {
            lock.unlock(); capture->stopping = true; SetEvent(capture->stopEvent); capture->worker.join(); return HRESULT_FROM_WIN32(WAIT_TIMEOUT);
        }
        const auto result = capture->startResult; lock.unlock();
        if (FAILED(result)) { capture->worker.join(); return result; }
        *output = new Handle{capture}; return S_OK;
    }
    catch (...) { return to_hresult(); }
}
HRESULT __stdcall LBWC_Stop(void* opaque) {
    if (!opaque) return S_OK;
    auto handle = static_cast<Handle*>(opaque);
    handle->value->stopping = true; SetEvent(handle->value->stopEvent);
    if (handle->value->worker.joinable()) handle->value->worker.join();
    delete handle; return S_OK;
}
HRESULT __stdcall LBWC_GetStatus(void* opaque, int* status) {
    if (!opaque || !status) return E_INVALIDARG;
    *status = static_cast<Handle*>(opaque)->value->status.load(); return S_OK;
}
