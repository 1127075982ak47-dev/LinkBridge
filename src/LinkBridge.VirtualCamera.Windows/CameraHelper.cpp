#include "pch.h"
#include <sddl.h>
#include <iostream>
#include <fstream>
#include <filesystem>
std::wstring CurrentSid() {
 HANDLE token=nullptr;if(!OpenProcessToken(GetCurrentProcess(),TOKEN_QUERY,&token))winrt::throw_last_error();
 DWORD size=0;GetTokenInformation(token,TokenUser,nullptr,0,&size);std::vector<BYTE> data(size);
 if(!GetTokenInformation(token,TokenUser,data.data(),size,&size)){CloseHandle(token);winrt::throw_last_error();}CloseHandle(token);
 PWSTR text=nullptr;if(!ConvertSidToStringSidW(reinterpret_cast<TOKEN_USER*>(data.data())->User.Sid,&text))winrt::throw_last_error();std::wstring sid(text);LocalFree(text);return sid;
}
void Require(HRESULT hr,const wchar_t* operation) {std::wcout<<operation<<L" HRESULT=0x"<<std::hex<<static_cast<uint32_t>(hr)<<std::dec<<L"\n";winrt::check_hresult(hr);}
bool Elevated() {HANDLE token=nullptr;TOKEN_ELEVATION elevation{};DWORD size=0;if(!OpenProcessToken(GetCurrentProcess(),TOKEN_QUERY,&token))return false;GetTokenInformation(token,TokenElevation,&elevation,sizeof(elevation),&size);CloseHandle(token);return elevation.TokenIsElevated!=0;}
void Registration(const std::wstring& dll,bool remove) {
 if(!Elevated())winrt::throw_hresult(E_ACCESSDENIED);
 const std::wstring key=L"SOFTWARE\\Classes\\CLSID\\"+std::wstring(CAMERA_CLSID);
 if(remove){const auto result=RegDeleteTreeW(HKEY_LOCAL_MACHINE,key.c_str());Require(result==ERROR_FILE_NOT_FOUND?S_OK:HRESULT_FROM_WIN32(result),L"Unregister CLSID");return;}
 const auto absolute=std::filesystem::absolute(dll);if(!std::filesystem::exists(absolute))winrt::throw_hresult(HRESULT_FROM_WIN32(ERROR_FILE_NOT_FOUND));
 HKEY handle=nullptr;Require(HRESULT_FROM_WIN32(RegCreateKeyExW(HKEY_LOCAL_MACHINE,(key+L"\\InprocServer32").c_str(),0,nullptr,0,KEY_SET_VALUE|KEY_WOW64_64KEY,nullptr,&handle,nullptr)),L"Register CLSID");
 const std::wstring value=absolute.wstring();auto status=RegSetValueExW(handle,nullptr,0,REG_SZ,reinterpret_cast<const BYTE*>(value.c_str()),static_cast<DWORD>((value.size()+1)*sizeof(wchar_t)));
 const wchar_t threading[]=L"Both";if(status==ERROR_SUCCESS)status=RegSetValueExW(handle,L"ThreadingModel",0,REG_SZ,reinterpret_cast<const BYTE*>(threading),sizeof(threading));RegCloseKey(handle);Require(HRESULT_FROM_WIN32(status),L"Register source DLL");
}
int wmain(int argc,wchar_t** argv) try {
 if(argc>1&&std::wstring(argv[1])==L"--info-json") {
  const auto sid=CurrentSid();std::wcout<<L"{\"v\":1,\"ownerSid\":\""<<sid<<L"\",\"sourceClsid\":\""<<CAMERA_CLSID<<L"\",\"mappingName\":\"Global\\\\LinkBridge.Camera.Frame.v1."<<sid<<L"\",\"pipeName\":\"LinkBridge.Camera.Events.v1."<<sid<<L"\",\"headerBytes\":4096,\"frameBytes\":33177600,\"mappingBytes\":66359296,\"width\":3840,\"height\":2160,\"stride\":15360,\"pixelFormat\":1095911234}\n";return 0;
 }
 Require(CoInitializeEx(nullptr,COINIT_MULTITHREADED),L"CoInitializeEx");Require(MFStartup(MF_VERSION),L"MFStartup");
 const auto sid=CurrentSid();std::wcout<<L"User SID="<<sid<<L"\nMapping="<<CameraMappingName(sid)<<L"\nEvents="<<CameraPipeName(sid)<<L"\nSource CLSID="<<CAMERA_CLSID<<L"\n";
 const std::wstring command=argc>1?argv[1]:L"--info";
 if(command==L"--register-source"){if(argc<3)winrt::throw_hresult(E_INVALIDARG);Registration(argv[2],false);}
 else if(command==L"--unregister-source")Registration(L"",true);
 else if(command==L"--create"||command==L"--remove") {
  wil::com_ptr_nothrow<IMFVirtualCamera> camera;
  Require(MFCreateVirtualCamera(MFVirtualCameraType_SoftwareCameraSource,MFVirtualCameraLifetime_System,MFVirtualCameraAccess_CurrentUser,CAMERA_NAME,CAMERA_CLSID,nullptr,0,&camera),L"MFCreateVirtualCamera");
  Require(camera->SetString(LINKBRIDGE_CAMERA_USER_SID,sid.c_str()),L"Set broker SID");
  if(command==L"--remove")Require(camera->Remove(),L"Remove virtual camera");
  else {
   Require(camera->Start(nullptr),L"Start virtual camera registration");
   wil::unique_cotaskmem_string symbolic;UINT32 chars=0;
   const auto hr=camera->GetAllocatedString(MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_SYMBOLIC_LINK,&symbolic,&chars);
   std::wcout<<L"Symbolic-link HRESULT=0x"<<std::hex<<static_cast<uint32_t>(hr)<<std::dec<<L"\n";
   if(SUCCEEDED(hr))std::wcout<<L"Virtual camera id="<<symbolic.get()<<L"\n";
   // Record the stable creation tuple and returned device id for review/removal, never pairing credentials.
   wchar_t appData[32768]{};GetEnvironmentVariableW(L"LOCALAPPDATA",appData,_countof(appData));
   auto record=std::filesystem::path(appData)/L"LinkBridge"/L"VirtualCamera";std::filesystem::create_directories(record);
   std::ofstream file(record/L"device.txt");file<<"protocol=1\nsourceClsid="<<winrt::to_string(CAMERA_CLSID)<<"\nuserSid="<<winrt::to_string(sid)<<"\nlifetime=System\naccess=CurrentUser\n";
   if(SUCCEEDED(hr))file<<"deviceId="<<winrt::to_string(symbolic.get())<<"\n";
  }
 }
 else if(command!=L"--info"){std::wcerr<<L"Usage: --info | --register-source DLL | --create | --remove | --unregister-source\n";return 2;}
 MFShutdown();CoUninitialize();return 0;
} catch(...){const auto hr=winrt::to_hresult();std::wcerr<<L"ERROR HRESULT=0x"<<std::hex<<static_cast<uint32_t>(hr)<<L"\n";return 1;}
