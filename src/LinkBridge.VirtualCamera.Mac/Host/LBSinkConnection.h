#import "../Shared/LBCameraContract.h"
NS_ASSUME_NONNULL_BEGIN
@interface LBSinkConnection : NSObject
@property(nonatomic, readonly) BOOL found;
- (OSStatus)discover;
- (nullable NSDictionary *)readDemand;
- (OSStatus)start;
- (OSStatus)pushBGRA:(NSData *)frame;
- (void)stop;
@end
NS_ASSUME_NONNULL_END
