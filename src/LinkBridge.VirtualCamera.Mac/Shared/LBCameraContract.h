#import <Foundation/Foundation.h>
#import <CoreMedia/CoreMedia.h>
#import <CoreVideo/CoreVideo.h>
#import <CoreMediaIO/CoreMediaIO.h>

NS_ASSUME_NONNULL_BEGIN
enum { LBWidth = 3840, LBHeight = 2160, LBFPS = 30, LBFrameBytes = 33177600, LBPoolLimit = 6 };
FOUNDATION_EXPORT NSString *const LBDeviceUID;
FOUNDATION_EXPORT NSString *const LBSourceName;
FOUNDATION_EXPORT NSString *const LBSinkName;
FOUNDATION_EXPORT NSString *const LBHostSigningID;
FOUNDATION_EXPORT NSString *const LBDemandProperty;
enum { LBDemandSelector = 0x6c626364 }; // lbcd, global/main custom property.
FOUNDATION_EXPORT CMVideoFormatDescriptionRef _Nullable LBCreate4KFormat(void) CF_RETURNS_RETAINED;
FOUNDATION_EXPORT CVPixelBufferPoolRef _Nullable LBCreate4KPool(void) CF_RETURNS_RETAINED;
FOUNDATION_EXPORT BOOL LBValidateSample(CMSampleBufferRef sample);
FOUNDATION_EXPORT uint64_t LBHostNanoseconds(void);
FOUNDATION_EXPORT void LBEmitJSON(NSDictionary *value);

/// One pending IOSurface pixel buffer; publishing drops the previous buffer.
@interface LBLatestFrame : NSObject
@property(nonatomic, readonly) uint64_t sequence;
- (BOOL)publishSample:(CMSampleBufferRef)sample;
- (nullable CVPixelBufferRef)copyPixelBufferAtHostTime:(uint64_t)now sequence:(uint64_t * _Nullable)sequence CF_RETURNS_RETAINED;
- (void)clear;
@end
NS_ASSUME_NONNULL_END
