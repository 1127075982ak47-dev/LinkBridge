#import "LBProvider.h"
#import <Security/Security.h>

static NSError *LBError(NSString *message) { return [NSError errorWithDomain:@"com.linkbridge.camera" code:1 userInfo:@{NSLocalizedDescriptionKey:message}]; }
static NSString *LBTeamOfCode(SecCodeRef code) {
    CFDictionaryRef info = NULL;
    if (SecCodeCopySigningInformation(code, kSecCSSigningInformation, &info) != errSecSuccess) return nil;
    NSString *team = [((__bridge NSDictionary *)info)[(__bridge id)kSecCodeInfoTeamIdentifier] copy];
    CFRelease(info); return team;
}
static BOOL LBAuthorizeHost(CMIOExtensionClient *client) {
    if (![client.signingID isEqualToString:LBHostSigningID]) return NO;
    SecCodeRef own = NULL, guest = NULL; SecRequirementRef requirement = NULL;
    NSString *team = nil, *text = nil;
    BOOL accepted = NO;
    if (SecCodeCopySelf(kSecCSDefaultFlags, &own) != errSecSuccess) goto done;
    team = LBTeamOfCode(own);
    if (!team.length || team.length != 10) goto done; // Never accept unsigned/ad-hoc host access.
    if (SecCodeCopyGuestWithAttributes(NULL, (__bridge CFDictionaryRef)@{(__bridge id)kSecGuestAttributePid:@(client.pid)},
        kSecCSDefaultFlags, &guest) != errSecSuccess) goto done;
    text = [NSString stringWithFormat:@"anchor apple generic and identifier \"%@\" and certificate leaf[subject.OU] = \"%@\"", LBHostSigningID, team];
    if (SecRequirementCreateWithString((__bridge CFStringRef)text, kSecCSDefaultFlags, &requirement) != errSecSuccess) goto done;
    accepted = SecCodeCheckValidity(guest, kSecCSStrictValidate, requirement) == errSecSuccess && [LBTeamOfCode(guest) isEqualToString:team];
done:
    if (requirement) CFRelease(requirement); if (guest) CFRelease(guest); if (own) CFRelease(own); return accepted;
}

@class LBStream;
@interface LBDevice : NSObject <CMIOExtensionDeviceSource>
@property(nonatomic) CMIOExtensionDevice *device;
@property(nonatomic) LBStream *source, *sink;
@property(nonatomic) dispatch_queue_t queue;
@property(nonatomic) LBLatestFrame *latest;
@property(nonatomic) NSUInteger demand;
@property(nonatomic) uint64_t demandGeneration;
@property(nonatomic) uint64_t delivered, lastSentNS;
@property(nonatomic) dispatch_source_t timer;
- (void)changeDemand:(BOOL)start;
- (NSString *)demandJSON;
@end
@interface LBStream : NSObject <CMIOExtensionStreamSource>
@property(nonatomic) CMIOExtensionStream *stream;
@property(nonatomic, weak) LBDevice *owner;
@property(nonatomic) BOOL sink, started;
@property(nonatomic) uint64_t sinkGeneration;
@property(nonatomic) CMIOExtensionClient *sinkClient;
@property(atomic, strong) NSArray<CMIOExtensionStreamFormat *> *formats;
- (instancetype)initWithOwner:(LBDevice *)owner sink:(BOOL)sink;
- (void)consume:(uint64_t)generation;
@end

@implementation LBDevice
- (instancetype)init {
    if ((self = [super init])) {
        _queue = dispatch_queue_create("com.linkbridge.camera.extension", DISPATCH_QUEUE_SERIAL);
        _latest = [LBLatestFrame new];
        _device = [[CMIOExtensionDevice alloc] initWithLocalizedName:@"联桥共享摄像头 · 原生4K"
            deviceID:[[NSUUID alloc] initWithUUIDString:@"C11EF49A-97A3-4B08-8144-0A24443F7243"] legacyDeviceID:LBDeviceUID source:self];
        _source = [[LBStream alloc] initWithOwner:self sink:NO]; _sink = [[LBStream alloc] initWithOwner:self sink:YES];
        NSError *error = nil;
        if (![_device addStream:_source.stream error:&error] || ![_device addStream:_sink.stream error:&error])
            @throw [NSException exceptionWithName:@"CMIOSetup" reason:error.localizedDescription userInfo:nil];
    } return self;
}
- (NSSet<CMIOExtensionProperty> *)availableProperties { return [NSSet setWithObjects:CMIOExtensionPropertyDeviceModel, CMIOExtensionPropertyDeviceTransportType, nil]; }
- (CMIOExtensionDeviceProperties *)devicePropertiesForProperties:(NSSet<CMIOExtensionProperty> *)properties error:(NSError **)error {
    (void)properties; (void)error;
    CMIOExtensionDeviceProperties *result = [[CMIOExtensionDeviceProperties alloc] initWithDictionary:@{}];
    result.model = @"LinkBridge Native 3840×2160"; result.transportType = @(0x76697274); return result; // virt
}
- (BOOL)setDeviceProperties:(CMIOExtensionDeviceProperties *)properties error:(NSError **)error {
    (void)properties; if (error) *error = LBError(@"Camera device properties are read-only."); return NO;
}
- (NSString *)demandJSON {
    NSData *data = [NSJSONSerialization dataWithJSONObject:@{ @"v":@1, @"consumers":@(_demand),
        @"generation":@(_demandGeneration), @"width":@(LBWidth), @"height":@(LBHeight), @"fps":@(LBFPS),
        @"framesDelivered":@(_delivered), @"lastFrameHostNs":@(_lastSentNS) } options:0 error:NULL];
    return [[NSString alloc] initWithData:data encoding:NSUTF8StringEncoding];
}
- (void)changeDemand:(BOOL)start {
    if (start) { if (_demand == 0) { _demandGeneration++; _delivered = _lastSentNS = 0; } _demand++; }
    else if (_demand > 0) _demand--;
    [_source.stream notifyPropertiesChanged:@{ LBDemandProperty:[CMIOExtensionPropertyState propertyStateWithValue:[self demandJSON]] }];
    if (_demand == 0) { _lastSentNS = 0; if (_timer) { dispatch_source_cancel(_timer); _timer = nil; } [_latest clear]; return; }
    if (_timer) return;
    _timer = dispatch_source_create(DISPATCH_SOURCE_TYPE_TIMER, 0, 0, _queue);
    dispatch_source_set_timer(_timer, dispatch_time(DISPATCH_TIME_NOW, 0), NSEC_PER_SEC / LBFPS, NSEC_PER_MSEC);
    __weak LBDevice *weak = self;
    dispatch_source_set_event_handler(_timer, ^{
        LBDevice *self = weak; if (!self || self.demand == 0) return;
        uint64_t now = LBHostNanoseconds(), sequence = 0;
        CVPixelBufferRef pixel = [self.latest copyPixelBufferAtHostTime:now sequence:&sequence];
        if (!pixel) return; // No fabricated frame when the remote source is missing/stale.
        CMVideoFormatDescriptionRef format = LBCreate4KFormat(); CMSampleBufferRef sample = NULL;
        CMSampleTimingInfo timing = { CMTimeMake(1, LBFPS), CMTimeMake((int64_t)now, 1000000000), kCMTimeInvalid };
        if (format && CMSampleBufferCreateForImageBuffer(kCFAllocatorDefault, pixel, true, NULL, NULL, format, &timing, &sample) == noErr) {
            [self.source.stream sendSampleBuffer:sample discontinuity:CMIOExtensionStreamDiscontinuityFlagNone hostTimeInNanoseconds:now];
            self.delivered++; self.lastSentNS = now;
            if (self.delivered == 1 || self.delivered % LBFPS == 0)
                [self.source.stream notifyPropertiesChanged:@{ LBDemandProperty:[CMIOExtensionPropertyState propertyStateWithValue:[self demandJSON]] }];
            CFRelease(sample);
        }
        if (format) CFRelease(format); CVPixelBufferRelease(pixel); (void)sequence;
    });
    dispatch_resume(_timer);
}
@end

@implementation LBStream
- (instancetype)initWithOwner:(LBDevice *)owner sink:(BOOL)sink {
    if ((self = [super init])) {
        _owner = owner; _sink = sink;
        CMVideoFormatDescriptionRef description = LBCreate4KFormat();
        if (!description) @throw [NSException exceptionWithName:@"CMIOFormat" reason:@"Cannot create 4K format" userInfo:nil];
        CMTime duration = CMTimeMake(1, LBFPS);
        _formats = @[[CMIOExtensionStreamFormat streamFormatWithFormatDescription:description maxFrameDuration:duration minFrameDuration:duration validFrameDurations:nil]];
        CFRelease(description);
        _stream = [[CMIOExtensionStream alloc] initWithLocalizedName:sink ? LBSinkName : LBSourceName
            streamID:[[NSUUID alloc] initWithUUIDString:sink ? @"FD3D7B0D-5C0B-4012-89BD-6D829289976D" : @"93BB61B1-D966-4F7A-9167-9420F37A64F0"]
            direction:sink ? CMIOExtensionStreamDirectionSink : CMIOExtensionStreamDirectionSource
            clockType:CMIOExtensionStreamClockTypeHostTime source:self];
    } return self;
}
- (NSSet<CMIOExtensionProperty> *)availableProperties {
    NSMutableSet *result = [NSMutableSet setWithObjects:CMIOExtensionPropertyStreamActiveFormatIndex,
        CMIOExtensionPropertyStreamFrameDuration, CMIOExtensionPropertyStreamMaxFrameDuration, nil];
    if (_sink) [result addObjectsFromArray:@[CMIOExtensionPropertyStreamSinkBufferQueueSize,
        CMIOExtensionPropertyStreamSinkBuffersRequiredForStartup, CMIOExtensionPropertyStreamSinkEndOfData]];
    else [result addObject:LBDemandProperty]; return result;
}
- (CMIOExtensionStreamProperties *)streamPropertiesForProperties:(NSSet<CMIOExtensionProperty> *)properties error:(NSError **)error {
    (void)properties; (void)error;
    CMIOExtensionStreamProperties *result = [[CMIOExtensionStreamProperties alloc] initWithDictionary:@{}];
    result.activeFormatIndex = @0;
    NSDictionary *duration = CFBridgingRelease(CMTimeCopyAsDictionary(CMTimeMake(1, LBFPS), kCFAllocatorDefault));
    result.frameDuration = duration; result.maxFrameDuration = duration;
    if (_sink) { result.sinkBufferQueueSize = @3; result.sinkBuffersRequiredForStartup = @1; result.sinkEndOfData = @NO; }
    else [result setPropertyState:[CMIOExtensionPropertyState propertyStateWithValue:[_owner demandJSON]] forProperty:LBDemandProperty];
    return result;
}
- (BOOL)setStreamProperties:(CMIOExtensionStreamProperties *)properties error:(NSError **)error {
    if ((properties.activeFormatIndex && properties.activeFormatIndex.integerValue != 0) ||
        (properties.frameDuration && CMTimeCompare(CMTimeMakeFromDictionary((__bridge CFDictionaryRef)properties.frameDuration), CMTimeMake(1,LBFPS)) != 0) ||
        (properties.maxFrameDuration && CMTimeCompare(CMTimeMakeFromDictionary((__bridge CFDictionaryRef)properties.maxFrameDuration), CMTimeMake(1,LBFPS)) != 0)) {
        if (error) *error = LBError(@"Only native 3840×2160 BGRA at 30fps is supported."); return NO;
    }
    if (properties.propertiesDictionary[LBDemandProperty]) { if (error) *error = LBError(@"Consumer demand is read-only."); return NO; }
    return YES;
}
- (BOOL)authorizedToStartStreamForClient:(CMIOExtensionClient *)client {
    if (!_sink) return YES; // Apple's proxy enforces consuming apps' camera privacy authorization.
    if (!LBAuthorizeHost(client) || (_started && ![_sinkClient.clientID isEqual:client.clientID])) return NO;
    _sinkClient = client; return YES;
}
- (BOOL)startStreamAndReturnError:(NSError **)error {
    if (!_sink) { [_owner changeDemand:YES]; return YES; }
    if (!_sinkClient) { if (error) *error = LBError(@"No authorized LinkBridge host."); return NO; }
    if (_started) return YES; _started = YES; _sinkGeneration++; [self consume:_sinkGeneration]; return YES;
}
- (BOOL)stopStreamAndReturnError:(NSError **)error {
    (void)error;
    if (!_sink) { [_owner changeDemand:NO]; return YES; }
    _started = NO; _sinkGeneration++; _sinkClient = nil; [_owner.latest clear]; return YES;
}
- (void)consume:(uint64_t)generation {
    if (!_started || generation != _sinkGeneration || !_sinkClient) return;
    __weak LBStream *weak = self;
    [_stream consumeSampleBufferFromClient:_sinkClient completionHandler:^(CMSampleBufferRef sample, uint64_t sequence,
        CMIOExtensionStreamDiscontinuityFlags flags, BOOL more, NSError *error) {
        (void)flags; (void)more;
        // Framework callback may be on another queue. Retain sample until serial application.
        if (sample) CFRetain(sample);
        dispatch_async(weak.owner.queue, ^{
            LBStream *self = weak;
            if (self && self.started && self.sinkGeneration == generation) {
                if (sample && !error) [self.owner.latest publishSample:sample];
                if (sample) [self.stream notifyScheduledOutputChanged:[[CMIOExtensionScheduledOutput alloc]
                    initWithSequenceNumber:sequence hostTimeInNanoseconds:LBHostNanoseconds()]];
                if (!error) [self consume:generation];
            }
            if (sample) CFRelease(sample);
        });
    }];
}
@end

@implementation LBProvider { LBDevice *_deviceSource; }
- (instancetype)init {
    if ((self = [super init])) {
        _deviceSource = [LBDevice new]; _provider = [[CMIOExtensionProvider alloc] initWithSource:self clientQueue:_deviceSource.queue];
        NSError *error = nil;
        if (![_provider addDevice:_deviceSource.device error:&error])
            @throw [NSException exceptionWithName:@"CMIOProvider" reason:error.localizedDescription userInfo:nil];
    } return self;
}
- (BOOL)connectClient:(CMIOExtensionClient *)client error:(NSError **)error { (void)client; (void)error; return YES; }
- (void)disconnectClient:(CMIOExtensionClient *)client { (void)client; }
- (NSSet<CMIOExtensionProperty> *)availableProperties { return [NSSet setWithObjects:CMIOExtensionPropertyProviderName, CMIOExtensionPropertyProviderManufacturer, nil]; }
- (CMIOExtensionProviderProperties *)providerPropertiesForProperties:(NSSet<CMIOExtensionProperty> *)properties error:(NSError **)error {
    (void)properties; (void)error;
    CMIOExtensionProviderProperties *result = [[CMIOExtensionProviderProperties alloc] initWithDictionary:@{}];
    result.name = @"LinkBridge"; result.manufacturer = @"LinkBridge"; return result;
}
- (BOOL)setProviderProperties:(CMIOExtensionProviderProperties *)properties error:(NSError **)error { (void)properties; if (error) *error = LBError(@"Provider properties are read-only."); return NO; }
@end
