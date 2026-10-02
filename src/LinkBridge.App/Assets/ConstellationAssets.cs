using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace LinkBridge.App.Assets;

/// <summary>实体插图单次解码，按实际显示尺寸缓存；几何材质由原生控件绘制。</summary>
public static class ConstellationAssets
{
    private static readonly Lazy<Bitmap> ComputersValue = new(() => Load("DualComputers.png", 1400));
    private static readonly Lazy<Bitmap> KeyboardValue = new(() => Load("KeyboardMouse.png", 900));
    private static readonly Lazy<Bitmap> CameraValue = new(() => Load("CameraProduct.png", 512));
    private static readonly Lazy<Bitmap> SpeakerValue = new(() => Load("DefaultSpeaker.png", 512));
    public static Bitmap Computers => ComputersValue.Value;
    public static Bitmap Keyboard => KeyboardValue.Value;
    public static Bitmap Camera => CameraValue.Value;
    public static Bitmap Speaker => SpeakerValue.Value;
    private static Bitmap Load(string name, int width)
    {
        using var stream = AssetLoader.Open(new Uri("avares://LinkBridge.App/Assets/ConstellationC/" + name));
        return Bitmap.DecodeToWidth(stream, width, BitmapInterpolationMode.HighQuality);
    }
}
