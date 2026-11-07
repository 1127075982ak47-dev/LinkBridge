namespace LinkBridge.Input;

/// <summary>A centered, bounded seam underneath one Windows monitor.</summary>
public readonly record struct BottomScreenSeam(int X, int Y, int Width, int Height, int SeamWidth)
{
    public int Start => X + (Width - Math.Clamp(SeamWidth, 1, Width)) / 2;
    public int End => Start + Math.Clamp(SeamWidth, 1, Width);
    public bool ContainsEdge(int x, int y) => x >= Start && x < End && y >= Y + Height - 2 && y < Y + Height;
    public int RemoteX(int x, int remoteWidth) => (int)Math.Clamp((long)(x - Start) * remoteWidth / (End - Start), 0, remoteWidth - 1);
    public int LocalX(int x, int remoteWidth) => Start + (int)Math.Clamp((long)x * (End - Start) / remoteWidth, 0, End - Start - 1);
    public bool Returns(int y, int deltaY) => deltaY < 0 && y + deltaY <= 0;
}
