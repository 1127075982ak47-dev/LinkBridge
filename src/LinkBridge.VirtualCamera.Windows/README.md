# Windows virtual camera · experimental

This native Media Foundation COM camera source derives from Microsoft's MIT-licensed Windows-Camera sample at commit `626f8b19c5f367602f2e89c6b314573d3776c9df`. Original sample files and notices are retained in `upstream/`, `LICENSE-Microsoft` and `THIRD-PARTY-NOTICES.txt`. WIL is restored as a pinned, separately licensed dependency.

## Build

Use Visual Studio 2022 C++ Build Tools, CMake and the Windows SDK version selected by `build.ps1`:

```powershell
./build.ps1
```

The script restores the fixed WIL package and verifies its SHA-256 before building. It does not register a camera or install a system service.

The source advertises 3840×2160@30 BGRA first and 1920×1080@30 second. The 1080 output downsamples real 4K input. Missing or stale input produces an explicit camera-offline frame; it never substitutes a captured image as proof of a working sensor.

Native allocator and two-consumer FrameServer behavior have been exercised in the development environment. That result does not prove that a physical Mac camera, authenticated producer and consuming application all work together. This full path remains experimental.

## Ownership and IPC

The helper's `--info-json` reports the current user's source CLSID, owner SID, mapping/pipe names and format metadata without registration changes. Production mapping and pipe names are scoped by that owner SID; local source state is not a public network control channel.

The source retains the upstream bounded sample allocator pool, and uses bounded frame buffers. Applications and FrameServer must be measured on the actual target machines before changing these limits.

See [project attributions](../../docs/ATTRIBUTIONS.md), [third-party notices](../../THIRD_PARTY_NOTICES.md) and [verification boundaries](../../docs/VERIFICATION.md).
