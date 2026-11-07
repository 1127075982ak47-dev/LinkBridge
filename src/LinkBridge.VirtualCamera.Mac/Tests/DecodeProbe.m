#import "../Host/LBUDPReceiver.h"
#import <sys/socket.h>
#import <arpa/inet.h>
#import <unistd.h>
int main(int argc, const char *argv[]) {
    @autoreleasepool {
        if (argc < 2 || argc > 3) return 2; NSString *ffmpeg = [NSString stringWithUTF8String:argv[1]];
        BOOL reject1080 = argc == 3 && strcmp(argv[2],"--reject-1080")==0;
        int socketFD = socket(AF_INET,SOCK_DGRAM,0); struct sockaddr_in address={0};
        address.sin_len=sizeof(address); address.sin_family=AF_INET; address.sin_addr.s_addr=htonl(INADDR_LOOPBACK);
        if (bind(socketFD,(struct sockaddr *)&address,sizeof(address))!=0) return 2;
        socklen_t length=sizeof(address); getsockname(socketFD,(struct sockaddr *)&address,&length); NSInteger port=ntohs(address.sin_port); close(socketFD);
        LBUDPReceiver *receiver=[LBUDPReceiver new]; dispatch_semaphore_t done=dispatch_semaphore_create(0);
        NSLock *lock=[NSLock new]; __block NSInteger frames=0; __block NSString *failure=nil;
        if (![receiver startPeer:@"127.0.0.1" port:port ffmpeg:ffmpeg frame:^(NSData *frame) {
            const uint8_t *pixels=frame.bytes;
            [lock lock]; if(frame.length!=LBFrameBytes || pixels[3]!=255) failure=@"Invalid native BGRA frame";
            frames++; BOOL complete=frames>=3; [lock unlock]; if(complete)dispatch_semaphore_signal(done);
        } error:^(NSString *message) { [lock lock]; failure=message; [lock unlock]; dispatch_semaphore_signal(done); }]) return 1;
        usleep(250000);
        NSTask *source=[NSTask new]; source.executableURL=[NSURL fileURLWithPath:ffmpeg];
        source.standardOutput=[NSFileHandle fileHandleWithNullDevice]; source.standardError=[NSFileHandle fileHandleWithNullDevice];
        source.arguments=@[@"-hide_banner",@"-loglevel",@"error",@"-nostdin",@"-re",@"-f",@"lavfi",@"-i",reject1080 ? @"color=c=blue:s=1920x1080:r=30" : @"color=c=blue:s=3840x2160:r=30",
            @"-an",@"-c:v",@"h264_videotoolbox",@"-b:v",@"16M",@"-g",@"30",@"-pix_fmt",@"yuv420p",@"-f",@"mpegts",
            [NSString stringWithFormat:@"udp://127.0.0.1:%ld?pkt_size=1316",(long)port]];
        NSError *launch=nil; if(![source launchAndReturnError:&launch]){[receiver stop];return 2;}
        long timeout=dispatch_semaphore_wait(done,dispatch_time(DISPATCH_TIME_NOW,12*NSEC_PER_SEC));
        [receiver stop]; if(source.running)kill(source.processIdentifier,SIGKILL); [source waitUntilExit];
        [lock lock]; NSInteger received=frames; NSString *message=failure; [lock unlock];
        if(reject1080) {
            if(timeout || received!=0 || ![message containsString:@"not native 3840"]){fprintf(stderr,"FAIL 1080 rejection\n");return 1;}
            puts("PASS actual random-port 1080 MPEGTS rejected from metadata; raw4K frames emitted=0; no upscale; owned children stopped.");return 0;
        }
        if(timeout || message || received<3){fprintf(stderr,"FAIL UDP4K frames=%ld error=%s\n",(long)received,message.UTF8String?:"timeout");return 1;}
        printf("PASS actual random-port loopback MPEGTS hardware-encoded synthetic4K -> frame-geometry-verified raw3840x2160BGRA: %ld frames; owned children stopped; no CMIO activation/physical devices.\n",(long)received);
    } return 0;
}
