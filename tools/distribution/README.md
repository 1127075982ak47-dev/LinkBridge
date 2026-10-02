# Public macOS preview · 0.0.1

This package targets **Apple Silicon, macOS 15 or later**, following the current [.NET 10 supported macOS versions](https://github.com/dotnet/core/blob/main/release-notes/10.0/supported-os.md). The .NET runtime is included; users do not need to install the SDK to launch this app. Media functions still need FFmpeg, and experimental camera/microphone features retain their separate driver and system-approval requirements.

## Install

1. Extract the release ZIP on the Mac, keeping `LinkBridge.app`, `Install.command`, and this README together.
2. Run `Install.command` to copy the app into `~/Applications/LinkBridge.app`.
3. Open that application in Finder. Configure your trusted peer and review the relevant macOS permissions when prompted. A new Mac needs its own Accessibility approval for remote input; screen capture may require Screen Recording access.

The installer verifies bundle identity, version and code signature. If the target already contains an app with a different signing requirement, it refuses to replace it. Ad-hoc requirements can change after a rebuild, so a later preview can also require a reviewed migration. An existing app with the same requirement is backed up before replacement. Configuration is preserved. The installer does not modify any Desktop application.

The public app is **ad-hoc signed**, with no Developer ID signature or Apple notarization. macOS may block a downloaded preview until you choose to review it using the normal system UI. Neither script removes quarantine, changes Gatekeeper, grants privacy permissions, runs `sudo`, or imports a signing key. The package contains no paired-device secrets, user configuration or private certificates.

## Build from the public source

On an Apple Silicon Mac, install the .NET 10 SDK and Xcode Command Line Tools, then run from the repository:

```sh
sh tools/distribution/build-mac-release.sh
```

The independent build writes `artifacts/public-release/LinkBridge.app`, `Install.command`, and this README. It publishes a self-contained `osx-arm64` app, excludes development symbols, maps compiler source paths to a generic prefix, builds the native file-drag helper and procedural icon, then applies ad-hoc signatures to native components and the outer bundle. It never calls the private/stable signing workflow or installs the application.

Adjacent files include the project LICENSE, dependency notices, build/setup instructions, pairing-profile generator, and the original license and third-party notices from the runtime package version actually included in this build. `runtime-notice.md` records that version; `SHA256SUMS.json` hashes every output file except the manifest itself. Project licensing does not replace runtime or native-component terms. Missing required notices stop release preparation. None of these adjacent files is inserted into or changes the already signed app.

Keep this preview separate from a daily app that already has a stable certificate and system authorizations. For continuing updates with retained authorizations, review a signing setup you control rather than replacing that identity with this public preview.

## 简体中文

这是 Apple Silicon/macOS 15 及以上的开发预览，已包含 .NET 运行时，系统要求依照当前 .NET 10 支持列表。解压后，将应用与 `Install.command` 放在一起，运行安装脚本即可复制到当前用户的 `~/Applications/LinkBridge.app`，随后从 Finder 打开并配置自己的配对设备。

公开包只使用 ad-hoc 签名，没有 Developer ID 和公证。首次权限与系统下载安全审核需由使用者在 macOS 正常界面中完成；脚本不更改 Gatekeeper、不清除隔离标记、不自动授权、不使用管理员命令。已有应用签名要求不同会拒绝覆盖，已有相同要求的应用先备份。不会替换桌面上的既有应用或携带开发者的配置、配对秘密和签名材料。
