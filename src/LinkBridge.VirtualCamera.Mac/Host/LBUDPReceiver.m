#import "LBUDPReceiver.h"
#import <arpa/inet.h>
#import <poll.h>
#import <unistd.h>

@implementation LBUDPReceiver { NSTask *_task; NSPipe *_out, *_err; NSLock *_lock; uint64_t _generation; }
- (instancetype)init { if ((self = [super init])) _lock = [NSLock new]; return self; }
- (BOOL)running { [_lock lock]; BOOL value = _task.running; [_lock unlock]; return value; }
- (BOOL)startPeer:(NSString *)peer port:(NSInteger)port ffmpeg:(NSString *)ffmpeg frame:(void (^)(NSData *))frame error:(void (^)(NSString *))error {
    struct in_addr address;
    if (inet_pton(AF_INET, peer.UTF8String, &address) != 1 || port < 1025 || port > 65535 || ![NSFileManager.defaultManager isExecutableFileAtPath:ffmpeg]) {
        error(@"Invalid peer/UDP port or FFmpeg executable."); return NO;
    }
    uint32_t host = ntohl(address.s_addr);
    if ((host >> 24) == 0 || (host >> 24) >= 224) { error(@"Camera peer must be unicast IPv4."); return NO; }
    [self stop];
    NSTask *task = [NSTask new]; NSPipe *out = [NSPipe pipe], *err = [NSPipe pipe];
    task.executableURL = [NSURL fileURLWithPath:ffmpeg]; task.standardOutput = out; task.standardError = err;
    task.arguments = @[@"-hide_banner", @"-loglevel", @"info", @"-nostdin", @"-fflags", @"nobuffer", @"-flags", @"low_delay",
        @"-probesize", @"1048576", @"-analyzeduration", @"500000",
        @"-i", [NSString stringWithFormat:@"udp://0.0.0.0:%ld?fifo_size=4096&overrun_nonfatal=1&timeout=8000000&sources=%@", (long)port, peer],
        @"-map", @"0:v:0", @"-an", @"-vf", @"showinfo", @"-c:v", @"rawvideo", @"-pix_fmt", @"bgra", @"-f", @"rawvideo", @"pipe:1"];
    NSError *launch = nil;
    if (![task launchAndReturnError:&launch]) { error(launch.localizedDescription); return NO; }
    [_lock lock]; _task = task; _out = out; _err = err; uint64_t generation = ++_generation; [_lock unlock];
    __weak LBUDPReceiver *weak = self;
    NSLock *metadataLock = [NSLock new]; __block BOOL confirmed = NO, inputSection = NO;
    __block uint64_t validatedFrames = 0;
    NSMutableData *lines = [NSMutableData new];
    err.fileHandleForReading.readabilityHandler = ^(NSFileHandle *handle) {
        NSData *bytes = handle.availableData; if (!bytes.length) { handle.readabilityHandler = nil; return; }
        @synchronized(lines) {
            if (lines.length + bytes.length > 65536) [lines setLength:0]; [lines appendData:bytes];
            while (true) {
                const uint8_t *data = lines.bytes; NSUInteger length = lines.length, end = 0;
                while (end < length && data[end] != '\n') end++; if (end == length) break;
                NSString *line = [[NSString alloc] initWithBytes:data length:end encoding:NSUTF8StringEncoding] ?: @"";
                [lines replaceBytesInRange:NSMakeRange(0,end+1) withBytes:NULL length:0];
                [metadataLock lock];
                if ([line hasPrefix:@"Input #0"]) inputSection = YES; else if ([line hasPrefix:@"Output #"]) inputSection = NO;
                if (inputSection && [line containsString:@"Video:"]) {
                    NSRegularExpression *regex = [NSRegularExpression regularExpressionWithPattern:@"\\b([0-9]{2,5})x([0-9]{2,5})\\b" options:0 error:NULL];
                    NSTextCheckingResult *match = [regex firstMatchInString:line options:0 range:NSMakeRange(0,line.length)];
                    if (match) {
                        NSInteger width = [[line substringWithRange:[match rangeAtIndex:1]] integerValue];
                        NSInteger height = [[line substringWithRange:[match rangeAtIndex:2]] integerValue];
                        if (width == LBWidth && height == LBHeight) confirmed = YES;
                        else { [metadataLock unlock]; error(@"Remote input is not native 3840×2160; refusing upscale."); [weak stop]; return; }
                    }
                }
                if ([line containsString:@"showinfo"] && [line containsString:@" n:"]) {
                    NSRegularExpression *geometry = [NSRegularExpression regularExpressionWithPattern:@"\\bn:\\s*([0-9]+).*\\bs:([0-9]+)x([0-9]+)" options:0 error:NULL];
                    NSTextCheckingResult *match = [geometry firstMatchInString:line options:0 range:NSMakeRange(0,line.length)];
                    if (match) {
                        NSInteger width = [[line substringWithRange:[match rangeAtIndex:2]] integerValue];
                        NSInteger height = [[line substringWithRange:[match rangeAtIndex:3]] integerValue];
                        if (width != LBWidth || height != LBHeight) { [metadataLock unlock]; error(@"Decoded frame changed away from native 4K; refusing frame."); [weak stop]; return; }
                        validatedFrames = MAX(validatedFrames, (uint64_t)[[line substringWithRange:[match rangeAtIndex:1]] longLongValue] + 1);
                    }
                } [metadataLock unlock];
            }
        }
    };
    dispatch_async(dispatch_get_global_queue(QOS_CLASS_USER_INITIATED,0), ^{
        NSMutableData *buffer = [NSMutableData dataWithLength:LBFrameBytes]; size_t offset = 0;
        LBUDPReceiver *initial = weak; if (!initial) return;
        [initial->_lock lock];
        int fd = initial->_generation == generation && initial->_task == task ? dup(out.fileHandleForReading.fileDescriptor) : -1;
        [initial->_lock unlock];
        if (fd < 0) return;
        uint64_t begin = LBHostNanoseconds(), frames = 0;
        while (true) {
            LBUDPReceiver *self = weak; if (!self) break;
            [self->_lock lock]; BOOL current = self->_generation == generation && self->_task == task; [self->_lock unlock]; if (!current) break;
            [metadataLock lock]; BOOL metadata = confirmed; [metadataLock unlock];
            if (!metadata && LBHostNanoseconds() - begin > 8000000000) { error(@"No confirmed remote native 4K metadata within 8 seconds."); break; }
            struct pollfd descriptor = { fd, POLLIN, 0 }; int ready = poll(&descriptor,1,100);
            if (ready < 0) { if (errno == EINTR) continue; break; }
            if (ready == 0) continue;
            ssize_t count = read(fd,(uint8_t *)buffer.mutableBytes + offset,LBFrameBytes - offset); if (count <= 0) break;
            offset += (size_t)count;
            if (offset == LBFrameBytes) {
                // Every emitted raw frame needs a matching decoder geometry record.
                // This also rejects mid-stream dimension changes, not just the first header.
                uint64_t waitStart = LBHostNanoseconds(); BOOL valid = NO;
                while (LBHostNanoseconds() - waitStart < 500000000) {
                    [metadataLock lock]; valid = confirmed && validatedFrames > frames; [metadataLock unlock];
                    if (valid) break;
                    [self->_lock lock]; BOOL alive = self->_generation == generation; [self->_lock unlock]; if (!alive) break;
                    usleep(1000);
                }
                if (!valid) { error(@"Raw frame lacks matching native 4K geometry proof."); break; }
                frame(buffer); frames++; offset = 0;
            }
        }
        close(fd);
        LBUDPReceiver *self = weak;
        if (self) { [self->_lock lock]; BOOL current = self->_generation == generation && self->_task == task; [self->_lock unlock];
            if (current) { error(@"Remote camera decoder stopped."); [self stop]; } }
    });
    return YES;
}
- (void)stop {
    [_lock lock]; ++_generation; NSTask *task = _task; NSPipe *out = _out, *err = _err; _task = nil; _out = _err = nil; [_lock unlock];
    err.fileHandleForReading.readabilityHandler = nil;
    // Stream receiver owns no user files; terminate this exact child synchronously
    // even when its host exits before asynchronous cleanup could run.
    if (task.running) kill(task.processIdentifier, SIGKILL);
    [out.fileHandleForReading closeFile]; [err.fileHandleForReading closeFile];
}
- (void)dealloc { [self stop]; }
@end
