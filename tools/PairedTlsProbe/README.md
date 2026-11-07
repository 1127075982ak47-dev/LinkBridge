# PairedTlsProbe

可重复运行的配对 TLS、PCM 拼帧和配置持久化验证。项目相对引用 `LinkBridge.Core`、`LinkBridge.Input`、`LinkBridge.Media`，不依赖生产配置中的密钥。

所有测试密钥在进程内随机生成；跨 SSH 时仅经 SSH 标准输入交付，代码和日志不保留密钥。输入测试使用假事件处理器，不调用原生键鼠注入。音频测试使用内存 `BufferedWaveProvider`，不打开声卡、麦克风、BlackHole 或摄像头。配置测试只创建 `LinkBridge-SettingsProbe-<随机标识>` 独立临时目录，并在结束时清理；不读取或更改真实 settings.json。

## 本机模式

在项目根目录运行：

```powershell
dotnet run --project tools/PairedTlsProbe/PairedTlsProbe.csproj -c Release -- input
dotnet run --project tools/PairedTlsProbe/PairedTlsProbe.csproj -c Release -- audio
dotnet run --project tools/PairedTlsProbe/PairedTlsProbe.csproj -c Release -- settings
```

Mac 同样支持这些命令。已构建的可移植 DLL 可运行 `dotnet <完整路径>/PairedTlsProbe.dll <mode>`。

| 模式 | 覆盖内容 |
|---|---|
| `input` | 双向 LBI3 挑战认证；2000 个有序假输入；正常心跳；EOF 释放；错误密钥；授权拒绝；替换 TLS 证书并转发真实挑战的 MITM；6 秒失联；100000 次并发发送与 256 队列上限；TCP 抓取中秘密/独特输入包没有明文；4 秒 TLS/认证超时。 |
| `audio` | LBA1 加密 PCM；3+5 字节与随机分片，400000 字节逐字节复原；错误密钥；LBI3/LBA1 上下文混淆；换证挑战转发；8 秒闲置超时；资源释放。 |
| `settings` | Windows 实际目录/文件显式私有 ACL，或 Mac 700/600；错误类型与数值范围安全默认；原文件未自动改写、密钥未更换；未知 JSON 字段保留；150 次原子保存及独立完整 JSON 读取；临时文件清理；错误保存保留上一份文件并返回错误。 |

命令发现断言失败会返回非零退出码。`settings` 中两条 `SAVE FAILURE` 是主动模拟的失败条件，随后必须出现最后一条 PASS。Windows 原子替换时独立原始文件读取可能短暂发生共享冲突，探针仅重试该错误，仍会把任何不完整 JSON 视为失败。

## 跨 Windows / Mac 的 SSH 模式

需要已有可用的 SSH 别名，且 `BatchMode=yes` 能连接。探针不创建凭据、不改变 SSH 配置、不弹出认证 UI。

先把构建产物放到 Mac 的一个独立目录，例如通过 `dotnet publish` 的框架依赖输出。下面的远端 DLL 路径是示例，运行时换为实际路径。

```powershell
# Windows 输入客户端 → Mac TLS 服务端
# 自动启动 cross-input-server，并建立独立 SSH 本地端口转发。
dotnet run --project tools/PairedTlsProbe/PairedTlsProbe.csproj -c Release -- cross-input-client mac /tmp/linkbridge-paired-tls-probe/PairedTlsProbe.dll

# Mac 音频客户端 → Windows TLS 服务端
# 自动启动 cross-audio-client，并建立独立 SSH 远端端口转发。
dotnet run --project tools/PairedTlsProbe/PairedTlsProbe.csproj -c Release -- cross-audio-server mac /tmp/linkbridge-paired-tls-probe/PairedTlsProbe.dll
```

`cross-input-server` 与 `cross-audio-client <转发端口>` 是上述编排模式启动的内部端点，测试密钥从标准输入读取。不要向命令行参数中加入生产配对密钥。

| 编排模式 | 实际覆盖 |
|---|---|
| `cross-input-client` | Windows SChannel 客户端与 Mac Secure Transport 服务端实际 TLS 互操作；2000 个假输入、心跳与一次 EOF 释放。 |
| `cross-audio-server` | Mac Secure Transport 客户端与 Windows SChannel 服务端实际 TLS 互操作；400000 字节 PCM 在 3+5/分片之后逐字节一致。 |

这些跨设备测试走独立 SSH 转发端口。它们证明两个 TLS 实现互通，不证明生产 LAN 端口、防火墙、Mac 辅助功能或实体声卡能用；这些仍由应用端验收。

## 2026-10-02 验证状态与证据

整理进仓库后重新执行了全部本机模式与两个 SSH 编排模式。没有用早期临时代码的成功代替当前仓库代码验收。

| 平台 / 模式 | 状态 | stdout |
|---|---|---|
| Windows `input`，TLS 1.3 | 已测通过 | [windows-input](evidence/windows-input-2026-10-02.txt) |
| Windows `audio`，TLS 1.3 | 已测通过 | [windows-audio](evidence/windows-audio-2026-10-02.txt) |
| Windows `settings`，保护 ACL | 已测通过 | [windows-settings](evidence/windows-settings-2026-10-02.txt) |
| Mac `input`，TLS 1.2 | 已测通过 | [mac-input](evidence/mac-input-2026-10-02.txt) |
| Mac `audio`，TLS 1.2 | 已测通过 | [mac-audio](evidence/mac-audio-2026-10-02.txt) |
| Mac `settings`，700/600 | 已测通过 | [mac-settings](evidence/mac-settings-2026-10-02.txt) |
| Windows 输入 → Mac 服务端，TLS 1.2 | 已测通过 | [cross-input](evidence/cross-input-2026-10-02.txt) |
| Mac PCM → Windows 服务端，TLS 1.2 | 已测通过 | [cross-audio](evidence/cross-audio-2026-10-02.txt) |

本轮未测：Linux；Mac Network Framework 的 TLS 1.3；实际生产 LAN 路径；实际键鼠注入；实体音响播放；真实摄像头；突然断电或磁盘控制器故障。配置压力测试证明原子 JSON 可见性与正常失败保留，不能代替真实断电测试。

普通视频与原生 4K 视频的 UDP MPEGTS 不在这些 TLS 测试覆盖范围，本项目当前未宣称视频传输加密。
