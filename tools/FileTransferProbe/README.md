# File transfer transport probe

Run `dotnet run --project tools/FileTransferProbe -c Release` from the repository root.

The probe creates a unique temporary directory and real encrypted loopback sockets. It uses a random temporary PSK and never reads product pairing data. It checks binary/Unicode/empty files and directories, duplicate selected root names, committed destination receipts, success waiting for the destination callback, concurrent transfer rejection, wrong PSK/context/IP, remote traversal and conflicting trees, size bounds, tampered file hashes, cancellation before upload completion and during destination commit, changed source files, and symbolic-link refusal. Symbolic-link creation is explicitly reported as skipped when the operating system denies the test fixture.

This probe verifies transport and callback ordering. Native Explorer edge handoff and Finder file promises require the separate application integration and live cross-machine acceptance test.

## Integration contract

- TLS context `LBF1`, default TCP port `45686`; supply the existing paired IP and 32-character printable ASCII pairing secret. The server does not alter keys or settings.
- `FileTransferSender.CreateManifest(paths, id)` captures names and lengths without a prehash pass. Use the overload taking that manifest and the original source paths when the native drag needs a known transfer ID. Files are streamed once and hashed as sent.
- Relative entry paths include their root name. Every root appears in `Entries`; every directory is explicit, including empty directories. Selection root basename collisions are given ` (2)` suffixes. Source paths are never deleted or moved.
- `manifestAccepted(manifest, staging, ct)` runs before upload. Start the native promised-file drag and return promptly; its promise writers must wait for `uploadReady`. Do not copy incomplete staging or report a destination success from this callback.
- `uploadReady(id, staging, ct)` runs only after every file passed SHA-256 and the full transfer ended. Await the actual native destination writes before returning; invoke `server.ReportCommittedDestinations(id, paths)` for the actual committed root paths. The sender reports success only after this callback returns and a receipt is received.
- `failed(id, error)` receives the error message, allowing the native integration to cancel pending promises. Failed staging is deleted after this callback returns. Successfully committed staging remains owned by the native integration, which must clean it only when all promise readers finished.
- Callbacks must honor cancellation. Client disconnect cancels a waiting destination callback. Each network operation has a 45-second idle timeout; native destination completion has a 10-minute deadline. Only one authenticated transfer may be active per server.
- Names are portable between both systems; paths escaping staging, duplicate case-insensitive paths, incomplete parent directories, reserved Windows device names, symlinks and reparse points are refused. Limits: 16,384 tree entries, 128 selected roots, 4 MiB manifest, 4,096 UTF-8 bytes/path, 64 GiB/file, 256 GiB/session.
- Progress is throttled to approximately 10 updates/second during streaming and follows real byte/file counters. The final phase appears only after the native destination receipt.
