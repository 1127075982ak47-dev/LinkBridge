# Clipboard transport

Run `dotnet run --project tools/ClipboardProbe -c Release`.

The probe uses real encrypted loopback sockets and a random temporary pairing key. It never reads product pairing settings or records clipboard contents. Six groups verify exact UTF-8/PNG bytes, immutable sender snapshot and retained source buffer, duplicate-ID rejection, ACK waiting for the clipboard callback, denied apply not reporting success, wrong key/context/IP, oversize headers refused before allocation, invalid UTF-8/PNG and SHA-256 mismatch, cancellation reaching the callback, and partial uploads never applying.

## App integration

```csharp
using LinkBridge.Core.Clipboard;

var server = new ClipboardBridgeServer(allowedPeerAddress, existingPairingSecret,
    async (payload, ct) => {
        ct.ThrowIfCancellationRequested();
        // Apply on the application's UI thread. Use UTF-8 text for Kind == "text",
        // or original lossless PNG bytes for Kind == "png". Await the native write.
        // Update the app's content-hash suppression state before clipboard polling resumes.
    });
server.Start();

await new ClipboardBridgeSender().SendAsync(peerAddress, existingPairingSecret,
    new ClipboardPayload(Guid.NewGuid(), ClipboardKinds.Text, utf8Bytes), cancellationToken);
```

`SendAsync` returns only after the peer's clipboard callback succeeds. The callback must honor cancellation; cancellation and permission failures are not successful acknowledgements. `DisposeAsync` cancels incoming work and waits for callbacks. Byte limits are 1 MiB text / 32 MiB PNG; PNG dimensions are bounded to 64 million pixels without altering the original image. The short-lived TLS connection uses `LBC1` / TCP `45687` and the existing paired IP and 32-character secret.

The Core server remembers only 256 recent request IDs to refuse repeats, not clipboard contents. The application must suppress clipboard echo by content hash across received and locally observed content. No file clipboard, file paths, clipboard history or persistent data store are involved. Diagnostic events are generic and contain no contents, hashes, IDs, paths, keys or provider exception messages.

The loopback probe verifies protocol safety and callback ordering. Native copy/paste between Windows and Mac must also be verified in the actual application.
