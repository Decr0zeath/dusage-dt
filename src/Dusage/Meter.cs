using System.Windows;
using System.Windows.Media;

namespace Dusage;

/// <summary>A usage bar: how much of the limit is used, plus a tick for how much of the window's time has passed.</summary>
public sealed class Meter : FrameworkElement
{
    static readonly Brush Track = Frozen(Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF));
    static readonly Brush Tick = Frozen(Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF));

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(Meter), new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty PaceProperty = DependencyProperty.Register(
        nameof(Pace), typeof(double), typeof(Meter), new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(Brush), typeof(Meter), new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Used share of the limit, 0..1. NaN when unknown.</summary>
    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }

    /// <summary>Elapsed share of the window's time, 0..1. NaN hides the tick.</summary>
    public double Pace { get => (double)GetValue(PaceProperty); set => SetValue(PaceProperty, value); }

    public Brush Fill { get => (Brush)GetValue(FillProperty); set => SetValue(FillProperty, value); }

    /// <summary>Thickness of the bar itself; the tick runs the full height of the element.</summary>
    public double BarHeight { get; init; } = 4;

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight, y = Math.Round((h - BarHeight) / 2), r = BarHeight / 2;
        dc.DrawRoundedRectangle(Track, null, new Rect(0, y, w, BarHeight), r, r);

        if (Value > 0) // NaN compares false
        {
            var width = Math.Max(3, w * Math.Min(Value, 1));
            var radius = Math.Min(r, width / 2);
            dc.DrawRoundedRectangle(Fill, null, new Rect(0, y, width, BarHeight), radius, radius);
        }

        if (Pace >= 0) // NaN compares false
        {
            var x = Math.Min(Math.Round(w * Math.Min(Pace, 1)), w - 1);
            dc.DrawRectangle(Tick, null, new Rect(x, 0, 1, h));
        }
    }

    static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
