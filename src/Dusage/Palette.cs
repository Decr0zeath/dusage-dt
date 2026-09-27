using System.Windows;
using System.Windows.Media;

namespace Dusage;

/// <summary>
/// Every color dUsage/dt draws with: a universe. Deep-space greys, starlight text, and a nebula (violet into pink)
/// for the accent. XAML reaches these through x:Static.
/// </summary>
public static class Palette
{
    public static readonly Color
        Void = Rgb(0x0A0A12),       // window, behind the stars
        Deep = Rgb(0x12121F),       // cards, menus, the widget
        Orbit = Rgb(0x262640),      // hairlines
        Eclipse = Rgb(0x1C1C30),    // controls
        Twilight = Rgb(0x2A2A46),   // controls under the mouse; switches that are off
        Dusk = Rgb(0x36365A),       // controls being pressed; off switches under the mouse
        Starlight = Rgb(0xEEEEF8),  // text, stars
        Stardust = Rgb(0x9C9BBE),   // secondary text
        Comet = Rgb(0x6C6A8E),      // placeholders
        Nebula = Rgb(0xA58BFA),     // the accent
        Nova = Rgb(0xE879C9),       // where the nebula turns pink
        Giant = Rgb(0xD97757),      // Claude's own clay: a red giant
        Rigel = Rgb(0x4796E3),      // Gemini's blue: a blue supergiant
        Amber = Rgb(0xF2B84B),      // a limit 75% used
        Flare = Rgb(0xF2605C);      // a limit 90% used

    /// <summary>The nebula as a gradient, top-left violet to bottom-right pink.</summary>
    public static readonly LinearGradientBrush NebulaGradient = Gradient(Nebula, Nova);

    /// <summary>A frozen brush of a palette color, optionally see-through.</summary>
    public static SolidColorBrush Brush(Color color, byte alpha = 0xFF)
    {
        var brush = new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B));
        brush.Freeze();
        return brush;
    }

    static LinearGradientBrush Gradient(Color from, Color to)
    {
        var brush = new LinearGradientBrush(from, to, new Point(0, 0), new Point(1, 1));
        brush.Freeze();
        return brush;
    }

    static Color Rgb(int rgb) => Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
}
