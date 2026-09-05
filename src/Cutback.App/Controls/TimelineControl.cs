using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Cutback.Core;
using Cutback.Core.Models;
using Cutback.Media;
using SkiaSharp;

namespace Cutback.App.Controls;

/// <summary>
/// The whole source timeline: ruler, waveform, kept and removed segments, playhead.
/// </summary>
/// <remarks>
/// Interactions:
/// <list type="bullet">
/// <item>Click or drag in the ruler: seek / scrub.</item>
/// <item>Click a segment: toggle it.</item>
/// <item>Drag a segment boundary: move it (snapped to the quietest nearby point on release).</item>
/// <item>Drag in the body: pan. Mouse wheel: zoom about the cursor. Shift+wheel or horizontal wheel: pan.</item>
/// </list>
/// Rendering is SkiaSharp through Avalonia's API lease, so it uses the SkiaSharp Avalonia ships.
/// </remarks>
public sealed class TimelineControl : Control
{
    public static readonly StyledProperty<SegmentList?> SegmentsProperty =
        AvaloniaProperty.Register<TimelineControl, SegmentList?>(nameof(Segments));

    public static readonly StyledProperty<Waveform?> WaveformProperty =
        AvaloniaProperty.Register<TimelineControl, Waveform?>(nameof(Waveform));

    public static readonly StyledProperty<double> DurationProperty =
        AvaloniaProperty.Register<TimelineControl, double>(nameof(Duration));

    public static readonly StyledProperty<double> PositionProperty =
        AvaloniaProperty.Register<TimelineControl, double>(nameof(Position));

    public static readonly StyledProperty<ICommand?> SeekCommandProperty =
        AvaloniaProperty.Register<TimelineControl, ICommand?>(nameof(SeekCommand));

    public static readonly StyledProperty<ICommand?> ToggleSegmentCommandProperty =
        AvaloniaProperty.Register<TimelineControl, ICommand?>(nameof(ToggleSegmentCommand));

    public static readonly StyledProperty<ICommand?> MoveBoundaryCommandProperty =
        AvaloniaProperty.Register<TimelineControl, ICommand?>(nameof(MoveBoundaryCommand));

    private const double RulerHeight = 22;
    private const double BoundaryHitHalfWidth = 5;
    private const double DragThreshold = 4;
    private const double MinViewSpanSeconds = 0.25;
    private const double SnapWindowSeconds = 0.020;

    private double _viewStart;
    private double _viewEnd;

    private enum Gesture
    {
        None,
        Pending,
        Scrub,
        Pan,
        Boundary,
    }

    private Gesture _gesture;
    private Point _pressPoint;
    private double _panStartViewStart;
    private int _dragBoundaryIndex = -1;
    private int _hoverSegment = -1;
    private int _hoverBoundary = -1;
    private SegmentList? _subscribed;

    static TimelineControl()
    {
        AffectsRender<TimelineControl>(WaveformProperty, DurationProperty, PositionProperty);
        FocusableProperty.OverrideDefaultValue<TimelineControl>(true);
    }

    public TimelineControl()
    {
        ClipToBounds = true;
        Cursor = new Cursor(StandardCursorType.Arrow);
    }

    public SegmentList? Segments
    {
        get => GetValue(SegmentsProperty);
        set => SetValue(SegmentsProperty, value);
    }

    public Waveform? Waveform
    {
        get => GetValue(WaveformProperty);
        set => SetValue(WaveformProperty, value);
    }

    public double Duration
    {
        get => GetValue(DurationProperty);
        set => SetValue(DurationProperty, value);
    }

    public double Position
    {
        get => GetValue(PositionProperty);
        set => SetValue(PositionProperty, value);
    }

    /// <summary>Parameter: <see cref="double"/> seconds.</summary>
    public ICommand? SeekCommand
    {
        get => GetValue(SeekCommandProperty);
        set => SetValue(SeekCommandProperty, value);
    }

    /// <summary>Parameter: <see cref="int"/> segment index.</summary>
    public ICommand? ToggleSegmentCommand
    {
        get => GetValue(ToggleSegmentCommandProperty);
        set => SetValue(ToggleSegmentCommandProperty, value);
    }

    /// <summary>Parameter: <see cref="BoundaryMove"/>.</summary>
    public ICommand? MoveBoundaryCommand
    {
        get => GetValue(MoveBoundaryCommandProperty);
        set => SetValue(MoveBoundaryCommandProperty, value);
    }

    // ---- property changes ---------------------------------------------------------------------

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == SegmentsProperty)
        {
            if (_subscribed is not null)
            {
                _subscribed.Changed -= OnSegmentsChanged;
            }

            _subscribed = change.GetNewValue<SegmentList?>();
            if (_subscribed is not null)
            {
                _subscribed.Changed += OnSegmentsChanged;
            }

            InvalidateVisual();
        }
        else if (change.Property == DurationProperty)
        {
            _viewStart = 0;
            _viewEnd = Math.Max(0, change.GetNewValue<double>());
            InvalidateVisual();
        }
        else if (change.Property == PositionProperty)
        {
            KeepPlayheadVisible(change.GetNewValue<double>());
        }
    }

    private void OnSegmentsChanged(object? sender, EventArgs e) => InvalidateVisual();

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_subscribed is not null)
        {
            _subscribed.Changed -= OnSegmentsChanged;
            _subscribed = null;
        }
    }

    // ---- coordinate mapping -------------------------------------------------------------------

    private double ViewSpan => Math.Max(1e-6, _viewEnd - _viewStart);

    private double TimeToX(double t) => (t - _viewStart) / ViewSpan * Bounds.Width;

    private double XToTime(double x) => _viewStart + x / Bounds.Width * ViewSpan;

    private double SecondsPerPixel => Bounds.Width > 0 ? ViewSpan / Bounds.Width : 0;

    private void SetView(double start, double span)
    {
        var duration = Duration;
        if (duration <= 0)
        {
            return;
        }

        span = Math.Clamp(span, Math.Min(MinViewSpanSeconds, duration), duration);
        start = Math.Clamp(start, 0, duration - span);
        _viewStart = start;
        _viewEnd = start + span;
        InvalidateVisual();
    }

    private void KeepPlayheadVisible(double position)
    {
        if (Duration <= 0 || _viewEnd - _viewStart >= Duration)
        {
            return;
        }

        if (position < _viewStart || position > _viewEnd)
        {
            SetView(position - ViewSpan * 0.1, ViewSpan);
        }
    }

    // ---- hit testing --------------------------------------------------------------------------

    private int BoundaryAt(Point p)
    {
        var segments = Segments;
        if (segments is null || p.Y < RulerHeight)
        {
            return -1;
        }

        var best = -1;
        var bestDistance = BoundaryHitHalfWidth + 1;
        for (var i = 1; i < segments.Count; i++)
        {
            var d = Math.Abs(TimeToX(segments.Segments[i].Start) - p.X);
            if (d < bestDistance)
            {
                bestDistance = d;
                best = i;
            }
        }

        return best;
    }

    private int SegmentAt(Point p)
    {
        var segments = Segments;
        if (segments is null || p.Y < RulerHeight || p.X < 0 || p.X > Bounds.Width)
        {
            return -1;
        }

        return segments.IndexAt(Math.Clamp(XToTime(p.X), 0, Duration));
    }

    // ---- pointer --------------------------------------------------------------------------------

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (Duration <= 0 || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        Focus();
        _pressPoint = e.GetPosition(this);
        e.Pointer.Capture(this);

        if (_pressPoint.Y < RulerHeight)
        {
            _gesture = Gesture.Scrub;
            Seek(XToTime(_pressPoint.X));
            return;
        }

        var boundary = BoundaryAt(_pressPoint);
        if (boundary >= 0)
        {
            _gesture = Gesture.Boundary;
            _dragBoundaryIndex = boundary;
            return;
        }

        _gesture = Gesture.Pending;
        _panStartViewStart = _viewStart;
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var p = e.GetPosition(this);

        switch (_gesture)
        {
            case Gesture.Scrub:
                Seek(XToTime(p.X));
                return;

            case Gesture.Boundary:
                MoveBoundary(_dragBoundaryIndex, XToTime(p.X), snap: false);
                return;

            case Gesture.Pending:
                if (Math.Abs(p.X - _pressPoint.X) > DragThreshold)
                {
                    _gesture = Gesture.Pan;
                    Cursor = new Cursor(StandardCursorType.SizeAll);
                }

                return;

            case Gesture.Pan:
                var dt = (p.X - _pressPoint.X) * SecondsPerPixel;
                SetView(_panStartViewStart - dt, ViewSpan);
                return;
        }

        // Idle hover feedback.
        var boundary = BoundaryAt(p);
        var segment = boundary >= 0 ? -1 : SegmentAt(p);
        if (boundary != _hoverBoundary || segment != _hoverSegment)
        {
            _hoverBoundary = boundary;
            _hoverSegment = segment;
            Cursor = new Cursor(boundary >= 0 ? StandardCursorType.SizeWestEast : StandardCursorType.Arrow);
            InvalidateVisual();
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        var p = e.GetPosition(this);

        switch (_gesture)
        {
            case Gesture.Pending:
                var index = SegmentAt(p);
                if (index >= 0 && ToggleSegmentCommand?.CanExecute(index) == true)
                {
                    ToggleSegmentCommand.Execute(index);
                }

                break;

            case Gesture.Boundary:
                MoveBoundary(_dragBoundaryIndex, XToTime(p.X), snap: true);
                break;
        }

        _gesture = Gesture.None;
        _dragBoundaryIndex = -1;
        Cursor = new Cursor(StandardCursorType.Arrow);
        e.Pointer.Capture(null);
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_gesture == Gesture.None && (_hoverSegment != -1 || _hoverBoundary != -1))
        {
            _hoverSegment = -1;
            _hoverBoundary = -1;
            InvalidateVisual();
        }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (Duration <= 0)
        {
            return;
        }

        var p = e.GetPosition(this);
        var horizontal = e.Delta.X != 0 ? e.Delta.X : (e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? e.Delta.Y : 0);

        if (horizontal != 0)
        {
            SetView(_viewStart - horizontal * ViewSpan * 0.1, ViewSpan);
        }
        else if (e.Delta.Y != 0)
        {
            // Zoom about the cursor: the time under the pointer stays under the pointer.
            var anchor = XToTime(p.X);
            var factor = Math.Pow(1.25, -e.Delta.Y);
            var span = ViewSpan * factor;
            span = Math.Clamp(span, Math.Min(MinViewSpanSeconds, Duration), Duration);
            var fraction = Bounds.Width > 0 ? p.X / Bounds.Width : 0.5;
            SetView(anchor - fraction * span, span);
        }

        e.Handled = true;
    }

    private void Seek(double time)
    {
        time = Math.Clamp(time, 0, Duration);
        if (SeekCommand?.CanExecute(time) == true)
        {
            SeekCommand.Execute(time);
        }
    }

    private void MoveBoundary(int boundaryIndex, double time, bool snap)
    {
        if (boundaryIndex < 0)
        {
            return;
        }

        if (snap && Waveform is { } waveform)
        {
            time = waveform.SnapToZeroCrossing(time, SnapWindowSeconds);
        }

        var move = new BoundaryMove(boundaryIndex, Math.Clamp(time, 0, Duration));
        if (MoveBoundaryCommand?.CanExecute(move) == true)
        {
            MoveBoundaryCommand.Execute(move);
        }
    }

    // ---- rendering ----------------------------------------------------------------------------

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var snapshot = new Snapshot(
            Bounds.Size,
            Segments?.Segments.ToArray() ?? [],
            Waveform,
            Duration,
            Position,
            _viewStart,
            _viewEnd,
            _hoverSegment,
            _gesture == Gesture.Boundary ? _dragBoundaryIndex : _hoverBoundary);
        context.Custom(new DrawOperation(new Rect(Bounds.Size), snapshot));
    }

    private sealed record Snapshot(
        Size Size,
        Segment[] Segments,
        Waveform? Waveform,
        double Duration,
        double Position,
        double ViewStart,
        double ViewEnd,
        int HoverSegment,
        int ActiveBoundary);

    private sealed class DrawOperation : ICustomDrawOperation
    {
        private static readonly SKColor RulerBackground = new(0x1E, 0x1E, 0x22);
        private static readonly SKColor BodyBackground = new(0x15, 0x15, 0x18);
        private static readonly SKColor KeptBackground = new(0x1F, 0x33, 0x52);
        private static readonly SKColor KeptBackgroundHover = new(0x27, 0x40, 0x66);
        private static readonly SKColor RemovedBackground = new(0x22, 0x22, 0x26);
        private static readonly SKColor RemovedBackgroundHover = new(0x2B, 0x2B, 0x30);
        private static readonly SKColor KeptWave = new(0x6F, 0xA8, 0xFF);
        private static readonly SKColor RemovedWave = new(0x4A, 0x4A, 0x52);
        private static readonly SKColor Boundary = new(0xC8, 0xC8, 0xD2, 0xB0);
        private static readonly SKColor BoundaryActive = new(0xFF, 0xD1, 0x66);
        private static readonly SKColor Playhead = new(0xFF, 0x5A, 0x5A);
        private static readonly SKColor Tick = new(0x55, 0x55, 0x5E);
        private static readonly SKColor Label = new(0xA0, 0xA0, 0xAA);
        private static readonly SKColor ReasonText = new(0x80, 0x80, 0x8A);
        private static readonly SKColor Hint = new(0x60, 0x60, 0x6A);

        private static readonly double[] NiceIntervals = [0.1, 0.25, 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 1200, 1800, 3600];

        private readonly Snapshot _s;

        public DrawOperation(Rect bounds, Snapshot snapshot)
        {
            Bounds = bounds;
            _s = snapshot;
        }

        public Rect Bounds { get; }

        public bool HitTest(Point p) => Bounds.Contains(p);

        public bool Equals(ICustomDrawOperation? other) => false;

        public void Dispose()
        {
        }

        public void Render(ImmediateDrawingContext context)
        {
            var lease = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
            if (lease is null)
            {
                return;
            }

            using var api = lease.Lease();
            var canvas = api.SkCanvas;
            var w = (float)_s.Size.Width;
            var h = (float)_s.Size.Height;
            var bodyTop = (float)RulerHeight;

            using var paint = new SKPaint { IsAntialias = false };

            paint.Color = RulerBackground;
            canvas.DrawRect(0, 0, w, bodyTop, paint);
            paint.Color = BodyBackground;
            canvas.DrawRect(0, bodyTop, w, h - bodyTop, paint);

            if (_s.Duration <= 0 || w <= 0)
            {
                DrawHint(canvas, w, h, paint);
                return;
            }

            DrawSegments(canvas, paint, w, bodyTop, h);
            DrawWaveform(canvas, paint, w, bodyTop, h);
            DrawBoundaries(canvas, paint, bodyTop, h);
            DrawRuler(canvas, paint, w, bodyTop);
            DrawPlayhead(canvas, paint, h);
        }

        private float TimeToX(double t) => (float)((t - _s.ViewStart) / (_s.ViewEnd - _s.ViewStart) * _s.Size.Width);

        private void DrawHint(SKCanvas canvas, float w, float h, SKPaint paint)
        {
            paint.Color = Hint;
            paint.IsAntialias = true;
            paint.TextSize = 13;
            const string text = "Open a video to see its timeline";
            var width = paint.MeasureText(text);
            canvas.DrawText(text, (w - width) / 2, h / 2 + 4, paint);
        }

        private void DrawSegments(SKCanvas canvas, SKPaint paint, float w, float top, float bottom)
        {
            paint.TextSize = 11;
            for (var i = 0; i < _s.Segments.Length; i++)
            {
                var seg = _s.Segments[i];
                if (seg.End < _s.ViewStart || seg.Start > _s.ViewEnd)
                {
                    continue;
                }

                var x0 = Math.Max(0, TimeToX(seg.Start));
                var x1 = Math.Min(w, TimeToX(seg.End));
                var hover = i == _s.HoverSegment;
                paint.Color = seg.Enabled
                    ? (hover ? KeptBackgroundHover : KeptBackground)
                    : (hover ? RemovedBackgroundHover : RemovedBackground);
                paint.IsAntialias = false;
                canvas.DrawRect(x0, top, x1 - x0, bottom - top, paint);

                if (!seg.Enabled && seg.Reason is { Length: > 0 } reason && x1 - x0 > 70)
                {
                    paint.Color = ReasonText;
                    paint.IsAntialias = true;
                    canvas.Save();
                    canvas.ClipRect(new SKRect(x0 + 2, top, x1 - 2, bottom));
                    canvas.DrawText(reason, x0 + 6, top + 14, paint);
                    canvas.Restore();
                }
            }
        }

        private void DrawWaveform(SKCanvas canvas, SKPaint paint, float w, float top, float bottom)
        {
            var waveform = _s.Waveform;
            if (waveform is null)
            {
                return;
            }

            var secondsPerPixel = (_s.ViewEnd - _s.ViewStart) / _s.Size.Width;
            var level = waveform.LevelFor(secondsPerPixel);
            var peaks = level.Peaks;
            var spb = level.SecondsPerBucket;
            var mid = top + (bottom - top) / 2;
            var halfHeight = (bottom - top) / 2 - 4;
            const float scale = 1f / 32768f;

            paint.IsAntialias = false;
            paint.StrokeWidth = 1;

            // One vertical line per pixel column, colour by the segment under that column.
            var segIndex = 0;
            for (var px = 0; px < (int)w; px++)
            {
                var t0 = _s.ViewStart + px * secondsPerPixel;
                var t1 = t0 + secondsPerPixel;
                var b0 = (int)(t0 / spb);
                var b1 = Math.Max(b0, (int)(t1 / spb) - 1);
                if (b0 >= peaks.Count || b0 < 0)
                {
                    continue;
                }

                b1 = Math.Min(b1, peaks.Count - 1);
                var min = short.MaxValue;
                var max = short.MinValue;
                for (var b = b0; b <= b1; b++)
                {
                    var p = peaks[b];
                    if (p.Min < min)
                    {
                        min = p.Min;
                    }

                    if (p.Max > max)
                    {
                        max = p.Max;
                    }
                }

                while (segIndex < _s.Segments.Length - 1 && _s.Segments[segIndex].End <= t0)
                {
                    segIndex++;
                }

                var enabled = _s.Segments.Length == 0 || _s.Segments[segIndex].Enabled;
                paint.Color = enabled ? KeptWave : RemovedWave;

                var yMax = mid - Math.Max(1, max * scale * halfHeight);
                var yMin = mid - Math.Min(-1, min * scale * halfHeight);
                canvas.DrawLine(px + 0.5f, yMax, px + 0.5f, yMin, paint);
            }
        }

        private void DrawBoundaries(SKCanvas canvas, SKPaint paint, float top, float bottom)
        {
            paint.IsAntialias = false;
            for (var i = 1; i < _s.Segments.Length; i++)
            {
                var t = _s.Segments[i].Start;
                if (t < _s.ViewStart || t > _s.ViewEnd)
                {
                    continue;
                }

                var x = (float)Math.Round(TimeToX(t)) + 0.5f;
                var active = i == _s.ActiveBoundary;
                paint.Color = active ? BoundaryActive : Boundary;
                paint.StrokeWidth = active ? 2 : 1;
                canvas.DrawLine(x, top, x, bottom, paint);
            }
        }

        private void DrawRuler(SKCanvas canvas, SKPaint paint, float w, float bodyTop)
        {
            var span = _s.ViewEnd - _s.ViewStart;
            var pxPerSecond = _s.Size.Width / span;
            var interval = NiceIntervals.FirstOrDefault(i => i * pxPerSecond >= 80, NiceIntervals[^1]);
            var minor = interval / (interval >= 60 ? 4 : 5);

            paint.TextSize = 10;
            paint.StrokeWidth = 1;

            // Integer tick indices, not accumulated doubles: 0.1 + 0.1 + ... drifts enough to
            // mislabel seconds after a few dozen ticks.
            var perMajor = (int)Math.Round(interval / minor);
            var firstTick = (long)Math.Floor(_s.ViewStart / minor);
            var lastTick = (long)Math.Ceiling(_s.ViewEnd / minor);
            for (var k = firstTick; k <= lastTick; k++)
            {
                var t = k * minor;
                var x = (float)Math.Round(TimeToX(t)) + 0.5f;
                var isMajor = k % perMajor == 0;
                paint.Color = Tick;
                paint.IsAntialias = false;
                canvas.DrawLine(x, isMajor ? bodyTop - 10 : bodyTop - 5, x, bodyTop, paint);
                if (isMajor)
                {
                    paint.Color = Label;
                    paint.IsAntialias = true;
                    canvas.DrawText(TimeFormat.Ruler(Math.Round(t, 6)), x + 3, bodyTop - 11, paint);
                }
            }

            paint.Color = Tick;
            paint.IsAntialias = false;
            canvas.DrawLine(0, bodyTop - 0.5f, w, bodyTop - 0.5f, paint);
        }

        private void DrawPlayhead(SKCanvas canvas, SKPaint paint, float h)
        {
            if (_s.Position < _s.ViewStart || _s.Position > _s.ViewEnd)
            {
                return;
            }

            var x = (float)Math.Round(TimeToX(_s.Position)) + 0.5f;
            paint.Color = Playhead;
            paint.IsAntialias = true;
            paint.StrokeWidth = 1.5f;
            canvas.DrawLine(x, 0, x, h, paint);

            using var head = new SKPath();
            head.MoveTo(x - 6, 0);
            head.LineTo(x + 6, 0);
            head.LineTo(x, 8);
            head.Close();
            paint.Style = SKPaintStyle.Fill;
            canvas.DrawPath(head, paint);
        }
    }
}
