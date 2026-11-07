#import "LBCameraContract.h"
#import <IOSurface/IOSurface.h>
#import <unistd.h>

NSString *const LBDeviceUID = @"com.linkbridge.camera.mac.native4k.v1";
NSString *const LBSourceName = @"LinkBridge.Source.4K";
NSString *const LBSinkName = @"LinkBridge.Sink.4K";
NSString *const LBHostSigningID = @"com.linkbridge.camera.host";
NSString *const LBDemandProperty = @"4cc_lbcd_glob_0000";

CMVideoFormatDescriptionRef LBCreate4KFormat(void) {
    CMVideoFormatDescriptionRef format = NULL;
    if (CMVideoFormatDescriptionCreate(kCFAllocatorDefault, kCVPixelFormatType_32BGRA,
        LBWidth, LBHeight, NULL, &format) != noErr) return NULL;
    return format;
}
CVPixelBufferPoolRef LBCreate4KPool(void) {
    CVPixelBufferPoolRef pool = NULL;
    NSDictionary *attributes = @{ (id)kCVPixelBufferPixelFormatTypeKey: @(kCVPixelFormatType_32BGRA),
        (id)kCVPixelBufferWidthKey: @(LBWidth), (id)kCVPixelBufferHeightKey: @(LBHeight),
        (id)kCVPixelBufferIOSurfacePropertiesKey: @{}, (id)kCVPixelBufferBytesPerRowAlignmentKey: @64 };
    NSDictionary *poolAttributes = @{ (id)kCVPixelBufferPoolMinimumBufferCountKey: @3 };
    if (CVPixelBufferPoolCreate(kCFAllocatorDefault, (__bridge CFDictionaryRef)poolAttributes,
        (__bridge CFDictionaryRef)attributes, &pool) != kCVReturnSuccess) return NULL;
    return pool;
}
BOOL LBValidateSample(CMSampleBufferRef sample) {
    CVImageBufferRef image = CMSampleBufferGetImageBuffer(sample);
    return image && CVPixelBufferGetWidth(image) == LBWidth && CVPixelBufferGetHeight(image) == LBHeight &&
        CVPixelBufferGetPixelFormatType(image) == kCVPixelFormatType_32BGRA && CVPixelBufferGetIOSurface(image) != NULL;
}
uint64_t LBHostNanoseconds(void) {
    CMTime time = CMClockGetTime(CMClockGetHostTimeClock());
    return (uint64_t)CMTimeConvertScale(time, 1000000000, kCMTimeRoundingMethod_Default).value;
}
void LBEmitJSON(NSDictionary *value) {
    NSData *data = [NSJSONSerialization dataWithJSONObject:value options:0 error:NULL];
    if (!data) return;
    if (data.length > 4095) return;
    static NSLock *lock; static dispatch_once_t once;
    dispatch_once(&once, ^{ lock = [NSLock new]; });
    NSMutableData *line = [data mutableCopy]; [line appendBytes:"\n" length:1];
    [lock lock];
    // Host stdout is nonblocking. A stalled/disconnected controller must not
    // prevent its owned decoder from being stopped by EOF/signal cleanup.
    ssize_t written = write(STDOUT_FILENO,line.bytes,line.length); (void)written;
    [lock unlock];
}

@implementation LBLatestFrame {
    NSLock *_lock;
    CVPixelBufferRef _pixel;
    uint64_t _sequence, _received;
}
- (instancetype)init { if ((self = [super init])) _lock = [NSLock new]; return self; }
- (uint64_t)sequence { [_lock lock]; uint64_t value = _sequence; [_lock unlock]; return value; }
- (BOOL)publishSample:(CMSampleBufferRef)sample {
    if (!LBValidateSample(sample)) return NO;
    CVPixelBufferRef pixel = CVPixelBufferRetain(CMSampleBufferGetImageBuffer(sample));
    [_lock lock]; CVPixelBufferRef old = _pixel; _pixel = pixel; _sequence++; _received = LBHostNanoseconds(); [_lock unlock];
    if (old) CVPixelBufferRelease(old);
    return YES;
}
- (CVPixelBufferRef)copyPixelBufferAtHostTime:(uint64_t)now sequence:(uint64_t *)sequence {
    [_lock lock];
    CVPixelBufferRef result = _pixel && now >= _received && now - _received <= 1000000000 ? CVPixelBufferRetain(_pixel) : NULL;
    if (sequence) *sequence = _sequence;
    [_lock unlock]; return result;
}
- (void)clear { [_lock lock]; CVPixelBufferRef old = _pixel; _pixel = NULL; _received = 0; [_lock unlock]; if (old) CVPixelBufferRelease(old); }
- (void)dealloc { if (_pixel) CVPixelBufferRelease(_pixel); }
@end
