using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace WinFinger.Controls;

/// <summary>
/// Circular memory gauge (mac MemoryRing): hairline track, coloured arc (teal / warning ≥65% / danger ≥85%),
/// bold monospaced percentage in the centre. Size follows the control's width/height.
/// </summary>
public sealed class MemoryRing : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(MemoryRing),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender, OnValueChanged));

    private static readonly DependencyProperty AnimatedValueProperty = DependencyProperty.Register(
        nameof(AnimatedValue), typeof(double), typeof(MemoryRing),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public MemoryRing()
    {
        ToolTip = "已用内存（不含可回收缓存），每秒更新";
        AutomationProperties.SetName(this, "内存占用");
        SetResourceReference(TrackBrushProperty, "Brush.Hairline");
        SetResourceReference(TealBrushProperty, "Brush.Teal");
        SetResourceReference(WarningBrushProperty, "Brush.Warning");
        SetResourceReference(DangerBrushProperty, "Brush.Danger");
        SetResourceReference(TextBrushProperty, "Brush.TextPrimary");
    }

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    private double AnimatedValue => (double)GetValue(AnimatedValueProperty);

    public static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(
        nameof(TrackBrush), typeof(Brush), typeof(MemoryRing),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty TealBrushProperty = DependencyProperty.Register(
        nameof(TealBrush), typeof(Brush), typeof(MemoryRing),
        new FrameworkPropertyMetadata(Brushes.Teal, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty WarningBrushProperty = DependencyProperty.Register(
        nameof(WarningBrush), typeof(Brush), typeof(MemoryRing),
        new FrameworkPropertyMetadata(Brushes.Orange, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty DangerBrushProperty = DependencyProperty.Register(
        nameof(DangerBrush), typeof(Brush), typeof(MemoryRing),
        new FrameworkPropertyMetadata(Brushes.Red, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty TextBrushProperty = DependencyProperty.Register(
        nameof(TextBrush), typeof(Brush), typeof(MemoryRing),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush TrackBrush { get => (Brush)GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }
    public Brush TealBrush { get => (Brush)GetValue(TealBrushProperty); set => SetValue(TealBrushProperty, value); }
    public Brush WarningBrush { get => (Brush)GetValue(WarningBrushProperty); set => SetValue(WarningBrushProperty, value); }
    public Brush DangerBrush { get => (Brush)GetValue(DangerBrushProperty); set => SetValue(DangerBrushProperty, value); }
    public Brush TextBrush { get => (Brush)GetValue(TextBrushProperty); set => SetValue(TextBrushProperty, value); }

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var ring = (MemoryRing)d;
        double target = Math.Clamp((double)e.NewValue, 0, 1);
        AutomationProperties.SetName(ring, $"内存占用 {(int)Math.Round(target * 100)}%");
        var anim = new DoubleAnimation(target, TimeSpan.FromMilliseconds(200))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        ring.BeginAnimation(AnimatedValueProperty, anim);
    }

    private Brush ArcBrush(double value)
    {
        if (value >= 0.85) return DangerBrush;
        if (value >= 0.65) return WarningBrush;
        return TealBrush;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double side = Math.Min(ActualWidth, ActualHeight);
        if (side <= 0) return;
        double lineWidth = Math.Max(side * 0.11, 2);
        double radius = (side - lineWidth) / 2;
        var center = new Point(ActualWidth / 2, ActualHeight / 2);

        // hit-test surface (transparent) so clicks register anywhere in the ring
        dc.DrawEllipse(Brushes.Transparent, null, center, side / 2, side / 2);

        dc.DrawEllipse(null, new Pen(TrackBrush, lineWidth), center, radius, radius);

        double value = Math.Clamp(AnimatedValue, 0, 1);
        double shown = Math.Clamp(Value, 0, 1);
        if (value > 0.001)
        {
            var pen = new Pen(ArcBrush(shown), lineWidth) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            double sweep = value * 360;
            if (value >= 0.999)
            {
                dc.DrawEllipse(null, pen, center, radius, radius);
            }
            else
            {
                double startAngle = -90;
                double endAngle = startAngle + sweep;
                var start = PointOn(center, radius, startAngle);
                var end = PointOn(center, radius, endAngle);
                var geometry = new StreamGeometry();
                using (var ctx = geometry.Open())
                {
                    ctx.BeginFigure(start, false, false);
                    ctx.ArcTo(end, new Size(radius, radius), 0, sweep > 180, SweepDirection.Clockwise, true, false);
                }
                geometry.Freeze();
                dc.DrawGeometry(null, pen, geometry);
            }
        }

        var text = new FormattedText(
            $"{(int)Math.Round(shown * 100)}%",
            System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Cascadia Code, Consolas"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
            Math.Max(side * 0.24, 6),
            TextBrush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(text, new Point(center.X - text.Width / 2, center.Y - text.Height / 2));
    }

    private static Point PointOn(Point center, double radius, double degrees)
    {
        double rad = degrees * Math.PI / 180;
        return new Point(center.X + radius * Math.Cos(rad), center.Y + radius * Math.Sin(rad));
    }
}
