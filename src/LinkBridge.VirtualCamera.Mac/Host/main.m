#import <AppKit/AppKit.h>
#import <SystemExtensions/SystemExtensions.h>
#import <Security/Security.h>
#import "LBSinkConnection.h"
#import "LBUDPReceiver.h"
#import <fcntl.h>
#import <signal.h>
#import <unistd.h>

static NSString *const LBExtensionID = @"com.linkbridge.camera.extension";
static NSString *LBTeam(void) {
    SecCodeRef code = NULL; CFDictionaryRef info = NULL; SecRequirementRef apple = NULL; NSString *team = nil;
    if (SecCodeCopySelf(kSecCSDefaultFlags,&code) == errSecSuccess &&
        SecRequirementCreateWithString(CFSTR("anchor apple generic"),kSecCSDefaultFlags,&apple) == errSecSuccess &&
        SecCodeCheckValidity(code,kSecCSStrictValidate,apple) == errSecSuccess &&
        SecCodeCopySigningInformation(code,kSecCSSigningInformation,&info) == errSecSuccess)
        team = [((__bridge NSDictionary *)info)[(__bridge id)kSecCodeInfoTeamIdentifier] copy];
    if (apple) CFRelease(apple); if (info) CFRelease(info); if (code) CFRelease(code); return team;
}
@interface LBActivation : NSObject <OSSystemExtensionRequestDelegate>
@end
@implementation LBActivation
- (void)requestNeedsUserApproval:(OSSystemExtensionRequest *)request { (void)request; LBEmitJSON(@{@"event":@"activation",@"state":@"requiresUserApproval"}); }
- (OSSystemExtensionReplacementAction)request:(OSSystemExtensionRequest *)request actionForReplacingExtension:(OSSystemExtensionProperties *)existing withExtension:(OSSystemExtensionProperties *)replacement {
    (void)request; (void)existing; (void)replacement; return OSSystemExtensionReplacementActionReplace;
}
- (void)request:(OSSystemExtensionRequest *)request didFinishWithResult:(OSSystemExtensionRequestResult)result {
    (void)request; LBEmitJSON(@{@"event":@"activation",@"state": result == OSSystemExtensionRequestCompleted ? @"completed" : @"requiresRestart",@"result":@(result)}); exit(0);
}
- (void)request:(OSSystemExtensionRequest *)request didFailWithError:(NSError *)error { (void)request; LBEmitJSON(@{@"event":@"activation",@"state":@"failed",@"error":error.localizedDescription}); exit(2); }
@end

int main(int argc, const char *argv[]) {
    @autoreleasepool {
        signal(SIGPIPE,SIG_IGN);
        fcntl(STDOUT_FILENO,F_SETFL,fcntl(STDOUT_FILENO,F_GETFL,0)|O_NONBLOCK);
        NSArray<NSString *> *args = NSProcessInfo.processInfo.arguments;
        NSString *mode = args.count > 1 ? args[1] : @"--status";
        if ([mode isEqual:@"--activate"] || [mode isEqual:@"--deactivate"] || [mode isEqual:@"--check-activation"]) {
            NSString *team = LBTeam(); NSString *bundle = NSBundle.mainBundle.bundlePath;
            BOOL eligible = team.length == 10 && [bundle hasPrefix:@"/Applications/"] &&
                [NSFileManager.defaultManager fileExistsAtPath:[bundle stringByAppendingPathComponent:@"Contents/Library/SystemExtensions/com.linkbridge.camera.extension.systemextension"]];
            if ([mode isEqual:@"--check-activation"] || !eligible) {
                LBEmitJSON(@{@"event":@"activationPrerequisites",@"eligible":@(eligible),@"hasAppleTeam":@((BOOL)(team.length==10)),
                    @"inApplications":@([bundle hasPrefix:@"/Applications/"]),@"note":@"Legal Apple signing/provisioning and approval must be verified before activation."});
                return eligible ? 0 : 2;
            }
            [NSApplication sharedApplication]; [NSApp setActivationPolicy:NSApplicationActivationPolicyAccessory];
            static LBActivation *delegate; delegate = [LBActivation new];
            OSSystemExtensionRequest *request = [mode isEqual:@"--activate"]
                ? [OSSystemExtensionRequest activationRequestForExtension:LBExtensionID queue:dispatch_get_main_queue()]
                : [OSSystemExtensionRequest deactivationRequestForExtension:LBExtensionID queue:dispatch_get_main_queue()];
            request.delegate = delegate; [OSSystemExtensionManager.sharedManager submitRequest:request]; [NSApp run]; return 0;
        }
        LBSinkConnection *sink = [LBSinkConnection new]; OSStatus status = [sink discover];
        if ([mode isEqual:@"--status"]) { LBEmitJSON(@{@"v":@1,@"found":@(sink.found),@"status":@(status),@"width":@(LBWidth),@"height":@(LBHeight),@"fps":@(LBFPS)}); return 0; }
        if (![mode isEqual:@"--observe"] && ![mode isEqual:@"--bridge"]) { fprintf(stderr,"Usage: --status | --check-activation | --activate | --deactivate | --observe | --bridge PEER UDPPORT FFMPEG\n"); return 2; }
        BOOL bridge = [mode isEqual:@"--bridge"];
        if (bridge && args.count != 5) return 2;
        NSString *peer = bridge ? args[2] : @""; NSInteger port = bridge ? args[3].integerValue : 0; NSString *ffmpeg = bridge ? args[4] : @"";
        LBUDPReceiver *decoder = [LBUDPReceiver new]; NSLock *gate = [NSLock new];
        signal(SIGTERM,SIG_IGN); signal(SIGINT,SIG_IGN);
        dispatch_source_t terminate = dispatch_source_create(DISPATCH_SOURCE_TYPE_SIGNAL,SIGTERM,0,dispatch_get_main_queue());
        dispatch_source_t interrupt = dispatch_source_create(DISPATCH_SOURCE_TYPE_SIGNAL,SIGINT,0,dispatch_get_main_queue());
        dispatch_block_t cleanup = ^{ [decoder stop]; [sink stop]; exit(0); };
        dispatch_source_set_event_handler(terminate,cleanup); dispatch_source_set_event_handler(interrupt,cleanup);
        dispatch_resume(terminate); dispatch_resume(interrupt);
        __block NSString *generation = nil; __block BOOL pairedReady = NO; __block NSInteger previousCount = -1;
        __block uint64_t lastReport = 0, sourceGeneration = 0;
        __block uint64_t nextDecoderAttempt = 0;
        void (^startDecoder)(NSString *) = ^(NSString *current) {
            OSStatus opened = [sink start];
            if (opened != noErr) { LBEmitJSON(@{@"event":@"error",@"stage":@"sinkStart",@"status":@(opened)}); return; }
            [decoder startPeer:peer port:port ffmpeg:ffmpeg frame:^(NSData *frame) {
                [gate lock]; BOOL allowed = pairedReady && [generation isEqual:current]; [gate unlock];
                if (allowed) { OSStatus pushed = [sink pushBGRA:frame]; if (pushed != noErr && pushed != kCMSimpleQueueError_QueueIsFull && pushed != kCVReturnWouldExceedAllocationThreshold)
                    LBEmitJSON(@{@"event":@"error",@"stage":@"sink",@"status":@(pushed)}); }
            } error:^(NSString *message) { LBEmitJSON(@{@"event":@"error",@"stage":@"decode",@"message":message}); }];
        };
        if (bridge) dispatch_async(dispatch_get_global_queue(QOS_CLASS_UTILITY,0), ^{
            char *line = NULL; size_t capacity = 0;
            while (getline(&line,&capacity,stdin) > 0) {
                if (capacity > 8192) break;
                NSData *data = [[NSString stringWithUTF8String:line] dataUsingEncoding:NSUTF8StringEncoding];
                NSDictionary *message = [NSJSONSerialization JSONObjectWithData:data options:0 error:NULL];
                [gate lock]; if ([message isKindOfClass:NSDictionary.class] && [message[@"v"] integerValue] == 1 &&
                    [message[@"generation"] isEqual:generation]) {
                    if ([message[@"event"] isEqual:@"pairedReady"]) pairedReady = YES;
                    else if ([message[@"event"] isEqual:@"pairedStopped"]) pairedReady = NO;
                } [gate unlock];
            }
            free(line); [decoder stop]; [sink stop]; exit(0);
        });
        dispatch_source_t timer = dispatch_source_create(DISPATCH_SOURCE_TYPE_TIMER,0,0,dispatch_get_main_queue());
        dispatch_source_set_timer(timer,DISPATCH_TIME_NOW,250*NSEC_PER_MSEC,10*NSEC_PER_MSEC);
        dispatch_source_set_event_handler(timer, ^{
            if (!sink.found) [sink discover]; NSDictionary *demand = [sink readDemand];
            NSInteger count = demand ? [demand[@"consumers"] integerValue] : 0;
            uint64_t epoch = [demand[@"generation"] unsignedLongLongValue], now = LBHostNanoseconds();
            if (count > 0 && (previousCount <= 0 || epoch != sourceGeneration)) {
                [decoder stop]; [sink stop]; sourceGeneration = epoch;
                [gate lock]; generation = NSUUID.UUID.UUIDString; pairedReady = NO; NSString *current = generation; [gate unlock];
                LBEmitJSON(@{@"v":@1,@"event":@"start",@"generation":current,@"consumers":@(count),@"port":@(port),@"width":@(LBWidth),@"height":@(LBHeight),@"fps":@(LBFPS)});
                if (bridge) { nextDecoderAttempt = now + 2000000000; startDecoder(current); }
            } else if (count == 0 && previousCount > 0) {
                [gate lock]; pairedReady = NO; NSString *current = generation; generation = nil; [gate unlock];
                [decoder stop]; [sink stop]; LBEmitJSON(@{@"v":@1,@"event":@"stop",@"generation":current ?: @"",@"consumers":@0});
            }
            if (bridge && count > 0 && !decoder.running && now >= nextDecoderAttempt) {
                [gate lock]; NSString *current = generation; [gate unlock];
                nextDecoderAttempt = now + 2000000000; if (current) startDecoder(current);
            }
            if (count != previousCount || now - lastReport > 1000000000) {
                [gate lock]; NSString *current = generation; [gate unlock];
                uint64_t sent = [demand[@"lastFrameHostNs"] unsignedLongLongValue];
                BOOL fresh = sent && now >= sent && now - sent < 2000000000;
                LBEmitJSON(@{@"v":@1,@"event":@"heartbeat",@"generation":current ?: @"",@"consumers":@(count),@"found":@(sink.found),
                    @"framesDelivered":demand[@"framesDelivered"] ?: @0,@"freshFrames":@(fresh)}); lastReport = now;
            } previousCount = count;
        }); dispatch_resume(timer); dispatch_main();
    } (void)argc; (void)argv; return 0;
}
