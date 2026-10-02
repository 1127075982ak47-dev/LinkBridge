# Install the public 0.0.1 preview

[Download from Releases](https://github.com/1127075982ak47-dev/LinkBridge/releases/tag/v0.0.1)

These packages include the .NET runtime. They do not contain a maintainer's settings, paired-device key, login credential, signing identity or optional audio-driver installer.

## Windows x64

1. Download and fully extract `LinkBridge-0.0.1-Windows-x64.zip`.
2. Double-click `Install.cmd`. It runs the reviewed PowerShell installer for this process only.
3. The app installs for your user under `%LOCALAPPDATA%\Programs\LinkBridge` and adds a desktop shortcut.

No administrator install is requested. Existing Roaming AppData settings are preserved. If your desktop LinkBridge shortcut points to another installation, the installer reports a conflict and stops before replacing it.

The Windows preview is not Authenticode signed. The ZIP's manifest validates file integrity but is not a publisher certificate. Review the source and normal Windows download prompts when deciding whether to run it. The installer does not change saved system execution policies.

## Apple Silicon Mac

1. Download and extract `LinkBridge-0.0.1-macOS-arm64.zip` on macOS 15 or later.
2. Keep the app and `Install.command` together inside the extracted `public-release` folder.
3. Run `Install.command` to install under `~/Applications/LinkBridge.app`, then open it in Finder.

This public bundle is ad-hoc signed. It has no personal maintainer certificate, Developer ID signature or notarization. The installer verifies the bundle and refuses to overwrite an app with a different signing requirement. It does not change Gatekeeper, remove quarantine, grant privacy permissions or replace Desktop apps.

First use needs the relevant local macOS permission review. Routine updates for a long-lived installation should preserve a signing identity you control. This public preview must not silently replace an already-authorized certificate-signed daily app.

## Pair your computers

The binaries do not include a ready-made pairing profile. Generate new settings for your own Windows/Mac pair using the included `New-PairingProfile.ps1`, and follow [BUILDING](BUILDING.md#generate-a-private-windowsmac-pair). The generator requires PowerShell 7.2 or newer.

Keyboard/mouse and clipboard setup are independent of FFmpeg. Media functions require a separately installed FFmpeg build; optional audio drivers and experimental application-visible cameras have additional prerequisites.

## Verify downloads

Compare the download's SHA-256 with `SHA256SUMS-public.txt` in the release. Each ZIP also includes a per-file `SHA256SUMS.json`. Windows installation validates that manifest; Mac build/packaging validation additionally verifies code signatures.

The publication audit is documented in [PUBLICATION-AUDIT](security/PUBLICATION-AUDIT.md). This is a developer preview, with compatibility and native acceptance limits in [VERIFICATION](VERIFICATION.md).

## 简体中文

两个包都带运行时，不需要为了启动软件另外安装 .NET SDK。Windows 解压后双击 `Install.cmd`；Mac 解压后在应用旁运行 `Install.command`，再从 Finder 打开。首次配对需要为自己的两台电脑生成新配置，首次 Mac 权限由使用者完成。

公开包没有开发者的配对秘密、账号、私钥或个人签名身份。Windows 包未做 Authenticode 签名；Mac 包采用 ad-hoc 签名、尚未公证。安装器不会绕过系统安全审核，也不会替换另一份已授权应用的身份。
