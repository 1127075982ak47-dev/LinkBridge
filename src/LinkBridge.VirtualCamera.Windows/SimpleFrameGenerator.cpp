#include "pch.h"
HRESULT SimpleFrameGenerator::Initialize(IMFMediaType* mediaType) try {
 RETURN_IF_FAILED(m_channel->EnsureInitialized());
 GUID subtype{};RETURN_IF_FAILED(mediaType->GetGUID(MF_MT_SUBTYPE,&subtype));
 if(subtype!=MFVideoFormat_RGB32)return MF_E_INVALIDMEDIATYPE;
 RETURN_IF_FAILED(MFGetAttributeSize(mediaType,MF_MT_FRAME_SIZE,&m_width,&m_height));
 if(!((m_width==3840&&m_height==2160)||(m_width==1920&&m_height==1080)))return MF_E_INVALIDMEDIATYPE;
 BuildOffline();return S_OK;
} catch(...) {return winrt::to_hresult();}
void SimpleFrameGenerator::BuildOffline() {
 m_offline.assign(static_cast<size_t>(m_width)*m_height*4,0);
 for(size_t p=3;p<m_offline.size();p+=4)m_offline[p]=255;
 BITMAPINFO info{};info.bmiHeader.biSize=sizeof(BITMAPINFOHEADER);info.bmiHeader.biWidth=m_width;info.bmiHeader.biHeight=-static_cast<LONG>(m_height);info.bmiHeader.biPlanes=1;info.bmiHeader.biBitCount=32;info.bmiHeader.biCompression=BI_RGB;
 void* pixels=nullptr;HDC dc=CreateCompatibleDC(nullptr);HBITMAP bitmap=CreateDIBSection(dc,&info,DIB_RGB_COLORS,&pixels,nullptr,0);
 if(bitmap&&pixels){auto previous=SelectObject(dc,bitmap);memcpy(pixels,m_offline.data(),m_offline.size());
  auto font=CreateFontW(static_cast<int>(m_height/30),0,0,0,FW_NORMAL,FALSE,FALSE,FALSE,DEFAULT_CHARSET,OUT_DEFAULT_PRECIS,CLIP_DEFAULT_PRECIS,ANTIALIASED_QUALITY,DEFAULT_PITCH,L"Segoe UI");auto oldFont=SelectObject(dc,font);
  SetTextColor(dc,RGB(190,200,216));SetBkMode(dc,TRANSPARENT);RECT bounds{0,static_cast<LONG>(m_height/2-40),static_cast<LONG>(m_width),static_cast<LONG>(m_height/2+80)};
  DrawTextW(dc,L"LinkBridge - Camera offline",-1,&bounds,DT_CENTER|DT_VCENTER|DT_SINGLELINE);
  memcpy(m_offline.data(),pixels,m_offline.size());for(size_t p=3;p<m_offline.size();p+=4)m_offline[p]=255;
  SelectObject(dc,oldFont);DeleteObject(font);SelectObject(dc,previous);
 }if(bitmap)DeleteObject(bitmap);if(dc)DeleteDC(dc);
}
HRESULT SimpleFrameGenerator::CreateFrame(BYTE* output,DWORD length,LONG pitch,ULONG) try {
 if(!output||pitch==LONG_MIN||std::abs(pitch)<static_cast<LONG>(m_width*4)||length<static_cast<uint64_t>(std::abs(pitch))*m_height)return E_INVALIDARG;
 const bool newFrame=m_channel->ReadLatest(m_latest);
 if(newFrame)m_haveFrame=true;
 const auto header=m_channel->HeaderForTest();
 const auto heartbeat=header?InterlockedCompareExchange64(&header->heartbeatTickMs,0,0):0;
 const bool live=header&&InterlockedCompareExchange(&header->status,0,0)==1&&heartbeat>0&&GetTickCount64()-static_cast<ULONGLONG>(heartbeat)<=3000;
 if(!live)m_haveFrame=false;
 for(UINT32 y=0;y<m_height;y++) {
  auto row=output+static_cast<ptrdiff_t>(pitch)*y;
  if(!m_haveFrame)memcpy(row,m_offline.data()+static_cast<size_t>(y)*m_width*4,m_width*4);
  else if(m_width==IPC_WIDTH)memcpy(row,m_latest.data()+static_cast<size_t>(y)*IPC_STRIDE,m_width*4);
  else {
   // Box-average each 2x2 block, preserving the complete 4K view for 1080p consumers.
   const auto a=m_latest.data()+static_cast<size_t>(y*2)*IPC_STRIDE;
   const auto b=a+IPC_STRIDE;
   for(UINT32 x=0;x<m_width;x++)for(int c=0;c<4;c++)row[x*4+c]=static_cast<BYTE>((static_cast<unsigned>(a[x*8+c])+a[x*8+4+c]+b[x*8+c]+b[x*8+4+c]+2)/4);
  }
 }
 return S_OK;
} catch(...) {return winrt::to_hresult();}
