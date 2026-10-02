#pragma once
#include <memory>
#include <vector>
#include "FrameChannel.h"
class SimpleFrameGenerator {
public:
 explicit SimpleFrameGenerator(std::shared_ptr<FrameChannel> channel):m_channel(std::move(channel)){}
 HRESULT Initialize(IMFMediaType* mediaType);
 HRESULT CreateFrame(BYTE* output,DWORD length,LONG pitch,ULONG unused);
 UINT32 Width()const{return m_width;}UINT32 Height()const{return m_height;}
private:
 void BuildOffline();
 std::shared_ptr<FrameChannel> m_channel;std::vector<BYTE> m_latest,m_offline;
 UINT32 m_width=0,m_height=0;bool m_haveFrame=false;
};
