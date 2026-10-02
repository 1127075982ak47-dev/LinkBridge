# Mac camera extension · experimental

This Objective-C host, Core Media IO system extension and native decoder use Apple's CMIOExtension, IOSurface and AVFoundation APIs. The source format is 3840×2160 BGRA at 30fps. It does not upscale a preview or claim that an unsigned extension is an installed camera.

## Build and offline checks

From the repository root on a Mac with Xcode/macOS SDK:

```sh
sh tools/MacCameraBuild/build.sh
sh tools/MacCameraProbe/probe.sh
sh tools/MacCameraProbe/probe.sh --decode /absolute/path/to/ffmpeg
```

The build creates an unsigned host with an embedded system extension under `artifacts/mac-camera-unsigned`. It does not install or activate that extension. The packaging placeholder is not an Apple Team identity.

Offline checks exercise real 4K pixel/sample buffers, stream format and bounded queues; the decoder checks incoming dimensions and rejects 1080 input. Synthetic format or decoder checks are separate from actual installed-camera application acceptance.

## Runtime design

The extension exposes explicit source/sink streams and consumer demand. The signed host observes demand; the parent coordinator authenticates CAMERA2 control before enabling the real producer. Latest-frame buffers are bounded and stale data is cleared. Missing input creates no fabricated camera sample.

The host does not receive the pairing secret. Parent confirmation is passed through inherited local IPC, not an unauthenticated public listener. Experimental UDP video remains outside an end-to-end encryption claim.

Public distribution and activation require an appropriate Apple developer identity, entitlements, bundle placement and explicit system extension approval. Local desktop-app signing does not satisfy those extension requirements.

Official references: [Apple camera extension guide](https://developer.apple.com/documentation/coremediaio/creating-a-camera-extension-with-core-media-i-o), [WWDC22 session](https://developer.apple.com/videos/play/wwdc2022/10022/).

See [BUILDING](../../docs/BUILDING.md), [attributions](../../docs/ATTRIBUTIONS.md) and [verification](../../docs/VERIFICATION.md).
