# LinkBridge · 0.0.4-beta.1

A local-network Windows + Apple Silicon Mac workspace with Windows-hosted keyboard/mouse control, a configurable screen seam, text/PNG clipboard sharing and bidirectional file copy/paste.

[中文](README.md) · [Download the public beta](https://github.com/1127075982ak47-dev/LinkBridge/releases/tag/v0.0.4-beta.1)

The complete friend-testing bundle includes the Windows installer, Mac package, detailed offline Chinese manual, WorkBuddy/Codex agent installation handoff, source snapshot, checksums and verification report. Both apps include their .NET runtime. SDKs, Python and FFmpeg are not prerequisites for end users.

Supported beta path: Windows 11 x64 controller + macOS 15+ Apple Silicon receiver, one selected cross-machine seam. Configure top/bottom/left/right, seam coverage and dwell time in Screen Layout. Ctrl+Alt+M returns control to Windows. Generate one private pairing set on Windows, import the matching files on both machines, and complete macOS permission prompts personally.

Audio, camera, microphone, window roaming, advanced multi-computer workspaces, Linux, Intel Mac and Windows ARM are outside this beta. Windows is not Authenticode-signed; the Mac app is ad-hoc signed and not notarized. Installation refuses to replace a Mac app with a different signing requirement. No security controls are disabled automatically.

See [installation](docs/INSTALLING.md), [build instructions](docs/BUILDING.md), [agent handoff](docs/agent-install/AGENTS.md) and [verification boundaries](docs/VERIFICATION.md). Protocol tests do not establish fresh-machine authorization, physical gestures, every display/IME combination or long-duration reliability.

MIT for project code; preserve all [third-party notices](THIRD_PARTY_NOTICES.md). Never include private pairing profiles, signing material, personal clipboard data or unsanitized logs in public issues.
