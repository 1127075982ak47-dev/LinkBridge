# Build and pair LinkBridge 0.0.1

LinkBridge currently targets a Windows computer on the left and an Apple Silicon Mac on the right. Keyboard and mouse control, shared text/image clipboard, and Windows-to-Finder file drag are the primary development path. The public UI does not yet include a complete first-run pairing wizard: a new pair needs its own settings and initial macOS permissions.

The existing developer installation is not a transferable pairing profile. Generate new credentials for your own two computers. Do not copy another installation's settings, signing key, keychain, or permission database.

## Dependencies

- .NET **10 SDK** to build. Framework-dependent published apps need the matching .NET 10 runtime; the SDK already includes it. See Microsoft's [Windows installation guide](https://learn.microsoft.com/en-us/dotnet/core/install/windows) and [macOS installation guide](https://learn.microsoft.com/en-us/dotnet/core/install/macos).
- Windows: the current native build and manual validation path is Windows 11 x64.
- Mac: the bundle script targets Apple Silicon `osx-arm64`. Install Xcode Command Line Tools for `swift`/`swiftc`, `codesign`, and `iconutil`. Use a macOS version supported by your .NET 10 installation. The file-drag helper has a macOS 13 deployment target; that target alone does not define the .NET runtime support matrix.
- FFmpeg for camera/microphone/media operations. Install an appropriate build from the [FFmpeg download page](https://ffmpeg.org/download.html). The application searches next to its executable, then `PATH`, and common platform locations. On macOS Homebrew's `/opt/homebrew/bin/ffmpeg` and `/usr/local/bin/ffmpeg` are recognized. Keyboard and clipboard transports do not invoke FFmpeg.
- Optional local Mac signing preparation requires Python 3 and Homebrew OpenSSL 3. Signing tooling recognizes the usual Apple Silicon and Intel Homebrew OpenSSL locations.
- PowerShell 7.2 or newer for the profile generator below.

## Windows: build and run from your checkout

Run from the repository root, at any ordinary local path:

```powershell
dotnet --info
dotnet restore src/LinkBridge.App/LinkBridge.App.csproj
dotnet build src/LinkBridge.App/LinkBridge.App.csproj -c Release
dotnet run --project src/LinkBridge.App/LinkBridge.App.csproj -c Release
```

For a portable framework-dependent output:

```powershell
dotnet publish src/LinkBridge.App/LinkBridge.App.csproj -c Release -r win-x64 --self-contained false -o ./artifacts/windows-local
./artifacts/windows-local/LinkBridge.App.exe
```

If you want FFmpeg beside this output, place the licensed `ffmpeg.exe` build in `artifacts/windows-local`. Keep its license and redistribution terms with your distribution.

Contributors choose their own installation path and shortcut using the generic publish command above. Machine-specific maintainer deployment scripts are intentionally excluded from this repository.

## Mac: build a complete native bundle

Restore/build from the checkout root:

```sh
dotnet restore src/LinkBridge.App/LinkBridge.App.csproj
dotnet build src/LinkBridge.App/LinkBridge.App.csproj -c Release
sh tools/mac-app/build-mac-app.sh
```

This produces `artifacts/LinkBridge.app`, including the compiled native Finder file-promise helper and programmatically rendered icon. `dotnet run` is useful for compilation/UI development; the complete bundle is needed for the production file-drag path and meaningful GUI permission checks.

The build prepares a candidate and does not install it by default. Review it, then use:

```sh
sh tools/mac-app/install-validated-app.sh
```

The existing installer uses the current user's `~/Desktop/LinkBridge.app`, preserves its previous bundle, validates signatures, and opens the installed application. Routine installs stop if the code identity changes.

### Persistent local development signing

On a fresh Mac without a configured local identity, the build script makes an ad-hoc preview. Rebuilding an ad-hoc app can change its designated code requirement and require permission again. Establish your own persistent local identity before granting permissions for a long-lived development installation. Apple explains code identity in [TN3127: Code signing requirements](https://developer.apple.com/documentation/technotes/tn3127-inside-code-signing-requirements).

The included preparation creates a new identity on **your Mac**, or verifies your already-created identity. It never imports a maintainer's private key. In a GUI Terminal on that Mac:

```sh
python3 tools/mac-signing/prepare-identity.py --prepare
# Explicitly review and approve this operation: it imports your prepared identity
# into your login keychain and sets current-user Code Signing trust for that certificate.
python3 tools/mac-signing/import-approved-identity.py --user-approved
python3 tools/mac-signing/ready-identity.py
sh tools/mac-app/build-mac-app.sh
```

Approve your certificate's Code Signing trust and `/usr/bin/codesign` private-key access in the native Mac prompts. Enter passwords only into those local prompts. The identity is kept under your private `~/Library/Application Support/LinkBridge/Signing/local-codesign-v1`; it is not part of the source checkout. Subsequent builds reuse that identity and stop if it is unavailable.

If you deliberately replace an installed ad-hoc preview with your persistent identity, the installer permits a single explicit migration:

```sh
LINKBRIDGE_ALLOW_IDENTITY_CHANGE=1 sh tools/mac-app/install-validated-app.sh
```

Use the ordinary install command for later versions. First migration can require granting permissions to the newly identified bundle. This local signing method is for development; public Developer ID distribution, notarization, and native camera extensions require their own Apple developer signing and entitlement work.

## Generate a private Windows/Mac pair

Choose the IPv4 addresses of the interfaces actually used between the computers. Keep both on the same trusted LAN for the current UDP discovery path. Use stable leases so the receiver allowlist remains correct.

```powershell
# These are documentation-only addresses. Replace them with your actual peer IPv4 addresses.
pwsh -File tools/open-source/New-PairingProfile.ps1 -WindowsAddress 192.0.2.10 -MacAddress 192.0.2.20 -MacDisplayWidth 2560 -MacDisplayHeight 1440
```

The generator creates a fresh cryptographically random 128-bit key encoded as 32 hexadecimal characters, shared by the two new files. It prints only the generated private paths. It refuses an existing output directory and an output inside a Git checkout; it does not read or overwrite existing settings. Windows files inherit an ACL limited to the current user, SYSTEM, and administrators; Unix output uses owner-only permissions.

The generated JSON uses the application schema:

| Field | Windows profile | Mac profile |
| --- | --- | --- |
| `allowedControllerAddress` | Mac interface IPv4 | Windows interface IPv4 |
| `inputPairingSecret` | New shared 32-character hexadecimal key | Same new key |
| `enableInputServer` | `false` | `true` |
| `autoDiscover` | `true` | `true` |
| `remoteDisplayWidth` / `remoteDisplayHeight` | Mac pointer coordinate dimensions | Same dimensions |
| `shareClipboardAutomatically` | `true` | `true` |

Camera, microphone, and automatic shared audio start disabled in these initial profiles. Enable and configure them when you want those capabilities. Match remote dimensions to the Mac desktop's pointer coordinates; the camera's 4K sensor resolution is a separate setting.

Close both applications. Back up any settings you already use locally, then install the appropriate new file as:

- Windows: `%APPDATA%\LinkBridge\settings.json`.
- Mac: `~/Library/Application Support/LinkBridge/settings.json`.

Copy only `mac.settings.json` to the Mac over a trusted private channel, rename it to `settings.json`, and set its directory to mode `0700` and the file to `0600`. The Windows file becomes the Windows `settings.json`. Restart both applications. Generated files contain a real secret: keep them private and out of commits, issue attachments, screenshots, and release archives.

## First launch and checks

1. Allow LinkBridge on the chosen private network when prompted. Discovery uses UDP `45678`; Mac input uses TCP `45680`; Windows-to-Finder file transfer uses TCP `45686`; shared clipboard uses TCP `45687` on both computers. Restrict reachability to your paired computers and chosen network.
2. Open the complete Mac bundle. Use LinkBridge's device-control setup to open **System Settings → Privacy & Security → Accessibility**, and grant that exact installed app. Follow any additional Input Monitoring or Local Network prompt shown by your macOS version. New computers need their own initial permission grants.
3. Verify the paired Mac appears and the application reports its actual connected state. Move from Windows's right edge into the Mac and back left; `Ctrl+Alt+M` restores Windows control.
4. Copy new plain text or an image and paste it in an application on the other computer. Startup clipboard history is skipped; test a fresh copy after the connection is established.
5. For file transfer, select ordinary local files in Explorer, hold the left button while passing the right screen edge, then drop into a Finder folder. The current direction is Windows → Mac and the operation copies, preserving the source. Completion follows actual destination saving and integrity checks.

The project's local 0.0.1 validation is evidence for its tested pair. A new machine still needs its own address allowlist, runtime, permission grants, and device/backend checks. Virtual audio/microphone backends and application-visible camera integration have additional native installation/signing requirements; compiling the GUI does not install those components.

## Contributor checks without private configuration

```powershell
dotnet run --project tools/ClipboardProbe -c Release
dotnet run --project tools/FileTransferProbe -c Release
```

These transport probes use isolated loopback fixtures. The Windows native clipboard probe is a separate test that deliberately writes known clipboard fixtures; inspect its source and preserve any clipboard content you need before using it. Never include user settings, pairing profiles, signing private material, credentials, or unsanitized local artifacts in a public contribution.
