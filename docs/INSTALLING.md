# Install LinkBridge 0.0.4-beta.1

Download the [complete beta bundle](https://github.com/1127075982ak47-dev/LinkBridge/releases/tag/v0.0.4-beta.1), extract it fully, and open `00-先读我.html`.

- Windows 11 x64: run the EXE in `01-Windows`. Default current-user installation: `%LOCALAPPDATA%\Programs\LinkBridge`.
- Apple Silicon macOS 15+: extract the ZIP in `02-macOS`; run `sh Install.command` with the script adjacent to LinkBridge.app. Target: `~/Applications/LinkBridge.app`.
- Create ONE pairing set in the Windows pairing assistant, import the corresponding files after both apps exit, complete macOS prompts, connect, and configure Screen Layout.
- Local agents: read the complete bundle's `04-Agent/AGENTS.md` and use its matching installation script. This repository also preserves the [handoff](agent-install/AGENTS.md).

Both packages bundle .NET; end users do not need SDKs, Python, PowerShell 7 or FFmpeg. Windows scripts support PowerShell 5.1. OS security approvals require the owner. Existing configuration is retained, and a conflicting Mac signing identity is not overwritten.

Detailed Chinese instructions: [user manual](customer/LinkBridge用户说明书.md). Current limitations: [verification report](releases/0.0.4-beta.1/verification.zh-CN.md).
