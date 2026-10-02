#pragma once
#include "CameraIpc.h"
#include <memory>
#include <vector>
#include <atomic>
#include <thread>
class FrameChannel {
public:
 ~FrameChannel();
 HRESULT Configure(const std::wstring& userSid, bool localTest=false);
 HRESULT EnsureInitialized();
 HRESULT Initialize(const std::wstring& userSid, bool localTest=false);
 bool ReadLatest(std::vector<BYTE>& destination);
 void SetActive(bool active, uint32_t width, uint32_t height);
 const std::wstring& MappingName() const {return m_mappingName;}
 CameraFrameHeader* HeaderForTest() const {return m_header;}
 BYTE* SlotForTest(int index) const {return m_view+IPC_HEADER_BYTES+index*IPC_FRAME_BYTES;}
private:
 void NotificationLoop();
 void SendNotification(const char* event);
 std::wstring m_sid,m_mappingName,m_pipeName,m_instance;
 HANDLE m_mapping=nullptr,m_stop=nullptr;
 BYTE* m_view=nullptr; CameraFrameHeader* m_header=nullptr;
 std::atomic<bool> m_active{false}; std::atomic<uint32_t> m_width{3840},m_height{2160};
 std::thread m_worker;
 std::vector<BYTE> m_snapshot;
 bool m_localTest=false;
};
