#import "LBSinkConnection.h"

static NSString *LBReadString(CMIOObjectID object, CMIOObjectPropertySelector selector) {
    CMIOObjectPropertyAddress address = { selector, kCMIOObjectPropertyScopeGlobal, kCMIOObjectPropertyElementMain };
    CFStringRef result = NULL; UInt32 size = sizeof(result);
    if (CMIOObjectGetPropertyData(object, &address, 0, NULL, size, &size, &result) != noErr || !result) return nil;
    return CFBridgingRelease(result);
}
static NSArray<NSNumber *> *LBReadObjectsInScope(CMIOObjectID object, CMIOObjectPropertySelector selector, CMIOObjectPropertyScope scope) {
    CMIOObjectPropertyAddress address = { selector, scope, kCMIOObjectPropertyElementMain };
    UInt32 size = 0;
    if (CMIOObjectGetPropertyDataSize(object, &address, 0, NULL, &size) != noErr || size > 4096 * sizeof(CMIOObjectID)) return @[];
    NSMutableData *data = [NSMutableData dataWithLength:size];
    if (CMIOObjectGetPropertyData(object, &address, 0, NULL, size, &size, data.mutableBytes) != noErr) return @[];
    NSMutableArray *objects = [NSMutableArray new]; CMIOObjectID *ids = data.mutableBytes;
    for (UInt32 i = 0; i < size / sizeof(CMIOObjectID); i++) [objects addObject:@(ids[i])];
    return objects;
}
static NSArray<NSNumber *> *LBReadObjects(CMIOObjectID object, CMIOObjectPropertySelector selector) {
    return LBReadObjectsInScope(object,selector,kCMIOObjectPropertyScopeGlobal);
}
static BOOL LBStreamHasNative4K(CMIOStreamID stream) {
    CMIOObjectPropertyAddress address = {kCMIOStreamPropertyFormatDescription,kCMIOObjectPropertyScopeGlobal,kCMIOObjectPropertyElementMain};
    CMFormatDescriptionRef format = NULL; UInt32 size = sizeof(format);
    if (CMIOObjectGetPropertyData(stream,&address,0,NULL,size,&size,&format) != noErr || !format) return NO;
    CMVideoDimensions dimensions = CMVideoFormatDescriptionGetDimensions(format);
    BOOL matches = CMFormatDescriptionGetMediaType(format) == kCMMediaType_Video &&
        CMFormatDescriptionGetMediaSubType(format) == kCVPixelFormatType_32BGRA && dimensions.width == LBWidth && dimensions.height == LBHeight;
    CFRelease(format);
    Float64 rate = 0; address.mSelector = kCMIOStreamPropertyFrameRate; size = sizeof(rate);
    return matches && CMIOObjectGetPropertyData(stream,&address,0,NULL,size,&size,&rate) == noErr && rate == LBFPS;
}
@interface LBSinkConnection ()
- (void)removed:(void *)token;
@end
static void LBQueueChanged(CMIOStreamID streamID, void *token, void *context) {
    (void)streamID; [(__bridge LBSinkConnection *)context removed:token];
}
@implementation LBSinkConnection {
    CMIODeviceID _device; CMIOStreamID _source, _sink;
    CMSimpleQueueRef _queue; CVPixelBufferPoolRef _pool; CMVideoFormatDescriptionRef _format;
    NSLock *_lock; NSMutableSet<NSValue *> *_pending;
    BOOL _active;
}
- (instancetype)init {
    if ((self = [super init])) { _lock = [NSLock new]; _pending = [NSMutableSet new]; _pool = LBCreate4KPool(); _format = LBCreate4KFormat(); }
    return self;
}
- (BOOL)found { return _device && _source && _sink; }
- (OSStatus)discover {
    if (_active) return noErr;
    _device = _source = _sink = 0;
    for (NSNumber *object in LBReadObjects(kCMIOObjectSystemObject, kCMIOHardwarePropertyDevices)) {
        CMIODeviceID device = object.unsignedIntValue;
        if (![LBReadString(device, kCMIODevicePropertyDeviceUID) isEqual:LBDeviceUID]) continue;
        NSMutableOrderedSet *streams = [NSMutableOrderedSet new];
        for (NSNumber *scope in @[@(kCMIOObjectPropertyScopeGlobal),@(kCMIODevicePropertyScopeInput),@(kCMIODevicePropertyScopeOutput)])
            [streams addObjectsFromArray:LBReadObjectsInScope(device,kCMIODevicePropertyStreams,scope.unsignedIntValue)];
        for (NSNumber *streamObject in streams) {
            CMIOStreamID stream = streamObject.unsignedIntValue;
            NSString *name = LBReadString(stream, kCMIOObjectPropertyName);
            if ([name isEqual:LBSourceName] && LBStreamHasNative4K(stream)) _source = stream;
            else if ([name isEqual:LBSinkName] && LBStreamHasNative4K(stream)) _sink = stream;
        }
        if (_source && _sink) { _device = device; return noErr; }
    }
    return kCMIOHardwareBadDeviceError;
}
- (NSDictionary *)readDemand {
    if (!self.found) return nil;
    NSString *value = LBReadString(_source, LBDemandSelector);
    if (!value || value.length > 4096) return nil;
    NSDictionary *data = [NSJSONSerialization JSONObjectWithData:[value dataUsingEncoding:NSUTF8StringEncoding] options:0 error:NULL];
    if (![data isKindOfClass:NSDictionary.class] || [data[@"v"] integerValue] != 1 ||
        [data[@"width"] integerValue] != LBWidth || [data[@"height"] integerValue] != LBHeight || [data[@"fps"] integerValue] != LBFPS) return nil;
    return data;
}
- (OSStatus)start {
    if (_active) return noErr;
    if (!self.found || !_pool || !_format) return kCMIOHardwareBadDeviceError;
    CMSimpleQueueRef queue = NULL;
    OSStatus status = CMIOStreamCopyBufferQueue(_sink, LBQueueChanged, (__bridge void *)self, &queue);
    if (status != noErr || !queue) return status != noErr ? status : kCMIOHardwareUnspecifiedError;
    if (CMSimpleQueueGetCapacity(queue) < 1 || CMSimpleQueueGetCapacity(queue) > 3) { CFRelease(queue); return kCMIOHardwareUnsupportedOperationError; }
    [_lock lock]; _queue = queue; _active = YES; [_lock unlock];
    status = CMIODeviceStartStream(_device, _sink);
    if (status != noErr) [self stop]; return status;
}
- (OSStatus)pushBGRA:(NSData *)frame {
    if (frame.length != LBFrameBytes) return kCMIOHardwareBadPropertySizeError;
    [_lock lock];
    if (!_active || !_queue) { [_lock unlock]; return kCMIOHardwareNotRunningError; }
    if (CMSimpleQueueGetCount(_queue) >= CMSimpleQueueGetCapacity(_queue)) { [_lock unlock]; return kCMSimpleQueueError_QueueIsFull; }
    CVPixelBufferRef pixel = NULL;
    CVReturn result = CVPixelBufferPoolCreatePixelBufferWithAuxAttributes(kCFAllocatorDefault, _pool,
        (__bridge CFDictionaryRef)@{(id)kCVPixelBufferPoolAllocationThresholdKey:@(LBPoolLimit)}, &pixel);
    if (result != kCVReturnSuccess) { [_lock unlock]; return result; }
    CVPixelBufferLockBaseAddress(pixel, 0);
    size_t stride = CVPixelBufferGetBytesPerRow(pixel); uint8_t *out = CVPixelBufferGetBaseAddress(pixel);
    for (int y = 0; y < LBHeight; y++) memcpy(out + y * stride, (const uint8_t *)frame.bytes + y * LBWidth * 4, LBWidth * 4);
    CVPixelBufferUnlockBaseAddress(pixel, 0);
    uint64_t now = LBHostNanoseconds(); CMSampleBufferRef sample = NULL;
    CMSampleTimingInfo timing = {CMTimeMake(1,LBFPS), CMTimeMake((int64_t)now,1000000000), kCMTimeInvalid};
    OSStatus status = CMSampleBufferCreateForImageBuffer(kCFAllocatorDefault, pixel, true, NULL, NULL, _format, &timing, &sample);
    CVPixelBufferRelease(pixel);
    if (status == noErr) {
        NSValue *token = [NSValue valueWithPointer:sample]; [_pending addObject:token];
        status = CMSimpleQueueEnqueue(_queue, sample);
        if (status != noErr) { [_pending removeObject:token]; CFRelease(sample); }
        // The queue callback releases our sample reference after framework removal.
    }
    [_lock unlock]; return status;
}
- (void)removed:(void *)token {
    if (!token) return;
    [_lock lock]; NSValue *key = [NSValue valueWithPointer:token]; BOOL ours = [_pending containsObject:key];
    if (ours) [_pending removeObject:key]; [_lock unlock]; if (ours) CFRelease(token);
}
- (void)stop {
    [_lock lock]; BOOL active = _active; _active = NO; CMSimpleQueueRef queue = _queue; _queue = NULL; [_lock unlock];
    if (!active) return;
    // The callback takes _lock. Stop/unregister outside it, preserving lock order.
    CMIODeviceStopStream(_device, _sink);
    CMSimpleQueueRef other = NULL; CMIOStreamCopyBufferQueue(_sink, NULL, NULL, &other); if (other) CFRelease(other);
    [_lock lock]; NSSet *pending = [_pending copy]; [_pending removeAllObjects]; [_lock unlock];
    for (NSValue *token in pending) CFRelease(token.pointerValue);
    if (queue) { while (CMSimpleQueueDequeue(queue)) {} CFRelease(queue); }
}
- (void)dealloc { [self stop]; if (_format) CFRelease(_format); if (_pool) CVPixelBufferPoolRelease(_pool); }
@end
