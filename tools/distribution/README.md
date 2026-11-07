# LinkBridge 0.0.4-beta.1 public beta

Release metadata: release.json. Windows 11 x64 controller + macOS 15+ Apple Silicon receiver. Scope: keyboard/mouse, one screen seam, text/PNG clipboard and bidirectional file copy/paste. Media, window roaming and advanced multi-computer features are outside this beta.

Manual: ../../docs/customer/LinkBridge用户说明书.md. Agent handoff: beta-docs/AGENTS.md.

Windows build: pwsh -File tools/distribution/Build-WindowsRelease.ps1. Installer: Build-CustomerInstaller.ps1 with -NsisPath and docs/customer HTML manual. Developer build requires PowerShell 7.2+, customer setup uses Windows PowerShell 5.1.

Mac build: sh tools/distribution/build-mac-release.sh on Apple Silicon with .NET 10 SDK, Xcode command line tools and Python 3. Standalone public build only: no installed app is replaced. Ad-hoc only, no Developer ID or notarization; differing existing signing requirements are refused. Windows has no Authenticode signature.

Generate one private pairing set per user pair. Preserve settings, backups, licenses and hash manifests. Never distribute private profiles or developer credentials.
