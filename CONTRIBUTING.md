# Contributing

Thank you for trying LinkBridge on your desk. Small, reproducible improvements are welcome.

## Useful starting points

- Windows-to-Mac drag behavior and cancellation across mixed DPI displays.
- Clipboard compatibility with native editors, browsers and creative applications.
- macOS permissions and persistent code identity during upgrades.
- Reverse native file dragging and first-run pairing.
- Audio endpoint ownership, device changes and reconnect behavior.

See [BUILDING](docs/BUILDING.md) for prerequisites and private pairing setup. Keep generated profiles outside your checkout.

## Before opening a pull request

1. Describe the user-visible problem and the smallest change that addresses it.
2. Build the affected projects and run the relevant transport probe.
3. For native behavior, record the OS versions, screen layout and actual receiving-side result.
4. Preserve third-party notices and explain any added dependencies.
5. Remove private addresses, names, clipboard contents, logs and credentials from examples.

Transport checks:

```sh
dotnet build LinkBridge.slnx -c Release
dotnet run --project tools/ClipboardProbe -c Release
dotnet run --project tools/FileTransferProbe -c Release
```

The Windows native clipboard probe intentionally changes the system clipboard. Read its source before running it. Do not run it while relying on clipboard contents in other work.

Please open an issue before a broad UI or protocol redesign. The accepted direction is a circular workspace, quiet feedback and a small set of reliable cross-system actions.
