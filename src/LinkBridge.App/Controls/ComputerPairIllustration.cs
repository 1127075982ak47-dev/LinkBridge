using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace LinkBridge.App.Controls;

/// <summary>Clean vector monitors; connection light is drawn only by PeerConnectionFlow.</summary>
public sealed class ComputerPairIllustration : Control
{
    private static readonly IBrush Metal = Gradient("#FCFFFFFF", "#A2B4CA");
    private static readonly IBrush WindowsScreen = Gradient("#071F67", "#2878FA");
    private static readonly IBrush MacScreen = Gradient("#291676", "#9871ED");
    private static readonly IPen Rim = new Pen(Brush.Parse("#243247"), 2).ToImmutable();
    private static readonly IPen BaseRim = new Pen(Brush.Parse("#8DA0B9"), .8).ToImmutable();
    private static readonly IBrush White = Brush.Parse("#D8F4FFFF").ToImmutable();
    private static readonly IBrush Shadow = Brush.Parse("#183E5778").ToImmutable();
    private static readonly IPen ScreenReflection = new Pen(Brush.Parse("#6596C7FF"), 1).ToImmutable();
    private static readonly IPen ChinReflection = new Pen(Brush.Parse("#24FFFFFF"), .8).ToImmutable();
    private static readonly IBrush GlassReflection = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(1, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
        GradientStops = new GradientStops { new(Color.Parse("#38FFFFFF"), 0), new(Color.Parse("#07FFFFFF"), .72), new(Colors.Transparent, 1) }
    }.ToImmutable();
    private static readonly IBrush Bezel = Gradient("#233349", "#697B93");
    private static readonly IBrush Side = Gradient("#4B5F78", "#233349");
    private static readonly Geometry Outer = Geometry.Parse("M32 21 L230 44 Q235 45 236 50 L251 172 Q252 176 248 177 L50 186 Q44 187 44 181 L26 27 Q26 21 32 21 Z");
    private static readonly Geometry Screen = Geometry.Parse("M30 26 L232 48 L247 167 L46 176 Z");
    private static readonly Geometry Chin = Geometry.Parse("M46 176 L247 167 L251 177 L44 186 Z");
    private static readonly Geometry SideEdge = Geometry.Parse("M235 44 L240 46 L256 175 L251 177 Z");
    private static readonly Geometry Reflection = Geometry.Parse("M31 27 L232 49 L244 151 Z");
    private static readonly Geometry Stand = Geometry.Parse("M109 178 L107 199 C107 205 97 209 89 212 L176 212 C168 209 158 205 158 199 L156 178 Z");
    private static readonly Geometry Leaf = Geometry.Parse("M484 74 C485 65 490 61 497 60 C497 66 493 72 484 74 Z");
    private static readonly Geometry Apple = Geometry.Parse("M485 80 C476 74 465 80 465 92 C465 104 474 116 480 115 C484 113 488 113 492 115 C499 116 505 106 507 101 C500 98 498 89 505 84 C499 77 492 77 485 80 Z");
    private static IBrush Gradient(string first, string last) => new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
        GradientStops = new GradientStops { new(Color.Parse(first), 0), new(Color.Parse(last), 1) }
    }.ToImmutable();
    public override void Render(DrawingContext context)
    {
        if (Bounds.Width <= 0 || Bounds.Height <= 0) return;
        using var scale = context.PushTransform(Matrix.CreateScale(Bounds.Width / 618, Bounds.Height / 246));
        DrawMonitor(context, false, WindowsScreen);
        DrawMonitor(context, true, MacScreen);
        var white = White;
        context.DrawRectangle(white, null, new Rect(112, 79, 18, 18));
        context.DrawRectangle(white, null, new Rect(133, 79, 18, 18));
        context.DrawRectangle(white, null, new Rect(112, 100, 18, 18));
        context.DrawRectangle(white, null, new Rect(133, 100, 18, 18));
        context.DrawGeometry(white, null, Leaf);
        context.DrawGeometry(white, null, Apple);
    }
    private static void DrawMonitor(DrawingContext context, bool mirror, IBrush screen)
    {
        using var perspective = context.PushTransform(mirror
            ? Matrix.CreateScale(-1, 1) * Matrix.CreateTranslation(618, 0) : Matrix.Identity);
        context.DrawEllipse(Shadow, null, new Rect(70, 209, 134, 8));
        context.DrawGeometry(Metal, BaseRim, Stand);
        context.DrawGeometry(Side, null, SideEdge);
        context.DrawGeometry(Bezel, Rim, Outer);
        context.DrawGeometry(screen, null, Screen);
        context.DrawGeometry(GlassReflection, null, Reflection);
        context.DrawGeometry(Metal, null, Chin);
        context.DrawLine(ScreenReflection, new Point(34, 32), new Point(241, 161));
        context.DrawLine(ChinReflection, new Point(48, 183), new Point(247, 175));
    }
}
