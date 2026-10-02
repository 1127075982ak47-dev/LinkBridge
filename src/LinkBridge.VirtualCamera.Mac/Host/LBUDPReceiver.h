#import "../Shared/LBCameraContract.h"
NS_ASSUME_NONNULL_BEGIN
@interface LBUDPReceiver : NSObject
@property(nonatomic, readonly) BOOL running;
- (BOOL)startPeer:(NSString *)peer port:(NSInteger)port ffmpeg:(NSString *)ffmpeg
    frame:(void (^)(NSData *frame))frame error:(void (^)(NSString *message))error;
- (void)stop;
@end
NS_ASSUME_NONNULL_END
