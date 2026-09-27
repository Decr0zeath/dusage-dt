using System.Windows;
using System.Windows.Media;

namespace Dusage;

/// <summary>
/// The backdrop of the Settings window: two faint nebulae and a scatter of stars, a few of them bright and four-pointed.
/// The stars are laid out once over a fixed area, so they stay put when the window changes height.
/// It is its own element so the sky can be animated without touching the content in front of it.
/// </summary>
public sealed class Sky : FrameworkElement
{
    const double Area = 1600;
    const int StarsPer10000Px = 3;

    static readonly Brush VioletGlow = Glow(Palette.Nebula, 0x30, new Point(0.95, 0), 0.9, 0.45);
    static readonly Brush PinkGlow = Glow(Palette.Nova, 0x18, new Point(0, 1), 0.8, 0.4);

    /// <summary>Faint to bright, in eight steps.</summary>
    static readonly Brush[] Shades = Enumerable.Range(0, 8).Select(i => (Brush)Palette.Brush(Palette.Starlight, (byte)(40 + i * 25))).ToArray();

    static readonly (Point At, double Brightness, double Size)[] Stars = Scatter();

    protected override void OnRender(DrawingContext dc)
    {
        var bounds = new Rect(RenderSize);
        dc.DrawRectangle(VioletGlow, null, bounds);
        dc.DrawRectangle(PinkGlow, null, bounds);

        foreach (var (at, brightness, size) in Stars)
        {
            if (!bounds.Contains(at)) continue;
            var shade = Shades[(int)(brightness * (Shades.Length - 1))];
            if (size > 2) dc.DrawGeometry(shade, null, Sparkle(at, size));
            else dc.DrawEllipse(shade, null, at, size / 2, size / 2);
        }
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        InvalidateVisual();
    }

    static (Point, double, double)[] Scatter()
    {
        var random = new Random(27); // the same sky every time
        var count = (int)(Area * Area / 10_000 * StarsPer10000Px);
        return Enumerable.Range(0, count).Select(_ =>
        {
            var at = new Point(random.NextDouble() * Area, random.NextDouble() * Area);
            var brightness = Math.Pow(random.NextDouble(), 2); // mostly faint
            // One star in thirty is a bright four-pointed one; the rest are specks.
            var size = random.NextDouble() < 1 / 30.0 ? 3 + random.NextDouble() * 2.5 : 0.8 + brightness * 1.2;
            return (at, size > 2 ? 0.6 + brightness * 0.4 : brightness, size);
        }).ToArray();
    }

    static Geometry Sparkle(Point at, double radius)
    {
        var pinch = radius * 0.14;
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(new Point(at.X, at.Y - radius), true, true);
            g.QuadraticBezierTo(new Point(at.X + pinch, at.Y - pinch), new Point(at.X + radius, at.Y), true, true);
            g.QuadraticBezierTo(new Point(at.X + pinch, at.Y + pinch), new Point(at.X, at.Y + radius), true, true);
            g.QuadraticBezierTo(new Point(at.X - pinch, at.Y + pinch), new Point(at.X - radius, at.Y), true, true);
            g.QuadraticBezierTo(new Point(at.X - pinch, at.Y - pinch), new Point(at.X, at.Y - radius), true, true);
        }
        geometry.Freeze();
        return geometry;
    }

    static Brush Glow(Color color, byte alpha, Point center, double radiusX, double radiusY)
    {
        var brush = new RadialGradientBrush(
            [
                new GradientStop(Color.FromArgb(alpha, color.R, color.G, color.B), 0),
                new GradientStop(Color.FromArgb(0, color.R, color.G, color.B), 1),
            ])
        {
            Center = center,
            GradientOrigin = center,
            RadiusX = radiusX,
            RadiusY = radiusY,
        };
        brush.Freeze();
        return brush;
    }
}
