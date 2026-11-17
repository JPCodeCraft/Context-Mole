using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace ContextMole.App.UI.Controls;

/// <summary>
/// One bar split into up to date, indexing, waiting and needs-attention segments. It has a fixed
/// height and only repaints when a count changes, so polling never moves the surrounding layout.
/// </summary>
public sealed class SegmentBar : Control
{
    public static readonly StyledProperty<int> ReadyProperty = AvaloniaProperty.Register<SegmentBar, int>(nameof(Ready));
    public static readonly StyledProperty<int> ProcessingProperty = AvaloniaProperty.Register<SegmentBar, int>(nameof(Processing));
    public static readonly StyledProperty<int> QueuedProperty = AvaloniaProperty.Register<SegmentBar, int>(nameof(Queued));
    public static readonly StyledProperty<int> AttentionProperty = AvaloniaProperty.Register<SegmentBar, int>(nameof(Attention));
    public static readonly StyledProperty<IBrush?> TrackBrushProperty = AvaloniaProperty.Register<SegmentBar, IBrush?>(nameof(TrackBrush));
    public static readonly StyledProperty<IBrush?> ReadyBrushProperty = AvaloniaProperty.Register<SegmentBar, IBrush?>(nameof(ReadyBrush));
    public static readonly StyledProperty<IBrush?> ProcessingBrushProperty = AvaloniaProperty.Register<SegmentBar, IBrush?>(nameof(ProcessingBrush));
    public static readonly StyledProperty<IBrush?> QueuedBrushProperty = AvaloniaProperty.Register<SegmentBar, IBrush?>(nameof(QueuedBrush));
    public static readonly StyledProperty<IBrush?> AttentionBrushProperty = AvaloniaProperty.Register<SegmentBar, IBrush?>(nameof(AttentionBrush));

    static SegmentBar()
    {
        AffectsRender<SegmentBar>(ReadyProperty, ProcessingProperty, QueuedProperty, AttentionProperty,
            TrackBrushProperty, ReadyBrushProperty, ProcessingBrushProperty, QueuedBrushProperty, AttentionBrushProperty);
    }

    public int Ready { get => GetValue(ReadyProperty); set => SetValue(ReadyProperty, value); }
    public int Processing { get => GetValue(ProcessingProperty); set => SetValue(ProcessingProperty, value); }
    public int Queued { get => GetValue(QueuedProperty); set => SetValue(QueuedProperty, value); }
    public int Attention { get => GetValue(AttentionProperty); set => SetValue(AttentionProperty, value); }
    public IBrush? TrackBrush { get => GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }
    public IBrush? ReadyBrush { get => GetValue(ReadyBrushProperty); set => SetValue(ReadyBrushProperty, value); }
    public IBrush? ProcessingBrush { get => GetValue(ProcessingBrushProperty); set => SetValue(ProcessingBrushProperty, value); }
    public IBrush? QueuedBrush { get => GetValue(QueuedBrushProperty); set => SetValue(QueuedBrushProperty, value); }
    public IBrush? AttentionBrush { get => GetValue(AttentionBrushProperty); set => SetValue(AttentionBrushProperty, value); }

    protected override Size MeasureOverride(Size availableSize) => new(0, double.IsNaN(Height) ? 8 : Height);

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        if (bounds.Width <= 0 || bounds.Height <= 0) return;
        var radius = bounds.Height / 2;
        using var clip = context.PushClip(new RoundedRect(bounds, radius));
        if (TrackBrush is { } track) context.FillRectangle(track, bounds);

        var segments = new (int Count, IBrush? Brush)[]
        {
            (Ready, ReadyBrush), (Processing, ProcessingBrush), (Queued, QueuedBrush), (Attention, AttentionBrush)
        };
        var total = segments.Sum(segment => Math.Max(0, segment.Count));
        if (total == 0) return;

        // A non-empty segment stays visible even when it is a tiny share of a large project.
        var minimum = Math.Min(4, bounds.Width / segments.Length);
        var visible = segments.Count(segment => segment.Count > 0);
        var flexible = Math.Max(0, bounds.Width - minimum * visible);
        var x = 0d;
        foreach (var (count, brush) in segments)
        {
            if (count <= 0) continue;
            var width = minimum + flexible * count / total;
            if (brush is not null) context.FillRectangle(brush, new Rect(x, 0, width, bounds.Height));
            x += width;
        }
    }
}
