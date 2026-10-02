# LinkBridge 第三方技术与许可核查

核查日期：2026-10-02。范围为当前源码、已恢复 NuGet 包的 `.nuspec` / 许可文件、Windows 相机目录中的上游说明，以及下文链接的厂商官方资料。本文记录技术来源和分发边界，不把依赖表当作完整法律结论，也不宣称所有代码、字体、插图或品牌均为项目原创。

## 1. 本项目代码与外部技术的边界

LinkBridge 的设备协调、产品界面、圆盘与流光程序绘制、跨屏事件接力、`LBI3` / `LBF1` / `LBC1` 应用层封包、剪贴板回声抑制、文件暂存/校验与原生互操作接入由本项目实现，开发过程有 AI 辅助。TLS、HMAC-SHA256、SHA-256、操作系统输入/剪贴板/音视频服务及 UI/渲染库来自下列平台和依赖。自定义协议不等于自研密码算法；本项目没有替代 TLS 或创建新的加密原语。

Windows 虚拟相机的 COM/MF source、stream、activation 骨架派生自 Microsoft Windows-Camera 示例，保留其来源和许可。Mac CMIO host/extension/decoder 是本项目针对 Apple SDK 接口编写的实现；调用 Apple 的框架不能表述为独立于系统的自研驱动平台。功能源码存在也不等于驱动、扩展授权和应用调用已经全部完成。

## 2. 直接与关键间接依赖

版本来自当前 `src/LinkBridge.App/obj/project.assets.json` 以及各项目 `.csproj`；NuGet 恢复产物是审计输入，不应连同用户机器上的整个包缓存上传。

| 组件 | 已核对版本 | 许可/来源 | 本项目用途与证据 |
|---|---|---|---|
| Avalonia、Avalonia.Desktop、Avalonia.Themes.Fluent、Avalonia.Fonts.Inter | 12.1.3 | 框架与 wrapper：MIT。[上游许可](https://github.com/AvaloniaUI/Avalonia/blob/master/licence.md) | 跨平台界面、控件、主题、字体封装。四个包的本地 nuspec 均为 `license type=expression, MIT`。 |
| Avalonia.Skia、Native、Win32、HarfBuzz、FreeDesktop、FreeDesktop.AtSpi、X11、Remote.Protocol | 12.1.3 | MIT，同一 Avalonia 上游 | Desktop 的平台适配、绘制与文本后端。来自恢复依赖图。Linux 相关包被恢复不表示 0.0.1 已支持 Linux。 |
| Avalonia.BuildServices | 11.3.2 | nuspec 标记 MIT；[官方项目](https://github.com/AvaloniaUI/Avalonia.BuildServices) | 构建依赖。许可声明不意味着本项目已经核对其全部内部实现或发布源代码可得性。 |
| AvaloniaUI.DiagnosticsSupport | 2.2.3 | **不标作 MIT**：本地 nuspec 未提供 SPDX/许可文件；[官方 FAQ](https://docs.avaloniaui.net/tools/faq) 明确 integration bridge 可由任何人构建、用于公开项目，不需单独许可；Developer Tools 软件自身另需厂商许可/账户 | 开发诊断桥。当前 `.csproj` 在非 Debug 配置设 `IncludeAssets=None` / `PrivateAssets=All`。公开源码保留 PackageReference，Release 不携带该运行库；不能复制此结论到 Developer Tools 本体。 |
| Inter 字体 | Avalonia.Fonts.Inter wrapper 12.1.3；嵌入字体 `Version 3.019;git-0a5106e0b` | SIL Open Font License 1.1。[字体官方许可](https://github.com/rsms/inter/blob/master/LICENSE.txt) | 只读解析本地 Avalonia.Fonts.Inter.dll 中 6 个 sfnt 的 name table，均记录该版本、`Copyright 2020 The Inter Project Authors` 与 OFL1.1 声明/链接。wrapper 的 MIT 不覆盖字体许可；分发字体时保留 OFL 和版权说明，不把字体改许可为项目 MIT。 |
| CommunityToolkit.Mvvm | 8.4.2 | MIT；本地 `License.md` / `ThirdPartyNotices.txt`；[官方项目](https://github.com/CommunityToolkit/dotnet) | ObservableProperty、RelayCommand 与 MVVM source generators。 |
| NAudio、NAudio.Core、NAudio.Wasapi、NAudio.WinMM、NAudio.Midi、NAudio.Asio | 2.2.1 | MIT；主包本地 `license.txt`，子包 nuspec MIT；[官方项目](https://github.com/naudio/NAudio) | Windows WASAPI/CoreAudio 音频端点与播放。包图包含 MIDI/ASIO 不代表产品实现这些能力；不附带第三方 ASIO 驱动。 |
| System.Management、System.CodeDom | 9.0.0 | MIT；本地 `LICENSE.TXT` / `THIRD-PARTY-NOTICES.TXT`；[.NET 上游许可](https://github.com/dotnet/runtime/blob/main/LICENSE.TXT) | Windows WMI 查询及关联依赖。 |
| .NET | 目标 net10.0；本机核查 SDK 10.0.400 | .NET 源码主要 MIT，另保留其 third-party notices；[官方 runtime](https://github.com/dotnet/runtime) | C# runtime、网络、TLS 与加密/IO。当前两端发布 `--self-contained false`，未把完整 .NET runtime 重打包。SDK 版本不是最终用户 runtime 的实际版本。 |
| SkiaSharp 及 NativeAssets.Win32/macOS/Linux/WebAssembly | 3.119.4 | bindings MIT；本地 `LICENSE.txt` / native `THIRD-PARTY-NOTICES.txt`；[SkiaSharp](https://github.com/mono/SkiaSharp)、[Skia BSD 许可](https://github.com/google/skia/blob/main/LICENSE) | Avalonia 渲染与无损图片编码/解码。native notice 包含 Skia/ANGLE/图像压缩/字体等不同许可，不能用一句 MIT 覆盖全部底层代码。仅实际目标的 native binaries 会用于相应发布。 |
| HarfBuzzSharp 及 NativeAssets | 8.3.1.3 | bindings MIT；native notice 包括 HarfBuzz 的上游许可与其他组成部分；[官方绑定来源](https://github.com/mono/SkiaSharp) | 字形 shaping。保留该版本完整 `THIRD-PARTY-NOTICES.txt`。 |
| Avalonia.Angle.Windows.Natives | 2.1.27548.20260419 | 本地 `LICENSE` 为 ANGLE BSD-style 3-clause notice；[Avalonia ANGLE 来源](https://github.com/AvaloniaUI/angle) | Windows 图形适配。不可把该 native 包一概写成 Avalonia MIT。 |
| MicroCom.Runtime | 0.11.6 | nuspec MIT；[上游](https://github.com/kekekeks/MicroCom) | Avalonia 原生 COM 互操作依赖。 |
| Tmds.DBus.Protocol | 0.94.1 | nuspec MIT；[上游](https://github.com/tmds/Tmds.DBus) | Avalonia Linux 平台间接依赖，不意味着本项目已完成 Linux 支持。 |
| Microsoft.Extensions.DependencyInjection.Abstractions、Logging.Abstractions | 8.0.0 | 本地 nuspec MIT 与 `LICENSE.TXT` / `THIRD-PARTY-NOTICES.TXT`；[.NET](https://github.com/dotnet/runtime) | 关键传递依赖。 |
| Microsoft.IO.RecyclableMemoryStream | 3.0.1 | nuspec MIT；[官方项目](https://github.com/microsoft/Microsoft.IO.RecyclableMemoryStream) | 诊断桥相关恢复依赖；不据此宣称 Release 必须打包它。 |

## 3. 系统 API、媒体工具与可选后端

| 项目 | 来源、版本与许可边界 | 实际关系 |
|---|---|---|
| FFmpeg | [官方许可说明](https://ffmpeg.org/legal.html)：默认 LGPL-2.1-or-later；启用部分 GPL 组件后适用 GPL；具体二进制的 `-version` / `-buildconf` 与所带许可决定实际组合 | 本项目通过外部进程使用 FFmpeg，不把其源码称为原创。`FfmpegLocator` 可从应用旁、PATH 或既有安装查找。当前核查的 Windows production 目录未发现 ffmpeg.exe。本源码公开方案不上传 FFmpeg 二进制；以后任何捆绑发行必须另核对该构建、对应源码/notice，不能仅凭“外部调用”宣布不存在义务。 |
| BlackHole | 可选 2ch / 16ch；工具准备版本 0.7.1。[官方 LICENSE](https://github.com/ExistentialAudio/BlackHole/blob/master/LICENSE) 目前说明源码为 GPLv3，官方编译包/安装器与品牌另列厂商权利 | 共享音频/麦克风可选 HAL 后端。本仓库仅提供官方独立下载/核验准备脚本，**不上传驱动、pkg、卸载器或源码副本**。不把 GPL 源码许可自动当作官方安装包重新包装许可。 |
| VB-CABLE | 可选 Windows Pack45，准备工具核对驱动 3.3.1.7；厂商 Donationware，并非 MIT/GPL 开源。[VB-Audio 官方许可/分发规则](https://vb-audio.com/Services/licensing.htm) | 共享麦克风可选虚拟音频设备。本仓库不上传 ZIP/SYS/安装器；用户由厂商独立取得。公开源码许可证不覆盖该驱动。 |
| Windows Media Foundation、Win32、WASAPI、WMI、剪贴板/OLE | Microsoft Windows/SDK 系统 API；[MFCreateVirtualCamera](https://learn.microsoft.com/en-us/windows/win32/api/mfvirtualcamera/nf-mfvirtualcamera-mfcreatevirtualcamera) | Windows 系统提供能力，本项目实现调用、生命周期和产品交互。Windows/SDK 系统组件不是项目 MIT 源码。 |
| Microsoft Windows-Camera 示例 | commit `626f8b19c5f367602f2e89c6b314573d3776c9df`，MIT；本地 `LICENSE-Microsoft` / `THIRD-PARTY-NOTICES.txt`；[固定源码入口](https://github.com/microsoft/Windows-Camera/tree/626f8b19c5f367602f2e89c6b314573d3776c9df/Samples/VirtualCamera/VirtualCameraMediaSource) | `src/LinkBridge.VirtualCamera.Windows` 明确为派生实现，保留 upstream 与版权，修改为 LinkBridge 标识/IPC/格式/生命周期。 |
| Windows Implementation Library (WIL) | 1.0.260126.7，MIT；本地 `LICENSE-WIL`、deps/wil/LICENSE、NuGet nuspec；[官方来源](https://github.com/microsoft/wil) | Windows 相机的 header-only 工具依赖。build.ps1 固定官方 NuGet URL 与 SHA-256；公开方案应使用恢复脚本，不上传 deps/wil.zip 或构建缓存。 |
| C++/WinRT / Windows SDK | 当前 native build 脚本指定 Windows SDK 10.0.26100.0；安装 SDK 自带相应许可 | 使用本机 SDK 的 header/library，未因此取得把整个 SDK 上传到仓库的权限。 |
| Mac AppKit、CoreGraphics、Accessibility、CoreAudio、AVFoundation、VideoToolbox、CMIOExtension、IOSurface | Apple 的操作系统与 SDK API；[CMIO 官方文档](https://developer.apple.com/documentation/coremediaio/creating-a-camera-extension-with-core-media-i-o)、[WWDC22 技术说明](https://developer.apple.com/videos/play/wwdc2022/10022/) | Objective-C/Swift/PInvoke 接入由项目编写；权限、系统扩展批准与 Developer Team 签名仍由系统管理。没有发布 Apple SDK 二进制，不宣称本地自签名等于 Apple 签名或扩展可部署。 |

## 4. UI 插图与品牌

`src/LinkBridge.App/Assets/ConstellationC` 中的设备/双电脑插图是本项目开发期间使用 AI 图像工具生成的视觉资产；不是摄像头制造商、Apple 或 Microsoft 官方宣传图，也不是运行能力的证据。圆盘、图标及中心流光为程序绘制，来源与图像插图分别注明。当前本次审计没有逐一重建每张插图的模型、prompt、参考图与输出时间，因此不作“已清除所有图像权利风险”的承诺。

Windows、Microsoft、Apple、Mac 等名称与品牌标识归各权利人；项目不受这些厂商背书。当前 AI 双电脑插图可能含可辨认系统品牌符号，AI 生成本身不消除商标规则。公开版本宜使用无品牌显示器插图与文字平台名称，或保留获得适当授权的证据。[Microsoft 品牌规则](https://www.microsoft.com/en-us/legal/intellectualproperty/trademarks) 与 [Apple 第三方商标指南](https://www.apple.com/legal/intellectual-property/guidelinesfor3rdparties.html) 是来源资料；本审计不替权利人授权。

## 5. 当前公开源码的处理范围

- 保留本项目与所改编示例的版权/来源说明；主仓库许可证不改变依赖与字体许可。
- 上传源码、构建/恢复脚本和必要 UI 资产；排除用户配置、密码/配对密钥、私钥、TCC/设备隐私快照、安装缓存、bin/obj、FFmpeg/第三方音频驱动安装包及完整 SDK。
- 公开源码与后续发布二进制分别审计。后者需要把实际捆绑 native 库的完整 license/notice 一起交付。根目录 `THIRD_PARTY_NOTICES.md` 保留本次关键本地 notice 原文，仍不能替代未来新版本的实际包审计。
- DiagnosticsSupport 的确切版权源代码许可证未在本地包获得；采用官方公开项目允许声明，不标为 MIT。Inter 内嵌字体修订号与 OFL 声明已从实际包字体 name table 核对。FFmpeg 构建不由本仓库固定，因此没有在此宣称某个实际安装一定是 LGPL-only。

本次只新增本报告和第三方通知，没有发布仓库、安装驱动或改变运行源码。
