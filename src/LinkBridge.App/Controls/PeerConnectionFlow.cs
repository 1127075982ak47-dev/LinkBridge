using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Rendering.Composition;
using Avalonia.Rendering.Composition.Animations;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace LinkBridge.App.Controls;

/// <summary>
/// A small connection wave between the two computer illustrations. Only three
/// cached dot visuals move in the compositor; the disk and wave never repaint per frame.
/// </summary>
public sealed class PeerConnectionFlow : Panel
{
    public static readonly StyledProperty<bool> IsConnectedProperty =
        AvaloniaProperty.Register<PeerConnectionFlow, bool>(nameof(IsConnected));
    public static readonly StyledProperty<bool> ReduceMotionProperty =
        AvaloniaProperty.Register<PeerConnectionFlow, bool>(nameof(ReduceMotion));

    public bool IsConnected { get => GetValue(IsConnectedProperty); set => SetValue(IsConnectedProperty, value); }
    public bool ReduceMotion { get => GetValue(ReduceMotionProperty); set => SetValue(ReduceMotionProperty, value); }

    private const double DesignWidth = 618, DesignHeight = 246, DotRadius = 12;
    private static readonly Point[] Knots = [new(165, 95), new(232, 120), new(305, 77), new(358, 108), new(410, 95)];
    private static readonly StreamGeometry Wave = CreateWave();
    private static readonly Point[] PathSamples = SampleWave();
    private static readonly IPen[] BlueLine = [Pen("#0877CFFF", 10), Pen("#167ECBFF", 6), Pen("#4398DBFF", 3.8), Pen("#BA73CEFF", 1.8), Pen("#EAF3FCFF", .85)];
    private static readonly IPen GrayLine = Pen("#709BA9BC", 1.35);
    private static readonly IBrush EndpointBrush = new SolidColorBrush(Color.Parse("#DDE8F9FF")).ToImmutable();
    private readonly GlowDot[] _dots = [new(), new(), new()];
    private readonly WaveLayer _line;
    private readonly List<CompositionVisual> _moving = [];
    private readonly List<Visual> _visibilityAncestors = [];
    private Window? _window;
    private bool _attached, _queued;
    private Size _animatedSize;

    public PeerConnectionFlow()
    {
        Width = DesignWidth; Height = DesignHeight;
        IsHitTestVisible = false; ClipToBounds = false;
        _line = new WaveLayer(this) { IsHitTestVisible = false };
        Children.Add(_line);
        foreach (var dot in _dots) { dot.IsHitTestVisible = false; dot.Opacity = 0; Children.Add(dot); }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsConnectedProperty) _line?.InvalidateVisual();
        if (change.Property == IsConnectedProperty || change.Property == ReduceMotionProperty || change.Property == IsVisibleProperty)
            QueueMotionUpdate();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        _window = TopLevel.GetTopLevel(this) as Window;
        if (_window is not null) _window.PropertyChanged += WindowPropertyChanged;
        foreach (var ancestor in this.GetVisualAncestors().Where(visual => visual != _window))
        { _visibilityAncestors.Add(ancestor); ancestor.PropertyChanged += WindowPropertyChanged; }
        QueueMotionUpdate();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _attached = false;
        if (_window is not null) _window.PropertyChanged -= WindowPropertyChanged;
        foreach (var ancestor in _visibilityAncestors) ancestor.PropertyChanged -= WindowPropertyChanged;
        _visibilityAncestors.Clear();
        _window = null;
        StopMotion();
        base.OnDetachedFromVisualTree(e);
    }

    private void WindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Window.WindowStateProperty || e.Property == IsVisibleProperty)
            QueueMotionUpdate();
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var sx = finalSize.Width / DesignWidth; var sy = finalSize.Height / DesignHeight;
        _line.Arrange(new Rect(finalSize));
        foreach (var dot in _dots)
            dot.Arrange(new Rect(-DotRadius * sx, -DotRadius * sy, DotRadius * 2 * sx, DotRadius * 2 * sy));
        if (_animatedSize != finalSize) QueueMotionUpdate();
        return finalSize;
    }

    private void QueueMotionUpdate()
    {
        // State changes are infrequent. A render-priority post lets layout create child visuals first.
        if (!_attached || !IsConnected || ReduceMotion || !IsEffectivelyVisible || _window?.WindowState == WindowState.Minimized)
        { StopMotion(); return; }
        if (_queued) return;
        _queued = true;
        Dispatcher.UIThread.Post(() => { _queued = false; UpdateMotion(); }, DispatcherPriority.Render);
    }

    private void UpdateMotion()
    {
        if (!_attached || !IsConnected || ReduceMotion || !IsEffectivelyVisible || _window?.WindowState == WindowState.Minimized ||
            Bounds.Width <= 0 || Bounds.Height <= 0)
        { StopMotion(); return; }
        if (_moving.Count == _dots.Length && _animatedSize == Bounds.Size) return;
        StopMotion(); _animatedSize = Bounds.Size;
        var sx = Bounds.Width / DesignWidth; var sy = Bounds.Height / DesignHeight;
        for (var index = 0; index < _dots.Length; index++)
        {
            var dot = _dots[index];
            var visual = ElementComposition.GetElementVisual(dot);
            if (visual is null) continue;
            dot.Opacity = 1;
            var origin = new Vector3D(-DotRadius * sx, -DotRadius * sy, 0);
            var offset = visual.Compositor.CreateVector3DKeyFrameAnimation();
            var opacity = visual.Compositor.CreateScalarKeyFrameAnimation();
            offset.Duration = opacity.Duration = TimeSpan.FromSeconds(3.8);
            offset.IterationBehavior = opacity.IterationBehavior = AnimationIterationBehavior.Forever;
            var shift = index / 3d;
            var times = new SortedSet<double>(Enumerable.Range(0, 97).Select(step => step / 96d));
            if (shift > 0) { times.Add(1 - shift - .0001); times.Add(1 - shift + .0001); }
            foreach (var time in times)
            {
                var phase = time + shift;
                if (phase > 1) phase -= 1;
                var point = AtDistance(phase);
                offset.InsertKeyFrame((float)time, origin + new Vector3D(point.X * sx, point.Y * sy, 0));
                var fade = Math.Clamp(Math.Min(phase / .10, (1 - phase) / .10), 0, 1);
                opacity.InsertKeyFrame((float)time, (float)(fade * .95));
            }
            visual.StartAnimation("Offset", offset);
            visual.StartAnimation("Opacity", opacity);
            _moving.Add(visual);
        }
    }

    private void StopMotion()
    {
        foreach (var visual in _moving)
        {
            visual.StopAnimation("Offset"); visual.StopAnimation("Opacity");
            visual.Opacity = 0;
        }
        _moving.Clear();
        foreach (var dot in _dots) dot.Opacity = 0;
    }

    private static IPen Pen(string color, double width) =>
        new Pen(new SolidColorBrush(Color.Parse(color)), width, lineCap: PenLineCap.Round).ToImmutable();

    private static (Point C1, Point C2) Controls(int segment)
    {
        var start = Knots[segment]; var end = Knots[segment + 1];
        var previous = Knots[Math.Max(0, segment - 1)]; var next = Knots[Math.Min(Knots.Length - 1, segment + 2)];
        return (new Point(start.X + (end.X - previous.X) / 6, start.Y + (end.Y - previous.Y) / 6),
            new Point(end.X - (next.X - start.X) / 6, end.Y - (next.Y - start.Y) / 6));
    }

    private static StreamGeometry CreateWave()
    {
        var geometry = new StreamGeometry();
        using var path = geometry.Open(); path.BeginFigure(Knots[0], false);
        for (var index = 0; index < Knots.Length - 1; index++)
        { var controls = Controls(index); path.CubicBezierTo(controls.C1, controls.C2, Knots[index + 1]); }
        path.EndFigure(false); return geometry;
    }

    private static Point[] SampleWave()
    {
        var raw = new List<Point> { Knots[0] }; var distances = new List<double> { 0 };
        for (var segment = 0; segment < Knots.Length - 1; segment++)
        {
            var start = Knots[segment]; var end = Knots[segment + 1]; var controls = Controls(segment);
            for (var step = 1; step <= 64; step++)
            {
                var t = step / 64d; var u = 1 - t;
                var point = new Point(u * u * u * start.X + 3 * u * u * t * controls.C1.X + 3 * u * t * t * controls.C2.X + t * t * t * end.X,
                    u * u * u * start.Y + 3 * u * u * t * controls.C1.Y + 3 * u * t * t * controls.C2.Y + t * t * t * end.Y);
                var delta = point - raw[^1];
                distances.Add(distances[^1] + Math.Sqrt(delta.X * delta.X + delta.Y * delta.Y)); raw.Add(point);
            }
        }
        var result = new Point[193]; var cursor = 1;
        for (var index = 0; index < result.Length; index++)
        {
            var target = distances[^1] * index / (result.Length - 1);
            while (cursor < raw.Count - 1 && distances[cursor] < target) cursor++;
            var blend = (target - distances[cursor - 1]) / (distances[cursor] - distances[cursor - 1]);
            result[index] = raw[cursor - 1] + (raw[cursor] - raw[cursor - 1]) * blend;
        }
        return result;
    }
    private static Point AtDistance(double phase)
    {
        var position = Math.Clamp(phase, 0, 1) * (PathSamples.Length - 1);
        var index = Math.Min((int)position, PathSamples.Length - 2);
        return PathSamples[index] + (PathSamples[index + 1] - PathSamples[index]) * (position - index);
    }

    private sealed class GlowDot : Control
    {
        private static readonly IBrush Glow = new RadialGradientBrush
        {
            Center = new RelativePoint(.5, .5, RelativeUnit.Relative), GradientOrigin = new RelativePoint(.5, .5, RelativeUnit.Relative),
            RadiusX = new RelativeScalar(.5, RelativeUnit.Relative), RadiusY = new RelativeScalar(.5, RelativeUnit.Relative),
            GradientStops = new GradientStops
            {
                new(Color.Parse("#FEFFFFFF"), 0), new(Color.Parse("#F6E7FBFF"), .14), new(Color.Parse("#BF93DFFF"), .28),
                new(Color.Parse("#4173CFFF"), .5), new(Color.Parse("#0A61C6FF"), .78), new(Colors.Transparent, 1)
            }
        }.ToImmutable();
        public override void Render(DrawingContext context)
        { context.DrawEllipse(Glow, null, new Rect(Bounds.Size)); }
    }

    private sealed class WaveLayer(PeerConnectionFlow owner) : Control
    {
        public override void Render(DrawingContext context)
        {
            if (Bounds.Width <= 0 || Bounds.Height <= 0) return;
            using var scale = context.PushTransform(Matrix.CreateScale(Bounds.Width / DesignWidth, Bounds.Height / DesignHeight));
            if (!owner.IsConnected) { context.DrawGeometry(null, GrayLine, Wave); return; }
            foreach (var pen in BlueLine) context.DrawGeometry(null, pen, Wave);
            context.DrawEllipse(EndpointBrush, null, Knots[0], 1.75, 1.75);
            context.DrawEllipse(EndpointBrush, null, Knots[^1], 1.75, 1.75);
        }
    }
}
