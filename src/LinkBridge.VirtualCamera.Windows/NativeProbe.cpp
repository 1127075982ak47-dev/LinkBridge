#include "pch.h"
#include <sddl.h>
#include <iostream>
#include <chrono>
#include <cassert>
#include <mutex>
#include <set>
std::wstring TestSid() {HANDLE token{};winrt::check_bool(OpenProcessToken(GetCurrentProcess(),TOKEN_QUERY,&token));DWORD n{};GetTokenInformation(token,TokenUser,nullptr,0,&n);std::vector<BYTE>b(n);winrt::check_bool(GetTokenInformation(token,TokenUser,b.data(),n,&n));CloseHandle(token);PWSTR s{};winrt::check_bool(ConvertSidToStringSidW(reinterpret_cast<TOKEN_USER*>(b.data())->User.Sid,&s));std::wstring sid=s;LocalFree(s);return sid;}
void Check(bool yes,const char* message){if(!yes)throw std::runtime_error(message);}
int main()try {
 winrt::check_hresult(CoInitializeEx(nullptr,COINIT_MULTITHREADED));winrt::check_hresult(MFStartup(MF_VERSION));
 auto dll=LoadLibraryW(L"LinkBridgeCameraSourceV2.dll");Check(dll!=nullptr,"load x64 native source DLL");
 auto getClass=reinterpret_cast<HRESULT(__stdcall*)(REFCLSID,REFIID,void**)>(GetProcAddress(dll,"DllGetClassObject"));Check(getClass!=nullptr,"COM export exists");
 GUID clsid{};winrt::check_hresult(CLSIDFromString(CAMERA_CLSID,&clsid));wil::com_ptr_nothrow<IClassFactory> factory;
 winrt::check_hresult(getClass(clsid,IID_PPV_ARGS(&factory)));wil::com_ptr_nothrow<IMFActivate> activation;
 winrt::check_hresult(factory->CreateInstance(nullptr,IID_PPV_ARGS(&activation)));winrt::check_hresult(activation->SetString(LINKBRIDGE_CAMERA_USER_SID,TestSid().c_str()));winrt::check_hresult(activation->SetUINT32(LINKBRIDGE_CAMERA_LOCAL_TEST,1));
 wil::com_ptr_nothrow<IMFMediaSource> source;winrt::check_hresult(activation->ActivateObject(IID_PPV_ARGS(&source)));
 wil::com_ptr_nothrow<IMFPresentationDescriptor> descriptor;winrt::check_hresult(source->CreatePresentationDescriptor(&descriptor));
 BOOL selected{};wil::com_ptr_nothrow<IMFStreamDescriptor> streamDescriptor;winrt::check_hresult(descriptor->GetStreamDescriptorByIndex(0,&selected,&streamDescriptor));
 wil::com_ptr_nothrow<IMFMediaTypeHandler> types;winrt::check_hresult(streamDescriptor->GetMediaTypeHandler(&types));DWORD typeCount{};winrt::check_hresult(types->GetMediaTypeCount(&typeCount));Check(typeCount==2,"two public camera formats");
 for(DWORD i=0;i<typeCount;i++){wil::com_ptr_nothrow<IMFMediaType> declared;winrt::check_hresult(types->GetMediaTypeByIndex(i,&declared));UINT32 w{},h{},fps{},denom{};winrt::check_hresult(MFGetAttributeSize(declared.get(),MF_MT_FRAME_SIZE,&w,&h));winrt::check_hresult(MFGetAttributeRatio(declared.get(),MF_MT_FRAME_RATE,&fps,&denom));Check(w==(i?1920u:3840u)&&h==(i?1080u:2160u)&&fps==30&&denom==1,"actual COM descriptor format");std::cout<<"PASS COM public format "<<w<<"x"<<h<<" @ "<<fps<<"fps HRESULT=S_OK\n";}
 std::atomic<bool> collecting{true};std::mutex messagesLock;std::vector<std::string> messages;
 const auto testPipe=L"\\\\.\\pipe\\LinkBridge.Camera.Test.Events."+TestSid()+L"."+std::to_wstring(GetCurrentProcessId());
 std::jthread collector([&](std::stop_token stopToken){while(collecting&&!stopToken.stop_requested()){HANDLE pipe=CreateNamedPipeW(testPipe.c_str(),PIPE_ACCESS_INBOUND|FILE_FLAG_OVERLAPPED,PIPE_TYPE_BYTE|PIPE_WAIT,8,4096,4096,100,nullptr);if(pipe==INVALID_HANDLE_VALUE)return;
  OVERLAPPED op{};op.hEvent=CreateEventW(nullptr,TRUE,FALSE,nullptr);BOOL connected=ConnectNamedPipe(pipe,&op);DWORD error=GetLastError();
  if(!connected&&error==ERROR_IO_PENDING){while(collecting&&!stopToken.stop_requested()&&WaitForSingleObject(op.hEvent,10)==WAIT_TIMEOUT){}if(!collecting||stopToken.stop_requested()){CancelIoEx(pipe,&op);DWORD ignored{};GetOverlappedResult(pipe,&op,&ignored,TRUE);}else{DWORD ignored{};connected=GetOverlappedResult(pipe,&op,&ignored,TRUE);}}
  else if(error==ERROR_PIPE_CONNECTED)connected=TRUE;
  if(connected&&collecting){char b[4096]{};DWORD got{};ResetEvent(op.hEvent);BOOL read=ReadFile(pipe,b,sizeof(b),&got,&op);if(!read&&GetLastError()==ERROR_IO_PENDING){while(collecting&&!stopToken.stop_requested()&&WaitForSingleObject(op.hEvent,10)==WAIT_TIMEOUT){}if(!collecting||stopToken.stop_requested())CancelIoEx(pipe,&op);read=GetOverlappedResult(pipe,&op,&got,TRUE);}if(read&&got){std::lock_guard guard(messagesLock);messages.emplace_back(b,got);}}
  CloseHandle(op.hEvent);CloseHandle(pipe);
 }});
 std::this_thread::sleep_for(std::chrono::milliseconds(30));
 wil::com_ptr_nothrow<IMFSampleAllocatorControl> allocatorControl;winrt::check_hresult(source->QueryInterface(IID_PPV_ARGS(&allocatorControl)));
 wil::com_ptr_nothrow<IMFVideoSampleAllocator> allocator;winrt::check_hresult(MFCreateVideoSampleAllocatorEx(IID_PPV_ARGS(&allocator)));winrt::check_hresult(allocatorControl->SetDefaultAllocator(0,allocator.get()));
 winrt::check_hresult(descriptor->SelectStream(0));PROPVARIANT position{};PropVariantInit(&position);winrt::check_hresult(source->Start(descriptor.get(),nullptr,&position));
 wil::com_ptr_nothrow<IMFMediaStream> actualStream;
 for(int i=0;i<4;i++){wil::com_ptr_nothrow<IMFMediaEvent> event;winrt::check_hresult(source->GetEvent(MF_EVENT_FLAG_NO_WAIT,&event));MediaEventType kind{};event->GetType(&kind);if(kind==MENewStream||kind==MEUpdatedStream){wil::unique_prop_variant value;event->GetValue(&value);winrt::check_hresult(value.punkVal->QueryInterface(IID_PPV_ARGS(&actualStream)));break;}}
 Check(actualStream!=nullptr,"real IMF stream announced");
 auto actualChannel=std::make_shared<FrameChannel>();winrt::check_hresult(actualChannel->Initialize(TestSid(),true));auto actualHeader=actualChannel->HeaderForTest();auto pixels=actualChannel->SlotForTest(1);
 InterlockedExchange64(&actualHeader->sequence,1);for(size_t i=0;i<IPC_FRAME_BYTES;i+=4){pixels[i]=31;pixels[i+1]=82;pixels[i+2]=143;pixels[i+3]=255;}pixels[0]=2;pixels[2159*15360+3839*4]=222;InterlockedExchange(&actualHeader->activeSlot,1);InterlockedExchange(&actualHeader->status,1);InterlockedExchange64(&actualHeader->heartbeatTickMs,GetTickCount64());MemoryBarrier();InterlockedExchange64(&actualHeader->sequence,2);
 LONGLONG previousTime=0;auto sampleClock=std::chrono::steady_clock::now();
 for(int n=0;n<8;n++) {winrt::check_hresult(actualStream->RequestSample(nullptr));wil::com_ptr_nothrow<IMFSample> sample;
  for(int j=0;j<4;j++){wil::com_ptr_nothrow<IMFMediaEvent> event;winrt::check_hresult(actualStream->GetEvent(MF_EVENT_FLAG_NO_WAIT,&event));MediaEventType kind{};event->GetType(&kind);if(kind==MEMediaSample){wil::unique_prop_variant value;event->GetValue(&value);winrt::check_hresult(value.punkVal->QueryInterface(IID_PPV_ARGS(&sample)));break;}}
  Check(sample!=nullptr,"actual MEMediaSample event");LONGLONG timestamp{},duration{};sample->GetSampleTime(&timestamp);sample->GetSampleDuration(&duration);Check(timestamp>previousTime&&duration==333333,"monotonic 30fps sample timing");previousTime=timestamp;
  wil::com_ptr_nothrow<IMFMediaBuffer> buffer;sample->GetBufferByIndex(0,&buffer);wil::com_ptr_nothrow<IMF2DBuffer2> surface;winrt::check_hresult(buffer->QueryInterface(IID_PPV_ARGS(&surface)));BYTE* image{};BYTE* base{};LONG pitch{};DWORD bytes{};winrt::check_hresult(surface->Lock2DSize(MF2DBuffer_LockFlags_Read,&image,&pitch,&base,&bytes));
  const bool real=pitch>=15360&&bytes>=IPC_FRAME_BYTES&&image[0]==2&&image[1]==82&&image[2]==143&&image[pitch*2159+3839*4]==222;surface->Unlock2D();Check(real,"actual 4K MFSample carries shared pixels at first/last pixel");
 }
 Check(std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::steady_clock::now()-sampleClock).count()>=200,"RequestSample must pace real output to 30fps");
 std::this_thread::sleep_for(std::chrono::milliseconds(1100));
 wil::com_ptr_nothrow<IMFActivate> secondActivation;winrt::check_hresult(factory->CreateInstance(nullptr,IID_PPV_ARGS(&secondActivation)));secondActivation->SetString(LINKBRIDGE_CAMERA_USER_SID,TestSid().c_str());secondActivation->SetUINT32(LINKBRIDGE_CAMERA_LOCAL_TEST,1);
 wil::com_ptr_nothrow<IMFMediaSource> secondSource;winrt::check_hresult(secondActivation->ActivateObject(IID_PPV_ARGS(&secondSource)));wil::com_ptr_nothrow<IMFSampleAllocatorControl> secondControl;secondSource->QueryInterface(IID_PPV_ARGS(&secondControl));wil::com_ptr_nothrow<IMFVideoSampleAllocator> secondAllocator;winrt::check_hresult(MFCreateVideoSampleAllocatorEx(IID_PPV_ARGS(&secondAllocator)));winrt::check_hresult(secondControl->SetDefaultAllocator(0,secondAllocator.get()));
 wil::com_ptr_nothrow<IMFPresentationDescriptor> secondDescriptor;secondSource->CreatePresentationDescriptor(&secondDescriptor);secondDescriptor->SelectStream(0);winrt::check_hresult(secondSource->Start(secondDescriptor.get(),nullptr,&position));wil::com_ptr_nothrow<IMFMediaStream> secondStream;
 for(int j=0;j<4;j++){wil::com_ptr_nothrow<IMFMediaEvent> e;winrt::check_hresult(secondSource->GetEvent(MF_EVENT_FLAG_NO_WAIT,&e));MediaEventType t{};e->GetType(&t);if(t==MENewStream){wil::unique_prop_variant v;e->GetValue(&v);winrt::check_hresult(v.punkVal->QueryInterface(IID_PPV_ARGS(&secondStream)));break;}}
 Check(secondStream!=nullptr,"second independent stream");winrt::check_hresult(source->Stop());source->Shutdown();Check(actualStream->RequestSample(nullptr)==MF_E_SHUTDOWN,"shutdown stream rejects new sample requests");
 winrt::check_hresult(secondStream->RequestSample(nullptr));bool secondSample=false;for(int j=0;j<4;j++){wil::com_ptr_nothrow<IMFMediaEvent> e;winrt::check_hresult(secondStream->GetEvent(MF_EVENT_FLAG_NO_WAIT,&e));MediaEventType t{};e->GetType(&t);if(t==MEMediaSample){secondSample=true;break;}}Check(secondSample,"stopping source one must not stop source two");
 winrt::check_hresult(secondSource->Stop());secondSource->Shutdown();secondStream.reset();secondControl.reset();secondAllocator.reset();secondDescriptor.reset();secondSource.reset();secondActivation->ShutdownObject();secondActivation.reset();
std::this_thread::sleep_for(std::chrono::milliseconds(50));collecting=false;collector.join();
 int starts=0,stops=0,beats=0;std::set<std::string> instances;for(auto& line:messages){auto key=line.find("\"instance\":\"");if(key!=std::string::npos)instances.insert(line.substr(key+12,38));if(line.find("\"start\"")!=std::string::npos)starts++;if(line.find("\"stop\"")!=std::string::npos)stops++;if(line.find("\"heartbeat\"")!=std::string::npos)beats++;}
 std::cout<<"Probe notifications starts="<<starts<<" stops="<<stops<<" beats="<<beats<<" instances="<<instances.size()<<"\n";Check(starts==2&&stops==2&&beats>=1&&instances.size()==2,"source lifetime emits one start/stop and heartbeat");
 std::cout<<"PASS actual MF source: 8 shared 4K samples, paced timestamps, 2 independent leases; start=2 stop=2 heartbeat="<<beats<<"; no camera registration\n";
 actualStream.reset();actualChannel.reset();allocatorControl.reset();allocator.reset();
 source->Shutdown();source.reset();activation->ShutdownObject();activation.reset();factory.reset();descriptor.reset();streamDescriptor.reset();types.reset();FreeLibrary(dll);
 auto channel=std::make_shared<FrameChannel>();winrt::check_hresult(channel->Initialize(TestSid(),true));auto h=channel->HeaderForTest();
 wil::com_ptr_nothrow<IMFMediaType> type;winrt::check_hresult(MFCreateMediaType(&type));type->SetGUID(MF_MT_SUBTYPE,MFVideoFormat_RGB32);MFSetAttributeSize(type.get(),MF_MT_FRAME_SIZE,3840,2160);
 SimpleFrameGenerator generator(channel);winrt::check_hresult(generator.Initialize(type.get()));std::vector<BYTE> output(static_cast<size_t>(IPC_FRAME_BYTES));
 winrt::check_hresult(generator.CreateFrame(output.data(),static_cast<DWORD>(output.size()),IPC_STRIDE,0));Check(output[3]==255&&output[0]==0,"offline must be explicit black frame");
 auto frame=channel->SlotForTest(1);for(size_t p=0;p<IPC_FRAME_BYTES;p+=4){frame[p]=17;frame[p+1]=53;frame[p+2]=91;frame[p+3]=255;}
 InterlockedExchange64(&h->sequence,1);InterlockedExchange(&h->activeSlot,1);InterlockedExchange(&h->status,1);InterlockedExchange64(&h->heartbeatTickMs,GetTickCount64());MemoryBarrier();InterlockedExchange64(&h->sequence,2);
 auto time=std::chrono::steady_clock::now();for(int i=0;i<30;i++)winrt::check_hresult(generator.CreateFrame(output.data(),static_cast<DWORD>(output.size()),IPC_STRIDE,0));
 Check(output[0]==17&&output[1]==53&&output[2]==91&&output.back()==255,"4K must copy exact shared BGRA");std::cout<<"PASS 3840x2160: 30 exact IPC frames in "<<std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::steady_clock::now()-time).count()<<"ms\n";
 InterlockedExchange64(&h->sequence,3);winrt::check_hresult(generator.CreateFrame(output.data(),static_cast<DWORD>(output.size()),IPC_STRIDE,0));Check(output[0]==17,"busy writer retains last stable frame");InterlockedExchange64(&h->sequence,4);
 std::atomic<bool> producing{true};
 std::thread producer([&]{LONG64 seq=4;BYTE color=23;int slot=0;while(producing){InterlockedExchange64(&h->sequence,seq+1);auto pixels=channel->SlotForTest(slot);for(size_t p=0;p<IPC_FRAME_BYTES;p+=4){pixels[p]=color;pixels[p+1]=color;pixels[p+2]=color;pixels[p+3]=255;}InterlockedExchange(&h->activeSlot,slot);InterlockedExchange64(&h->heartbeatTickMs,GetTickCount64());MemoryBarrier();InterlockedExchange64(&h->sequence,seq+2);seq+=2;slot=1-slot;color=color==23?71:23;std::this_thread::sleep_for(std::chrono::milliseconds(2));}});
 bool torn=false;for(int n=0;n<60;n++){winrt::check_hresult(generator.CreateFrame(output.data(),static_cast<DWORD>(output.size()),IPC_STRIDE,0));const auto c=output[0];if(c==23||c==71){for(size_t p=0;p<output.size();p+=4096)if(output[p]!=c)torn=true;}}
 producing=false;producer.join();Check(!torn,"seqlock must never commit a torn 4K snapshot");std::cout<<"PASS concurrent producer: 60 untorn 4K output snapshots\n";
 // Restore deterministic color for the 1080p verification.
 frame=channel->SlotForTest(1);InterlockedExchange64(&h->sequence,1001);for(size_t p=0;p<IPC_FRAME_BYTES;p+=4){frame[p]=17;frame[p+1]=53;frame[p+2]=91;frame[p+3]=255;}InterlockedExchange(&h->activeSlot,1);MemoryBarrier();InterlockedExchange64(&h->sequence,1002);
 MFSetAttributeSize(type.get(),MF_MT_FRAME_SIZE,1920,1080);winrt::check_hresult(generator.Initialize(type.get()));std::vector<BYTE> smallFrame(1920*1080*4);winrt::check_hresult(generator.CreateFrame(smallFrame.data(),static_cast<DWORD>(smallFrame.size()),1920*4,0));Check(smallFrame[0]==17&&smallFrame[2]==91,"1080 consumer uses real 4K source");
 InterlockedExchange64(&h->heartbeatTickMs,GetTickCount64()-4000);winrt::check_hresult(generator.CreateFrame(smallFrame.data(),static_cast<DWORD>(smallFrame.size()),1920*4,0));Check(smallFrame[0]==0&&smallFrame[3]==255,"stale producer returns offline");
 std::cout<<"PASS 1080 box downsample / stable snapshot / stale producer offline; no registration or device installation\n";
 generator=SimpleFrameGenerator(nullptr);channel.reset();MFShutdown();CoUninitialize();return 0;
}catch(const std::exception& e){std::cerr<<"FAIL "<<e.what()<<"\n";return 1;}catch(...){std::cerr<<"FAIL HRESULT 0x"<<std::hex<<static_cast<uint32_t>(winrt::to_hresult())<<"\n";return 1;}
