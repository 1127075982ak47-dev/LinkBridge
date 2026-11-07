#import "../Shared/LBCameraContract.h"
#import <IOSurface/IOSurface.h>
static void Require(BOOL value, NSString *message) { if (!value) { fprintf(stderr,"FAIL %s\n",message.UTF8String); exit(1); } }
int main(void) {
    @autoreleasepool {
        CMVideoFormatDescriptionRef format = LBCreate4KFormat(); Require(format != NULL,@"4K format");
        CMVideoDimensions dimensions = CMVideoFormatDescriptionGetDimensions(format);
        Require(dimensions.width == 3840 && dimensions.height == 2160 && CMFormatDescriptionGetMediaSubType(format) == kCVPixelFormatType_32BGRA,@"Exact BGRA dimensions");
        CMTime duration = CMTimeMake(1,30);
        CMIOExtensionStreamFormat *native = [CMIOExtensionStreamFormat streamFormatWithFormatDescription:format maxFrameDuration:duration minFrameDuration:duration validFrameDurations:nil];
        Require(CMTimeCompare(native.minFrameDuration,duration)==0 && CMTimeCompare(native.maxFrameDuration,duration)==0,@"CMIO format 30fps");
        CVPixelBufferPoolRef pool = LBCreate4KPool(); Require(pool != NULL,@"IOSurface pool");
        LBLatestFrame *latest = [LBLatestFrame new];
        for (int i=0; i<20; i++) {
            CVPixelBufferRef pixel = NULL;
            Require(CVPixelBufferPoolCreatePixelBufferWithAuxAttributes(kCFAllocatorDefault,pool,
                (__bridge CFDictionaryRef)@{(id)kCVPixelBufferPoolAllocationThresholdKey:@(LBPoolLimit)},&pixel)==kCVReturnSuccess,@"Bounded allocation");
            Require(CVPixelBufferGetIOSurface(pixel)!=NULL,@"Native IOSurface transport");
            CVPixelBufferLockBaseAddress(pixel,0); memset(CVPixelBufferGetBaseAddress(pixel),i,CVPixelBufferGetDataSize(pixel)); CVPixelBufferUnlockBaseAddress(pixel,0);
            CMSampleTimingInfo timing = {duration,CMTimeMake(i+1,30),kCMTimeInvalid}; CMSampleBufferRef sample=NULL;
            Require(CMSampleBufferCreateForImageBuffer(kCFAllocatorDefault,pixel,true,NULL,NULL,format,&timing,&sample)==noErr,@"Actual sample buffer");
            Require([latest publishSample:sample],@"Latest frame accepted"); CFRelease(sample); CVPixelBufferRelease(pixel);
        }
        uint64_t sequence=0; CVPixelBufferRef last=[latest copyPixelBufferAtHostTime:LBHostNanoseconds() sequence:&sequence];
        Require(last && sequence==20,@"Latest sequence only"); CVPixelBufferLockBaseAddress(last,kCVPixelBufferLock_ReadOnly);
        Require(((uint8_t *)CVPixelBufferGetBaseAddress(last))[0]==19,@"Latest real pixels retained");
        CVPixelBufferUnlockBaseAddress(last,kCVPixelBufferLock_ReadOnly); CVPixelBufferRelease(last);
        [latest clear]; Require([latest copyPixelBufferAtHostTime:LBHostNanoseconds() sequence:NULL]==NULL,@"Clear/offline no fabricated sample");
        CVPixelBufferRef wrong=NULL; Require(CVPixelBufferCreate(kCFAllocatorDefault,1920,1080,kCVPixelFormatType_32BGRA,
            (__bridge CFDictionaryRef)@{(id)kCVPixelBufferIOSurfacePropertiesKey:@{}},&wrong)==kCVReturnSuccess,@"1080 negative input");
        CMVideoFormatDescriptionRef wrongFormat=NULL; CMVideoFormatDescriptionCreateForImageBuffer(kCFAllocatorDefault,wrong,&wrongFormat);
        CMSampleTimingInfo timing={duration,CMTimeMake(1,30),kCMTimeInvalid}; CMSampleBufferRef wrongSample=NULL;
        CMSampleBufferCreateForImageBuffer(kCFAllocatorDefault,wrong,true,NULL,NULL,wrongFormat,&timing,&wrongSample);
        Require(![latest publishSample:wrongSample],@"Reject 1080 instead of upscale"); CFRelease(wrongSample); CFRelease(wrongFormat); CVPixelBufferRelease(wrong);
        CMSimpleQueueRef queue=NULL; Require(CMSimpleQueueCreate(kCFAllocatorDefault,3,&queue)==noErr,@"Actual bounded sink queue");
        int tokens[4]={1,2,3,4}; for(int i=0;i<3;i++)Require(CMSimpleQueueEnqueue(queue,&tokens[i])==noErr,@"Queue enqueue");
        Require(CMSimpleQueueEnqueue(queue,&tokens[3])==kCMSimpleQueueError_QueueIsFull,@"Bounded backpressure"); CFRelease(queue);
        CVPixelBufferPoolRelease(pool); CFRelease(format);
        puts("PASS actual CMIOExtensionStreamFormat:3840x2160 BGRA min/max30fps; IOSurface CMSampleBuffers; latest20; 1080 rejected; queue3 overload; offline clears. No service/activation/physical capture.");
    } return 0;
}
