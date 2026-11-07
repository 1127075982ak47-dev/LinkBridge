namespace LinkBridge.App.Services;

/// <summary>A transient native clipboard item: text, lossless PNG, or local file-list metadata.</summary>
public sealed record SharedClipboardContent(string Kind, byte[] Data);
