namespace LinkBridge.Core.WindowRoaming;

public sealed record WindowDescriptor(Guid SessionId, string Title, int Width, int Height, int Stride,
    string SourcePlatform = "Windows", double SourceScale = 1, bool ContinueDrag = false,
    int DragOffsetX = 0, int DragOffsetY = 0, double EntryY = 0.5);
public sealed record WindowFrame(Guid SessionId, int Sequence, int Width, int Height, byte[] Pixels);
public sealed record WindowRemoteInput(Guid SessionId, string Kind, int X = 0, int Y = 0, int Code = 0,
    bool IsDown = false, string? Text = null, int Width = 0, int Height = 0);
public enum WindowMessageType : byte { Descriptor = 1, Frame = 2, Input = 3, End = 4, Ready = 5, Heartbeat = 6, Status = 7 }
public sealed record WindowMessage(WindowMessageType Type, WindowDescriptor? Descriptor = null,
    WindowFrame? Frame = null, WindowRemoteInput? Input = null, string? Status = null);

public static class WindowRoamingLimits
{
    public const int DefaultPort = 45688;
    public const string ProtocolContext = "LBR1";
    public const int MaximumPixelsBytes = 64 * 1024 * 1024;
    public const int MaximumCompressedBytes = MaximumPixelsBytes + 64 * 1024;
    public const int MaximumMetadataBytes = 16 * 1024;
    public static int FrameBytes(int width, int height)
    {
        if (width is < 1 or > 8192 || height is < 1 or > 8192 ||
            (long)width * height * 4 > MaximumPixelsBytes)
            throw new InvalidDataException("窗口尺寸超出支持范围。");
        return checked(width * height * 4);
    }
    public static void Validate(WindowDescriptor value)
    {
        FrameBytes(value.Width, value.Height);
        if (value.SessionId == Guid.Empty || value.Stride != value.Width * 4 || value.Title is null || value.Title.Length > 512 ||
            value.SourcePlatform is not ("Windows" or "Mac") || !double.IsFinite(value.SourceScale) ||
            value.SourceScale is < 0.5 or > 4 || !double.IsFinite(value.EntryY) || value.EntryY is < 0 or > 1 ||
            value.DragOffsetX < 0 || value.DragOffsetY < 0 || value.DragOffsetX >= value.Width || value.DragOffsetY >= value.Height)
            throw new InvalidDataException("窗口描述无效。");
    }
    public static void Validate(WindowRemoteInput value)
    {
        if (value.SessionId == Guid.Empty || value.Kind is not ("move" or "button" or "wheel" or "key" or "text" or "release" or
            "resize" or "hit-test" or "hit-result" or "drag-start" or "drag-end" or "return-drag"))
            throw new InvalidDataException("窗口输入类型无效。");
        if (value.Kind is "move" or "button" or "wheel" or "hit-test")
        {
            FrameBytes(value.Width, value.Height);
            if (value.X < 0 || value.Y < 0 || value.X >= value.Width || value.Y >= value.Height)
                throw new InvalidDataException("窗口输入坐标无效。");
        }
        if (value.Kind == "resize") FrameBytes(value.Width, value.Height);
        if ((value.Kind == "hit-test" && value.Code < 1) ||
            (value.Kind == "hit-result" && (value.X < 1 || value.Code is < -2 or > 24)) ||
            (value.Kind == "return-drag" && (value.X is < 0 or > 8192 || value.Y is < 0 or > 8192 || value.Code is < 0 or > 10000)))
            throw new InvalidDataException("窗口拖动消息无效。");
        if ((value.Kind == "button" && value.Code is < 0 or > 4) ||
            (value.Kind == "key" && value.Code is < 1 or > 255) ||
            (value.Kind == "wheel" && Math.Abs((long)value.Code) > 12000) ||
            (value.Kind == "text" && (value.Text is null || value.Text.Length is < 1 or > 4096)))
            throw new InvalidDataException("窗口输入内容无效。");
    }
}
