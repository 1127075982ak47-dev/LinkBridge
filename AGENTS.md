# Working on LinkBridge

- Read README.md, docs/BUILDING.md and docs/VERIFICATION.md before changing platform behavior.
- Preserve the circular workspace and bind connection feedback to actual transport state.
- Prefer programmatic drawing for regular shapes, icons and animation. Avoid per-frame layout work.
- Test native OS behavior separately from transport tests. A build or loopback test is not evidence of complete cross-machine interaction.
- Keep pairing profiles, private signing keys, credentials, clipboard contents and personal logs outside the repository.
- Use your own private pairing profiles and local signing identity. Preserve installed macOS code identity across routine updates.
- Preserve third-party copyright and license notices. Update THIRD_PARTY_NOTICES.md when changing dependencies.
- Do not claim experimental camera, microphone or reverse file-drag paths are complete.
