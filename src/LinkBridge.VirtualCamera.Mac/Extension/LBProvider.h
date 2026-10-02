#import "../Shared/LBCameraContract.h"
@interface LBProvider : NSObject <CMIOExtensionProviderSource>
@property(nonatomic, readonly) CMIOExtensionProvider *provider;
@end
