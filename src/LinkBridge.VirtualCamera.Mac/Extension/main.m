#import "LBProvider.h"
int main(int argc, const char *argv[]) {
    (void)argc; (void)argv;
    @autoreleasepool {
        static LBProvider *source; source = [LBProvider new];
        [CMIOExtensionProvider startServiceWithProvider:source.provider];
        CFRunLoopRun();
    } return 0;
}
