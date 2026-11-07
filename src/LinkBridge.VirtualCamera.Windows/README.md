# LinkBridge Windows software camera (isolated native feature)

This directory implements a real Windows Media Foundation COM software-camera source. It is derived from Microsoft's MIT-licensed Windows-Camera sample, pinned to `626f8b19c5f367602f2e89c6b314573d3776c9df`. Original files are in `upstream/`, and the original Microsoft license is in `LICENSE-Microsoft`. WIL is a separately licensed, pinned dependency in `deps/wil`.

The source preserves the official sample's bounded ten-sample MF allocator pool. Actual registered FrameServer testing showed a four-sample pool can fail a second consumer with MF_E_SAMPLEALLOCATOR_EMPTY, so reducing it is not an acceptable optimization. A fixed two-slot shared buffer and two stable/scratch snapshots bound transport memory. Actual FrameServer working-set/performance needs consumer measurement.

The source advertises RGB32 (BGRA byte layout), 3840×2160@30 first and 1920×1080@30 second. 1080 output box-averages the real 4K input. A missing/stale producer generates black with the explicit text `LinkBridge - Camera offline`; it never substitutes a fake camera image. 960×540 UI preview is not an input to this source.

## Build and current gate

Run `./build.ps1`. Requires CMake, Visual Studio 2022 C++ BuildTools and Windows SDK >=22621. Built artifacts are `build/Release/LinkBridgeCameraSourceV2.dll`, `LinkBridgeCameraHelperV2.exe`, `LinkBridgeCameraNativeProbe.exe`. The native probe loads the COM DLL directly without COM registration and uses an isolated Local mapping/pipe namespace. Neither building nor running `--info` or the probe registers any device.

A successful native DLL/media-type/shared-frame probe is not end-to-end camera completion. The pending gate is registered FrameServer activation, authenticated Mac camera capture, real remote 4K frames and a consumer (`getUserMedia`/system capture) reading 3840×2160 without upscale. The parent has now installed and registered the isolated camera under `C:\Program Files\LinkBridge\Camera`; the registered-platform probe reads actual 4K offline frames through FrameServer, including two independent consumers. Current V2 runtime is `C:\Program Files\LinkBridge\Camera\v2-0df6f8b7`. Real Mac content remains pending.

## Broker names and SID API

Call `LinkBridgeCameraHelperV2.exe --info-json` from the intended normal user's context. It prints a single JSON object with `ownerSid`, `sourceClsid`, `mappingName` (full Global name), `pipeName` (unprefixed NamedPipeServerStream name), sizes/dimensions/stride and BGRA FourCC. This command reads current token metadata only and makes no registration changes. `--info` prints a human-readable equivalent plus startup HRESULTs.

Production mapping: `Global\LinkBridge.Camera.Frame.v1.<ownerSid>`

Production events pipe: `\\.\pipe\LinkBridge.Camera.Events.v1.<ownerSid>`

The current user's SID is persisted as the custom IMFAttributes key `LINKBRIDGE_CAMERA_USER_SID`, GUID `{7D7A77EA-BE87-42ED-9171-1B3A457D06C7}`, when the helper creates the camera. FrameServer passes activation attributes to the source. Missing/invalid SID is a clear HRESULT failure; it never derives a SID from the FrameServer service account.

The native source creates the Global mapping on the first actual stream Start, not on enumeration. Its DACL is only that user, SYSTEM and LOCAL SERVICE (`D:P(A;;GA;;;SY)(A;;GA;;;LS)(A;;GA;;;<ownerSid>)`). The .NET broker opens the existing mapping, retries briefly while first Start finishes and never silently creates a different mapping. Global creation requires privilege in the FrameServer context; a failure must be reported, not bypassed with Everyone ACL or a different namespace. Actual service activation is still untested.

The broker creates the events pipe with ACL for those same identities and validates client process identity/SID. The device source ID is `{0DF6F8B7-A2E5-4F7B-9CCD-8AB144574F1E}`; it is distinct from Microsoft's sample CLSID.

## Shared BGRA layout (little endian)

Header: 4096 bytes. Each slot: 33177600 bytes. Slot 0 starts at 4096; slot 1 starts at 33181696. Total mapping: 66359296 bytes. No stride padding in the producer slots.

| Offset | Type | Value / owner |
|---|---|---|
| 0 | byte[8] | `LBCAM01\0` (native init) |
| 8 | uint32 | version=1 |
| 12 | uint32 | headerBytes=4096 |
| 16 | uint32 | width=3840 |
| 20 | uint32 | height=2160 |
| 24 | uint32 | stride=15360 |
| 28 | uint32 | FourCC BGRA=0x41524742 (1095911234) |
| 32 | uint64 | frameBytes=33177600 |
| 40 | int64 | sequence: odd while writer is changing pixels/header; even after commit |
| 48 | int32 | activeSlot 0 or 1 |
| 52 | int32 | status 0=offline, 1=live |
| 56 | int64 | timestamp100ns, QPC/Stopwatch counter scaled to 100ns |
| 64 | int64 | producerPid |
| 72 | int64 | heartbeatTickMs: `GetTickCount64` / `.NET Environment.TickCount64` |
| 80..4095 | bytes | reserved=0 |

Only one broker writer is allowed. Validate immutable fields before writing. Use aligned `Interlocked`/acquire-release access for shared counters:

1. Choose `inactive = 1 - activeSlot`.
2. Increment sequence to odd with Interlocked.
3. Copy the full real 4K BGRA frame into inactive slot.
4. Atomically store activeSlot, status=1, timestamp100ns, producerPid, heartbeatTickMs.
5. Memory barrier, increment sequence to even.

On stop/disconnect, atomically set status=0. Do not refresh the heartbeat to simulate a working camera when no frames arrive. Native reader rejects stale heartbeat >3 seconds. It copies a slot to a separate scratch snapshot and commits it only when before/after sequence match; a busy writer retains the last stable frame, while offline/stale state immediately shows the explicit placeholder. Do not reuse 960×540 preview data or resize it to 4K.

## Consumer lease messages

The native stream source emits one short pipe connection per UTF-8 JSON line (newline terminated):

`{"v":1,"event":"start","instance":"{GUID}","pid":123,"width":3840,"height":2160,"fps":30}`

`event` is `start`, `heartbeat`, or `stop`. An active source sends heartbeat every 1 second. The `instance` is the native-source lease identity and must key consumer state; pid alone is insufficient. Each source instance has one stream. Repeat Start does not increment the lease. Different active source instances have different GUIDs.

The broker should create a lease on start, refresh on heartbeat, remove only that lease on stop, expire after 8 seconds or verified process exit and run remote capture while at least one lease remains. Last lease closing stops capture and marks shared status offline. Received width/height is the requesting consumer's format; the broker still captures/writes 3840×2160 into the fixed shared layout, even for 1080 consumers. NamedPipeServerStream should accept repeated connections; do not treat each connection close as camera stop.

The source never sends pair secrets or keystrokes over IPC. Notifications happen on a worker, not in sample-copy work. A broker disconnect/restart is healed by subsequent heartbeat. Pipe wait/write are bounded.

## Registration helper (review before execution)

- `--info` / `--info-json`: read-only metadata.
- `--register-source ABSOLUTE_DLL`: explicit elevated HKLM 64-bit COM registration, ThreadingModel=Both. Only the parent performs installation/deployment.
- `--create`: MFCreateVirtualCamera(System lifetime, CurrentUser access, `联桥共享摄像头`), pass ownerSid, Start device registration. Records tuple/device id in `%LOCALAPPDATA%\LinkBridge\VirtualCamera\device.txt`. Must run in the intended user context after source registration. Windows appends its virtual camera suffix.
- `--remove`: reopens the same stable creation tuple and calls Remove. Must run as the same user who created it.
- `--unregister-source`: explicit elevated removal of only LinkBridge's CLSID, after camera removal. Only the parent performs installation/deployment.

Do not register a DLL under this development directory for final delivery; first copy the reviewed runtime to a stable installation path. Do not use the Microsoft sample CLSID. No Mac security/SIP/signature changes are part of this feature.

## Required end-to-end evidence

Verify consumer negotiated settings and actual frames: 3840×2160@30, real source detail (no upscale), no sample backlog, sensible latency, Start when consumer opens, last Stop ends remote capture, two consumers do not interrupt each other, restart/reconnect, privacy refusal, native source/FrameServer errors, uninstall. Until those pass, show partial capability honestly in product UI.

## Registered-platform probe

`build/Release/LinkBridgeCameraFrameServerProbe.exe` enumerates and activates only the already registered LinkBridge camera, creates a Media Foundation SourceReader, selects exact native 3840×2160@30 RGB32 and reads 100 actual platform frames by default. `--frames N` accepts 2–900 frames. It checks monotonic timestamps, reports measured fps/minimum/maximum interval, checks current-user read/write access to the actual service-created Global mapping and prints its real DACL. It has a 45-second process-local watchdog and explicitly closes the reader/source. `--dual` adds a second independent registered platform SourceReader and checks it continues reading after the first closes. These commands do not register a camera, modify global FrameServer settings, or open physical cameras.

The offline placeholder gate is distinct from real Mac content. The registered test has proved platform activation, exact 4K frame dimensions and the intended Global ACL. It initially revealed that the four-frame allocator pool failed a second consumer, and the installed older DLL used coarse sleeps producing ~46.8ms timestamps; the reviewed source restores ten samples and uses a per-handle high-resolution waitable timer. The original CLSID stayed cached in the running FrameServer despite a new InprocServer32 path. The V2 source uses a new LinkBridge-owned CLSID and DLL filename, replacing only this application's endpoint; the global FrameServer service and its settings are unchanged. Actual loaded modules confirm V2. Registered single and dual probes now succeed with clean exit 0, about 29.75–29.85fps sample timestamps and no allocator-empty. No global timer policy is changed.

## V2 installation and rollback evidence

Current software source CLSID: `{0DF6F8B7-A2E5-4F7B-9CCD-8AB144574F1E}`. Current runtime DLL SHA256: `2D5A477B9812F72C193B0705C5258577A0AA24612236E3B5A3F8B3C3F3F6A3AA`.

Evidence files are under `evidence/`: old/new device tuples, scoped remove/register/create logs, loaded module paths, actual Global ACL, registered single and clean dual sample logs, DLL SHA and unchanged FrameServer PID. The V2 dual probe releases all samples and COM objects before MFShutdown, fixing a probe-only teardown access violation. It closes one consumer while another continues reading five 4K samples; final exit is 0.

The former CLSID `{B6A08E59-3C48-4A73-9F68-74B9AE19F2AD}` and old files remain for rollback, but its camera endpoint was removed. Do not run the old helper's `--create` during normal operation: it would restore a duplicate old endpoint and cached old code. For rollback, first run the V2 installed helper's `--remove`, then explicitly run the preserved v1 helper's `--create`; do not change unrelated CLSIDs or stop the global FrameServer service.

The format and lifecycle platform gate is passed with offline content. It remains necessary to verify real authenticated Mac imagery, browser getUserMedia exact 4K, long-running capture/performance and device startup after reboot. No physical camera was opened in these platform probes.
