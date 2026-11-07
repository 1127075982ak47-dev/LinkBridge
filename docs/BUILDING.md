# Build LinkBridge 0.0.4-beta.1

End users should use [the prebuilt beta](INSTALLING.md). Developers need .NET 10 SDK. The supported delivery targets are Windows x64 and Apple Silicon macOS 15+.

```sh
dotnet restore LinkBridge.slnx
dotnet build LinkBridge.slnx -c Release
```

Windows self-contained release (PowerShell 7.2+):

```powershell
pwsh -File tools/distribution/Build-WindowsRelease.ps1
pwsh -File tools/distribution/Build-CustomerInstaller.ps1 -NsisPath C:/tools/NSIS/makensis.exe
```

The installer requires the included `docs/customer/LinkBridge用户说明书.html`. Version and channel metadata are in `tools/distribution/release.json`. Client install/pairing scripts support Windows PowerShell 5.1.

Mac native self-contained release, on Apple Silicon with Xcode Command Line Tools and Python 3:

```sh
sh tools/distribution/build-mac-release.sh
```

This produces an independent ad-hoc signed public candidate under artifacts/public-release. It does not replace an installed app, import signing identities or grant system permissions. Retain the existing identity for routine updates to an authorized local development app. Different requirements are refused by the public installer.

The optional experimental native window-capture library is not required for beta input/clipboard/file features. FFmpeg and media drivers are not bundled and are outside this beta.

## Regression probes

```sh
dotnet run --project tools/ScreenLayoutProbe -c Release
dotnet run --project tools/ClipboardProbe -c Release
dotnet run --project tools/FileTransferProbe -c Release
dotnet run --project tools/DiscoveryDatagramsProbe -c Release
dotnet run --project tools/ManualConnectionProbe -c Release
dotnet run --project tools/PairedTlsProbe -c Release -- input
dotnet run --project tools/PairedTlsProbe -c Release -- settings
```

On macOS, set TMPDIR to a real, writable directory without symlink ancestors when running FileTransferProbe; /var or /tmp aliases can correctly trigger symlink rejection. Do not weaken the transfer path checks to work around a test fixture.

DeviceProbe currently produces nine CA1416 platform-analysis warnings when building the complete solution; the beta source build had zero errors. The actual release checks and untested native scenarios are listed in [verification](VERIFICATION.md).

Create fresh private pairing profiles outside the repository using the distribution pairing assistant; never commit credentials, settings, signing keys, clipboard contents, or private logs. Preserve LICENSE and all third-party notices in redistributed builds.
