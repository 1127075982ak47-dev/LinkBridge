#include "pch.h"
#include <sddl.h>
#include <sstream>
FrameChannel::~FrameChannel() {
 SetActive(false,0,0);
 if(m_stop)CloseHandle(m_stop);
 if(m_view)UnmapViewOfFile(m_view);
 if(m_mapping)CloseHandle(m_mapping);
}
HRESULT FrameChannel::Configure(const std::wstring& sid,bool localTest) {
 PSID parsed=nullptr;if(!ConvertStringSidToSidW(sid.c_str(),&parsed))return HRESULT_FROM_WIN32(GetLastError());
 const bool valid=IsValidSid(parsed)!=FALSE;LocalFree(parsed);if(!valid)return E_INVALIDARG;
 m_sid=sid;m_mappingName=CameraMappingName(sid);m_pipeName=CameraPipeName(sid);m_localTest=localTest;return S_OK;
}
HRESULT FrameChannel::EnsureInitialized() {return m_header?S_OK:Initialize(m_sid,m_localTest);}
HRESULT FrameChannel::Initialize(const std::wstring& sid,bool localTest) {
 PSID parsed=nullptr;
 if(!ConvertStringSidToSidW(sid.c_str(),&parsed))return HRESULT_FROM_WIN32(GetLastError());
 const bool valid=IsValidSid(parsed)!=FALSE;LocalFree(parsed);if(!valid)return E_INVALIDARG;
 m_sid=sid;m_mappingName=CameraMappingName(sid);m_pipeName=CameraPipeName(sid);
 if(localTest){m_mappingName=L"Local\\LinkBridge.Camera.Test."+sid+L"."+std::to_wstring(GetCurrentProcessId());m_pipeName=L"\\\\.\\pipe\\LinkBridge.Camera.Test.Events."+sid+L"."+std::to_wstring(GetCurrentProcessId());}
 GUID instance{};RETURN_IF_FAILED(CoCreateGuid(&instance));wchar_t guid[40]{};StringFromGUID2(instance,guid,40);m_instance=guid;
 const std::wstring sddl=L"D:P(A;;GA;;;SY)(A;;GA;;;LS)(A;;GA;;;"+sid+L")";
 PSECURITY_DESCRIPTOR sd=nullptr;
 if(!ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl.c_str(),SDDL_REVISION_1,&sd,nullptr))return HRESULT_FROM_WIN32(GetLastError());
 SECURITY_ATTRIBUTES sa{sizeof(sa),sd,FALSE};
 m_mapping=CreateFileMappingW(INVALID_HANDLE_VALUE,&sa,PAGE_READWRITE,0,static_cast<DWORD>(IPC_MAPPING_BYTES),m_mappingName.c_str());
 const DWORD error=GetLastError();LocalFree(sd);if(!m_mapping)return HRESULT_FROM_WIN32(error);
 m_view=static_cast<BYTE*>(MapViewOfFile(m_mapping,FILE_MAP_ALL_ACCESS,0,0,static_cast<SIZE_T>(IPC_MAPPING_BYTES)));
 if(!m_view)return HRESULT_FROM_WIN32(GetLastError());m_header=reinterpret_cast<CameraFrameHeader*>(m_view);
 if(error!=ERROR_ALREADY_EXISTS) {
  ZeroMemory(m_view,IPC_HEADER_BYTES);memcpy(m_header->magic,"LBCAM01",8);m_header->version=1;m_header->headerBytes=IPC_HEADER_BYTES;
  m_header->width=IPC_WIDTH;m_header->height=IPC_HEIGHT;m_header->stride=IPC_STRIDE;m_header->pixelFormat=IPC_BGRA;m_header->frameBytes=IPC_FRAME_BYTES;
 }
 if(memcmp(m_header->magic,"LBCAM01",8)||m_header->version!=1||m_header->headerBytes!=IPC_HEADER_BYTES||
    m_header->width!=IPC_WIDTH||m_header->height!=IPC_HEIGHT||m_header->stride!=IPC_STRIDE||m_header->pixelFormat!=IPC_BGRA||m_header->frameBytes!=IPC_FRAME_BYTES) return HRESULT_FROM_WIN32(ERROR_INVALID_DATA);
 m_stop=CreateEventW(nullptr,TRUE,FALSE,nullptr);if(!m_stop)return HRESULT_FROM_WIN32(GetLastError());return S_OK;
}
bool FrameChannel::ReadLatest(std::vector<BYTE>& destination) {
 if(!m_header)return false;
 const auto heartbeat=InterlockedCompareExchange64(&m_header->heartbeatTickMs,0,0);
 if(heartbeat<=0||GetTickCount64()-static_cast<ULONGLONG>(heartbeat)>3000||InterlockedCompareExchange(&m_header->status,0,0)!=1)return false;
 if(m_snapshot.size()!=IPC_FRAME_BYTES)m_snapshot.resize(static_cast<size_t>(IPC_FRAME_BYTES));
 for(int attempt=0;attempt<2;attempt++) {
  const auto before=InterlockedCompareExchange64(&m_header->sequence,0,0);if(before<=0||(before&1))return false;
  const auto slot=InterlockedCompareExchange(&m_header->activeSlot,0,0);if(slot<0||slot>1)return false;
  memcpy(m_snapshot.data(),m_view+IPC_HEADER_BYTES+slot*IPC_FRAME_BYTES,static_cast<size_t>(IPC_FRAME_BYTES));MemoryBarrier();
  const auto after=InterlockedCompareExchange64(&m_header->sequence,0,0);
  if(before==after&&(after&1)==0){destination.swap(m_snapshot);return true;}
 }
 return false;
}
void FrameChannel::SetActive(bool active,uint32_t width,uint32_t height) {
 if(active) {
  m_width=width;m_height=height;
  if(m_active.exchange(true))return;
  ResetEvent(m_stop);m_worker=std::thread([this]{NotificationLoop();});
 } else {
  if(!m_active.exchange(false))return;
  SetEvent(m_stop);if(m_worker.joinable())m_worker.join();
 }
}
void FrameChannel::NotificationLoop() {
 SendNotification("start");
 while(WaitForSingleObject(m_stop,1000)==WAIT_TIMEOUT)SendNotification("heartbeat");
 SendNotification("stop");
}
void FrameChannel::SendNotification(const char* event) {
 HANDLE pipe=INVALID_HANDLE_VALUE;const auto deadline=GetTickCount64()+200;
 // Multiple sources can race the broker's short-connection accept/recreate gap.
 // WaitNamedPipe does not wait at all when no instance exists; explicitly retry that case.
 while(GetTickCount64()<deadline) {
  if(WaitNamedPipeW(m_pipeName.c_str(),30)) {
   pipe=CreateFileW(m_pipeName.c_str(),GENERIC_WRITE,0,nullptr,OPEN_EXISTING,FILE_FLAG_OVERLAPPED,nullptr);
   if(pipe!=INVALID_HANDLE_VALUE)break;
  }
  const auto error=GetLastError();if(error!=ERROR_FILE_NOT_FOUND&&error!=ERROR_PIPE_BUSY&&error!=ERROR_SEM_TIMEOUT)return;
  Sleep(10);
 }
 if(pipe==INVALID_HANDLE_VALUE)return;
 std::string instance=winrt::to_string(m_instance);
 std::ostringstream json;json<<"{\"v\":1,\"event\":\""<<event<<"\",\"instance\":\""<<instance<<"\",\"pid\":"<<GetCurrentProcessId()<<",\"width\":"<<m_width.load()<<",\"height\":"<<m_height.load()<<",\"fps\":30}\n";
 const std::string message=json.str();OVERLAPPED overlapped{};overlapped.hEvent=CreateEventW(nullptr,TRUE,FALSE,nullptr);
 if(overlapped.hEvent){DWORD written=0;if(!WriteFile(pipe,message.data(),static_cast<DWORD>(message.size()),&written,&overlapped)&&GetLastError()==ERROR_IO_PENDING){
  if(WaitForSingleObject(overlapped.hEvent,150)!=WAIT_OBJECT_0)CancelIoEx(pipe,&overlapped);GetOverlappedResult(pipe,&overlapped,&written,TRUE);
 }CloseHandle(overlapped.hEvent);}CloseHandle(pipe);
}
