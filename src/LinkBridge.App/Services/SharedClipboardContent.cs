namespace LinkBridge.App.Services;

/// <summary>A transient native clipboard item. Kind is "text" (UTF-8) or "png" (lossless original pixels).</summary>
public sealed record SharedClipboardContent(string Kind, byte[] Data);
