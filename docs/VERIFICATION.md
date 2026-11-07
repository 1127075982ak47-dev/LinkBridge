# Current verification: 0.0.4-beta.1

The [2026-11-07 public-beta report](releases/0.0.4-beta.1/verification.zh-CN.md) distinguishes automated protocol checks, isolated installs, runtime startup and untested physical/customer workflows.

Passed: Windows and Mac builds; Windows install/start/reinstall/uninstall and bundled runtime loading; isolated Mac install/reinstall/start/exit and signing-conflict rejection; screen layout, input/paired TLS, discovery/manual connection, clipboard and file protocol checks; configuration persistence; 2,000 synthetic inputs across Windows/Mac TLS; both agent installation routes; archive/payload verification.

Not established this round: fresh-customer Gatekeeper/SmartScreen/TCC, full native Explorer/Finder copy/paste, all physical crossing directions, multiple real Mac monitors, every IME/DPI combination, sleep/reboot and long-duration acceptance. The legacy native edge probe observed zero crossings and is not counted as a pass. DeviceProbe has nine CA1416 warnings in the full source build.

Historical evidence is retained separately: [0.0.1, 2026-10-02](verification/0.0.1.md). It is not current-version acceptance.
