# 0.0.1 verification

Evidence collected on 2026-10-02 for one paired Windows 11 x64 / Apple Silicon macOS desk. This is a development acceptance record, not a universal compatibility claim.

## Actual two-machine results

| Action | Observed receiving-side result |
| --- | --- |
| Pointer crossing and keyboard following | Authenticated input connection accepted by the installed Mac bundle; pointer transitions and return controls exercised. |
| Explorer file drag → Finder | Native CF_HDROP, source OLE cancellation and capture release, NSDraggingSession, Finder file promises, actual destination receipt and integrity verification. |
| Files and folders | Four file SHA-256 values matched independently at the destination. Unicode names, nested folders, empty folders and zero-byte files preserved; source files retained. |
| Mac text → Windows | Native Windows clipboard contained exact UTF-8 fixture text, including Chinese, emoji and newlines. |
| Windows text → Mac | Native NSPasteboard contained exact fixture text. |
| Mac PNG → Windows | 3840×2160 RGBA PNG, 53,739 bytes: dimensions and every original PNG byte retained. |
| Windows PNG → Mac | 320×180 RGBA PNG, 3,522 bytes: every original PNG byte retained. |
| Center connection flow | Actual captured center-region frames differed while the paired connection was active. Flow uses compositor visuals instead of per-frame disk layout. |
| macOS update identity | Repeated updates with the same installed bundle identifier and signing requirement continued to pass device-control authorization and actual input authentication. New computers still require initial permissions. |

No personal clipboard data, device identifiers, addresses or private signing materials are included in this record.

## Automated checks

- **Clipboard transport: six groups passed.** Exact UTF-8/PNG transfer, immutable source buffer, commit acknowledgement, wrong key/context/IP, invalid lengths/UTF-8/PNG/hash, cancellation and partial disconnect behavior.
- **File transfer: ten groups passed.** Binary/Unicode/empty directory fixtures, real receipt, concurrent rejection, wrong key/context/IP, path traversal and limits, hash tampering, disconnect cleanup, cancellation, changed source and symlink rejection.
- **Windows clipboard adapter.** Actual native Unicode, PNG, DIBV5 and DIB formats; source dimensions and semitransparent channels; startup history, file-list exclusion and own-write echo suppression.
- **Mac clipboard adapter.** Private pasteboard Unicode/PNG exactness, TIFF-to-PNG conversion with per-pixel RGBA equivalence, and file-URL/filename exclusion.
- **Release build.** Application and relevant probes compiled with zero warnings and errors during local acceptance.

## Boundaries

- The complete system screenshot-selector button-and-selection flow has not completed live acceptance. Image transport and native clipboard writes are verified separately.
- Center-flow behavior has not been exhaustively examined for every window state and display scale.
- Native file drag currently supports Windows → Mac Finder copying. Reverse dragging, file clipboard sharing and dragging into remote application attachment fields are not delivered.
- Microphone/camera application devices require additional native drivers, signing and approval. They remain experimental.
- Shared Windows speaker playback has an existing real link, but every endpoint and target application was not retested in this acceptance round.
- Clipboard supports plain UTF-8 text up to 1 MiB and PNG up to 32 MiB / 64 million pixels. Unsupported or oversized data is rejected; images are not silently downsampled.
- Public source users need their own runtimes, pairing profiles, permissions and signing identity. First-run pairing does not yet have a complete UI wizard.
