#pragma once
#include <windows.h>
#include <cstdint>
#include <string>
// LinkBridge-owned attribute; persistent virtual-camera configuration passes the target user's SID to FrameServer.
inline constexpr GUID LINKBRIDGE_CAMERA_USER_SID{0x7d7a77ea,0xbe87,0x42ed,{0x91,0x71,0x1b,0x3a,0x45,0x7d,0x06,0xc7}};
// Used only by the isolated in-process probe, never set by the registration helper.
inline constexpr GUID LINKBRIDGE_CAMERA_LOCAL_TEST{0xc5b9d8b2,0x2702,0x40fb,{0xbd,0x2b,0x24,0x53,0x11,0xca,0x45,0x80}};
inline constexpr wchar_t CAMERA_CLSID[]=L"{0DF6F8B7-A2E5-4F7B-9CCD-8AB144574F1E}";
inline constexpr wchar_t CAMERA_NAME[]=L"联桥共享摄像头";
inline constexpr uint32_t IPC_WIDTH=3840, IPC_HEIGHT=2160, IPC_STRIDE=15360, IPC_HEADER_BYTES=4096;
inline constexpr uint64_t IPC_FRAME_BYTES=33177600, IPC_MAPPING_BYTES=IPC_HEADER_BYTES+IPC_FRAME_BYTES*2;
inline constexpr uint32_t IPC_BGRA=0x41524742;
struct alignas(64) CameraFrameHeader {
 char magic[8]; // LBCAM01 followed by NUL
 uint32_t version; // offset 8
 uint32_t headerBytes; // 12
 uint32_t width; // 16
 uint32_t height; // 20
 uint32_t stride; // 24
 uint32_t pixelFormat; // 28
 uint64_t frameBytes; // 32
 volatile LONG64 sequence; // 40, producer seqlock: odd means write in progress
 volatile LONG activeSlot; // 48, 0 or 1
 volatile LONG status; // 52, 0 offline, 1 live
 volatile LONG64 timestamp100ns; // 56, QPC converted to 100ns
 volatile LONG64 producerPid; // 64
 volatile LONG64 heartbeatTickMs; // 72, GetTickCount64/Environment.TickCount64
 uint8_t reserved[4016];
};
static_assert(sizeof(CameraFrameHeader)==4096);
static_assert(offsetof(CameraFrameHeader,sequence)==40);
static_assert(offsetof(CameraFrameHeader,activeSlot)==48);
inline std::wstring CameraMappingName(const std::wstring& sid) {return L"Global\\LinkBridge.Camera.Frame.v1."+sid;}
inline std::wstring CameraPipeName(const std::wstring& sid) {return L"\\\\.\\pipe\\LinkBridge.Camera.Events.v1."+sid;}
