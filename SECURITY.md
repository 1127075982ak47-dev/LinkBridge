# Security

LinkBridge 0.0.1 is a developer preview for paired computers on a trusted network. It has not undergone an independent security audit. Do not expose its listener ports directly to the internet.

Keyboard, file and clipboard channels authenticate the configured peer using a private shared pairing key and separate TLS protocol contexts. Discovery is a local broadcast. Some experimental media paths are not covered by an end-to-end encryption claim.

Pairing files and local signing identities are private per-installation data. This repository contains neither. Clipboard sharing sends newly copied plain text or supported images to the paired computer while enabled; it skips startup history and does not persist clipboard history.

If you find a vulnerability, use the repository's **Security → Report a vulnerability** entry when available. Do not include credentials or exploitable private-system details in a public issue. For non-sensitive reproducible defects, the regular issue template is appropriate.

Only the current developer preview is maintained. No response-time or security certification is promised.
