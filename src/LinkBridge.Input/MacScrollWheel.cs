namespace LinkBridge.Input;

/// <summary>Converts Windows wheel units to Mac logical pixels, retaining fine wheel input.</summary>
public sealed class MacScrollWheel
{
    // A Windows detent is 120 units. User-approved speed: 60 logical pixels.
    public const int PixelsPerDetent = 60;
    private long _remainder;

    public int Convert(int windowsDelta)
    {
        var scaled = (long)windowsDelta * PixelsPerDetent + _remainder;
        var pixels = scaled / 120;
        _remainder = scaled % 120;
        return checked((int)pixels);
    }

    public void Reset() => _remainder = 0;
}
