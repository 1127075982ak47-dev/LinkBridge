using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace LinkBridge.App.Controls;

public enum CircularGlassStyleKind
{
    Shell,
    Pearl,
    Coral
}

/// <summary>
/// A transparent-corner, perfectly circular glass surface. All geometry and
/// immutable paints are shared; resizing only changes the render transform.
/// The outer 4% is reserved for a small, sharply bounded optical halo.
/// </summary>
public sealed class CircularGlassSurface : Control
{
    public static readonly StyledProperty<CircularGlassStyleKind> StyleKindProperty =
        AvaloniaProperty.Register<CircularGlassSurface, CircularGlassStyleKind>(
            nameof(StyleKind), CircularGlassStyleKind.Shell);

    public static readonly StyledProperty<bool> IsActiveProperty =
        AvaloniaProperty.Register<CircularGlassSurface, bool>(nameof(IsActive));

    public CircularGlassStyleKind StyleKind
    {
        get => GetValue(StyleKindProperty);
        set => SetValue(StyleKindProperty, value);
    }

    public bool IsActive
    {
        get => GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    private static readonly Point Center = new(500, 500);
    private static readonly Rect Face = new(40, 40, 920, 920);
    private static readonly Rect InnerFace = new(53, 53, 894, 894);

    private static readonly IBrush ShellFace = Linear(
        (0, "#FFFFFFFF"), (.22, "#FFECF5FF"), (.53, "#FFDFEAF9"),
        (.76, "#FFEAF0FA"), (1, "#FFEDF4FF"));
    // The continuous optical backing closes the space between the face and halo.
    // Without it, a dark desktop becomes a black ring through those alpha gaps.
    private static readonly IBrush ShellBacking = Linear(
        (0, "#FFF2F8FF"), (.28, "#FFDCE8F8"), (.66, "#FFD2E1F6"), (1, "#FFEDF5FF"));
    private static readonly IBrush PearlFace = Linear(
        (0, "#FCFFFFFF"), (.28, "#F6EEF8FF"), (.64, "#EDE1E5FA"),
        (.87, "#F4E8EDFF"), (1, "#FCF8FBFF"));
    private static readonly IBrush CoralFace = Linear(
        (0, "#FFFFFEFC"), (.32, "#FFFDF5EF"), (.67, "#F7F6E4E4"),
        (1, "#FEFFF5F1"));

    private static readonly IBrush ShellScatter = Radial(.29, .16, .91,
        (0, "#AAFFFFFF"), (.35, "#38FFFFFF"), (.8, "#10B9DFFF"), (1, "#00000000"));
    private static readonly IBrush PearlScatter = Radial(.23, .17, .88,
        (0, "#DFFFFFFF"), (.42, "#51FFFFFF"), (.76, "#09CFEBFF"), (1, "#00000000"));
    private static readonly IBrush CoralScatter = Radial(.25, .17, .91,
        (0, "#DFFFFFFF"), (.39, "#55FFFFFF"), (.78, "#10FFB495"), (1, "#00000000"));
    private static readonly IBrush ShellEdge = Radial(.5, .5, .5,
        (0, "#00000000"), (.79, "#00000000"), (.94, "#209CD8FF"),
        (.98, "#80E8F5FF"), (1, "#F2FFFFFF"));
    private static readonly IBrush PearlEdge = Radial(.5, .5, .5,
        (0, "#00000000"), (.8, "#00000000"), (.95, "#38B9DFFF"),
        (.985, "#B0FFFFFF"), (1, "#FEFFFFFF"));
    private static readonly IBrush CoralEdge = Radial(.5, .5, .5,
        (0, "#00000000"), (.82, "#00000000"), (.95, "#50FFC0A7"),
        (.985, "#CDFFF8F2"), (1, "#FFFFFFFF"));

    private static readonly IPen ShellRim = Pen("#E8FFFFFF", 2.2);
    private static readonly IPen ShellInnerRim = Pen("#4089A5CE", 1.1);
    private static readonly IPen PodRim = Pen("#EEFFFFFF", 5.5);
    private static readonly IPen PodInnerRim = Pen("#40C1C9E4", 2);
    private static readonly IPen WarmInnerRim = Pen("#55E9C3BF", 2);
    private static readonly IPen WhiteShellArc = Pen("#DBFFFFFF", 3.7);
    private static readonly IPen CyanShellArc = Pen("#BE93E7FF", 3.1);
    private static readonly IPen VioletShellArc = Pen("#73CEC8FF", 2.5);
    private static readonly IPen WhitePodArc = Pen("#EEFFFFFF", 8);
    private static readonly IPen CyanPodArc = Pen("#78ACECFF", 5);
    private static readonly IPen WarmPodArc = Pen("#89FFC5AA", 5);
    private static readonly IPen ActiveShellArc = Pen("#CC7BE7FF", 4.2);
    private static readonly IPen ActivePodArc = Pen("#BD72DAFF", 8);
    private static readonly IPen ActiveWarmArc = Pen("#C8FFB798", 8);

    // Four rings make a compact optical halo without a blur layer or filter.
    private static readonly IPen[] CoolHalo =
    [Pen("#08A0D9FF", 14), Pen("#129EDFFF", 10), Pen("#2998E5FF", 6), Pen("#62B4EBFF", 3)];
    private static readonly IPen[] WarmHalo =
    [Pen("#09FFC0AB", 14), Pen("#18FFB89E", 10), Pen("#34FFC6AC", 6), Pen("#65FFD9C8", 3)];
    private static readonly double[] HaloRadii = [487, 478, 470, 463];
    private static readonly IPen[] ShellHalo =
        [Pen("#18CDE9FF", 8), Pen("#30E2F3FF", 5), Pen("#58F2FAFF", 2)];
    private static readonly double[] ShellHaloRadii = [489, 485, 480];

    private static readonly Geometry TopReflection = Arc(455, 207, 318);
    private static readonly Geometry LeftReflection = Arc(458, 135, 188);
    private static readonly Geometry LowerReflection = Arc(458, 26, 91);
    private static readonly Geometry InnerReflection = Arc(444, 222, 293);
    private static readonly Geometry ActiveReflection = Arc(459, 310, 365);

    static CircularGlassSurface()
    {
        AffectsRender<CircularGlassSurface>(StyleKindProperty, IsActiveProperty);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var side = Math.Min(Bounds.Width, Bounds.Height);
        if (side <= 0 || !double.IsFinite(side)) return;
        var scale = side / 1000;
        using var transform = context.PushTransform(
            Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(
                (Bounds.Width - side) / 2, (Bounds.Height - side) / 2));

        var shell = StyleKind == CircularGlassStyleKind.Shell;
        var coral = StyleKind == CircularGlassStyleKind.Coral;
        if (shell)
        {
            context.DrawEllipse(ShellBacking, null, Center, 486, 486);
            for (var i = 0; i < ShellHaloRadii.Length; i++)
                context.DrawEllipse(null, ShellHalo[i], Center, ShellHaloRadii[i], ShellHaloRadii[i]);
        }
        else
        {
            var halo = coral ? WarmHalo : CoolHalo;
            for (var i = 0; i < HaloRadii.Length; i++)
                context.DrawEllipse(null, halo[i], Center, HaloRadii[i], HaloRadii[i]);
        }

        context.DrawEllipse(coral ? CoralFace : shell ? ShellFace : PearlFace, null, Face);
        context.DrawEllipse(coral ? CoralScatter : shell ? ShellScatter : PearlScatter, null, Face);
        context.DrawEllipse(coral ? CoralEdge : shell ? ShellEdge : PearlEdge,
            shell ? ShellRim : PodRim, Face);
        context.DrawEllipse(null, shell ? ShellInnerRim : coral ? WarmInnerRim : PodInnerRim, InnerFace);

        context.DrawGeometry(null, shell ? WhiteShellArc : WhitePodArc, TopReflection);
        context.DrawGeometry(null, shell ? CyanShellArc : coral ? WarmPodArc : CyanPodArc, LeftReflection);
        context.DrawGeometry(null, shell ? VioletShellArc : coral ? WarmPodArc : CyanPodArc, LowerReflection);
        if (!shell) context.DrawGeometry(null, PodInnerRim, InnerReflection);
        if (IsActive)
            context.DrawGeometry(null, shell ? ActiveShellArc : coral ? ActiveWarmArc : ActivePodArc,
                ActiveReflection);
    }

    private static IPen Pen(string color, double width) =>
        new Pen(new SolidColorBrush(Color.Parse(color)), width,
            lineCap: PenLineCap.Round).ToImmutable();

    private static IBrush Linear(params (double Offset, string Color)[] stops)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(.25, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(.75, 1, RelativeUnit.Relative),
            GradientStops = Stops(stops)
        };
        return brush.ToImmutable();
    }

    private static IBrush Radial(double x, double y, double radius,
        params (double Offset, string Color)[] stops)
    {
        var brush = new RadialGradientBrush
        {
            Center = new RelativePoint(x, y, RelativeUnit.Relative),
            GradientOrigin = new RelativePoint(x, y, RelativeUnit.Relative),
            RadiusX = new RelativeScalar(radius, RelativeUnit.Relative),
            RadiusY = new RelativeScalar(radius, RelativeUnit.Relative),
            GradientStops = Stops(stops)
        };
        return brush.ToImmutable();
    }

    private static GradientStops Stops((double Offset, string Color)[] stops)
    {
        var result = new GradientStops();
        foreach (var stop in stops) result.Add(new GradientStop(Color.Parse(stop.Color), stop.Offset));
        return result;
    }

    private static Geometry Arc(double radius, double start, double end)
    {
        static Point At(double radius, double degrees)
        {
            var angle = degrees * Math.PI / 180;
            return new Point(500 + radius * Math.Cos(angle), 500 + radius * Math.Sin(angle));
        }
        var geometry = new StreamGeometry();
        using (var path = geometry.Open())
        {
            path.BeginFigure(At(radius, start), false);
            path.ArcTo(At(radius, end), new Size(radius, radius), 0,
                end - start > 180, SweepDirection.Clockwise);
            path.EndFigure(false);
        }
        return geometry;
    }
}
