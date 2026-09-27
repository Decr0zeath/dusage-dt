using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Dusage;

/// <summary>
/// Service logos (path data from Simple Icons, CC0; the marks themselves belong to Anthropic and OpenAI)
/// and dUsage/dt's own mark.
/// </summary>
static class Logos
{
    const string ClaudePath = "m4.7144 15.9555 4.7174-2.6471.079-.2307-.079-.1275h-.2307l-.7893-.0486-2.6956-.0729-2.3375-.0971-2.2646-.1214-.5707-.1215-.5343-.7042.0546-.3522.4797-.3218.686.0608 1.5179.1032 2.2767.1578 1.6514.0972 2.4468.255h.3886l.0546-.1579-.1336-.0971-.1032-.0972L6.973 9.8356l-2.55-1.6879-1.3356-.9714-.7225-.4918-.3643-.4614-.1578-1.0078.6557-.7225.8803.0607.2246.0607.8925.686 1.9064 1.4754 2.4893 1.8336.3643.3035.1457-.1032.0182-.0728-.164-.2733-1.3539-2.4467-1.445-2.4893-.6435-1.032-.17-.6194c-.0607-.255-.1032-.4674-.1032-.7285L6.287.1335 6.6997 0l.9957.1336.419.3642.6192 1.4147 1.0018 2.2282 1.5543 3.0296.4553.8985.2429.8318.091.255h.1579v-.1457l.1275-1.706.2368-2.0947.2307-2.6957.0789-.7589.3764-.9107.7468-.4918.5828.2793.4797.686-.0668.4433-.2853 1.8517-.5586 2.9021-.3643 1.9429h.2125l.2429-.2429.9835-1.3053 1.6514-2.0643.7286-.8196.85-.9046.5464-.4311h1.0321l.759 1.1293-.34 1.1657-1.0625 1.3478-.8804 1.1414-1.2628 1.7-.7893 1.36.0729.1093.1882-.0183 2.8535-.607 1.5421-.2794 1.8396-.3157.8318.3886.091.3946-.3278.8075-1.967.4857-2.3072.4614-3.4364.8136-.0425.0304.0486.0607 1.5482.1457.6618.0364h1.621l3.0175.2247.7892.522.4736.6376-.079.4857-1.2142.6193-1.6393-.3886-3.825-.9107-1.3113-.3279h-.1822v.1093l1.0929 1.0686 2.0035 1.8092 2.5075 2.3314.1275.5768-.3218.4554-.34-.0486-2.2039-1.6575-.85-.7468-1.9246-1.621h-.1275v.17l.4432.6496 2.3436 3.5214.1214 1.0807-.17.3521-.6071.2125-.6679-.1214-1.3721-1.9246L14.38 17.959l-1.1414-1.9428-.1397.079-.674 7.2552-.3156.3703-.7286.2793-.6071-.4614-.3218-.7468.3218-1.4753.3886-1.9246.3157-1.53.2853-1.9004.17-.6314-.0121-.0425-.1397.0182-1.4328 1.9672-2.1796 2.9446-1.7243 1.8456-.4128.164-.7164-.3704.0667-.6618.4008-.5889 2.386-3.0357 1.4389-1.882.929-1.0868-.0062-.1579h-.0546l-6.3385 4.1164-1.1293.1457-.4857-.4554.0608-.7467.2307-.2429 1.9064-1.3114Z";

    const string OpenAIPath = "M22.2819 9.8211a5.9847 5.9847 0 0 0-.5157-4.9108 6.0462 6.0462 0 0 0-6.5098-2.9A6.0651 6.0651 0 0 0 4.9807 4.1818a5.9847 5.9847 0 0 0-3.9977 2.9 6.0462 6.0462 0 0 0 .7427 7.0966 5.98 5.98 0 0 0 .511 4.9107 6.051 6.051 0 0 0 6.5146 2.9001A5.9847 5.9847 0 0 0 13.2599 24a6.0557 6.0557 0 0 0 5.7718-4.2058 5.9894 5.9894 0 0 0 3.9977-2.9001 6.0557 6.0557 0 0 0-.7475-7.0729zm-9.022 12.6081a4.4755 4.4755 0 0 1-2.8764-1.0408l.1419-.0804 4.7783-2.7582a.7948.7948 0 0 0 .3927-.6813v-6.7369l2.02 1.1686a.071.071 0 0 1 .038.052v5.5826a4.504 4.504 0 0 1-4.4945 4.4944zm-9.6607-4.1254a4.4708 4.4708 0 0 1-.5346-3.0137l.142.0852 4.783 2.7582a.7712.7712 0 0 0 .7806 0l5.8428-3.3685v2.3324a.0804.0804 0 0 1-.0332.0615L9.74 19.9502a4.4992 4.4992 0 0 1-6.1408-1.6464zM2.3408 7.8956a4.485 4.485 0 0 1 2.3655-1.9728V11.6a.7664.7664 0 0 0 .3879.6765l5.8144 3.3543-2.0201 1.1685a.0757.0757 0 0 1-.071 0l-4.8303-2.7865A4.504 4.504 0 0 1 2.3408 7.872zm16.5963 3.8558L13.1038 8.364 15.1192 7.2a.0757.0757 0 0 1 .071 0l4.8303 2.7913a4.4944 4.4944 0 0 1-.6765 8.1042v-5.6772a.79.79 0 0 0-.407-.667zm2.0107-3.0231l-.142-.0852-4.7735-2.7818a.7759.7759 0 0 0-.7854 0L9.409 9.2297V6.8974a.0662.0662 0 0 1 .0284-.0615l4.8303-2.7866a4.4992 4.4992 0 0 1 6.6802 4.66zM8.3065 12.863l-2.02-1.1638a.0804.0804 0 0 1-.038-.0567V6.0742a4.4992 4.4992 0 0 1 7.3757-3.4537l-.142.0805L8.704 5.459a.7948.7948 0 0 0-.3927.6813zm1.0976-2.3654l2.602-1.4998 2.6069 1.4998v2.9994l-2.5974 1.4997-2.6067-1.4997Z";

    // dUsage/dt's mark: an hourglass split by an integral sign, traced from the artwork; 10 units wide, 13.83 tall.
    const string NebulaHalf = "M0.37,-0.01 0.35,0.02 0.34,0.68 0.37,1.54 0.46,1.99 0.62,2.43 0.93,2.97 1.35,3.55 1.87,4.14 2.81,5.14 3.11,5.51 3.35,5.89 3.47,6.12 3.56,6.38 3.63,6.71 3.63,7.22 3.56,7.59 3.4,7.99 3.17,8.34 2.91,8.62 2.66,8.84 1.47,9.65 1.19,9.88 1.02,10.05 0.9,10.21 0.79,10.42 0.72,10.63 0.7,10.93 0.74,11.21 0.88,11.52 1.06,11.75 1.24,11.9 1.54,12.04 1.82,12.1 2.2,12.08 2.45,12.02 2.71,11.9 2.9,11.78 3.15,11.55 3.39,11.29 3.56,11.05 3.74,10.72 3.88,10.4 4.07,9.79 4.19,9.25 4.81,5.77 5.18,4.02 5.51,3.04 5.67,2.69 6,2.13 6.34,1.71 6.8,1.33 7.06,1.19 7.24,1.12 7.43,1.07 7.66,1.05 7.85,1.07 7.97,1.17 8,1.33 7.91,1.64 7.91,1.75 7.96,1.89 8.06,1.99 8.25,2.06 8.5,2.07 8.86,2.06 9.09,2.01 9.32,1.9 9.52,1.68 9.59,1.47 9.59,1.14 9.54,0.96 9.45,0.75 9.33,0.58 9.11,0.37 8.95,0.25 8.71,0.13 8.43,0.04 8.18,-0Z";

    const string StarlightHalf = "M7.62,2.45 7.41,2.49 7.24,2.56 6.96,2.77 6.79,2.97 6.67,3.15 6.49,3.62 6.35,4.21 5.82,7.69 5.42,9.77 5.29,10.26 5.06,10.86 4.85,11.31 4.63,11.68 4.18,12.24 3.83,12.57 3.53,12.78 3.04,13.04 2.76,13.14 2.43,13.2 2.15,13.21 1.89,13.19 1.66,13.08 1.53,12.94 1.43,12.64 1.36,12.5 1.24,12.36 1.07,12.26 0.93,12.21 0.75,12.19 0.56,12.21 0.38,12.27 0.21,12.37 0.07,12.52 -0.01,12.69 -0.03,12.92 0.02,13.13 0.13,13.34 0.28,13.5 0.44,13.63 0.77,13.79 1.21,13.87 10,13.86 10.01,12.59 9.96,12.13 9.87,11.71 9.68,11.21 9.47,10.84 9.03,10.26 8.7,9.91 7.85,9.12 7.38,8.62 7.05,8.11 6.91,7.69 6.87,7.29 6.89,6.96 6.96,6.66 7.14,6.24 7.4,5.86 7.66,5.58 8.39,4.94 8.77,4.56 8.96,4.25 9.03,3.97 9.05,3.69 8.98,3.36 8.82,3.06 8.69,2.9 8.39,2.66 8.11,2.51 7.83,2.45Z";

    static readonly Color ClaudeColor = Palette.Giant;
    static readonly Color OpenAIColor = Palette.Starlight;

    static readonly DrawingImage Claude = Mark(ClaudePath, ClaudeColor);
    static readonly DrawingImage OpenAI = Mark(OpenAIPath, OpenAIColor);

    public static readonly DrawingImage App = CreateAppMark();

    public static DrawingImage For(string providerKey) => providerKey == "claude" ? Claude : OpenAI;

    public static Color ColorFor(string providerKey) => providerKey == "claude" ? ClaudeColor : OpenAIColor;

    public static Image View(ImageSource source, double size) => new()
    {
        Source = source,
        Width = size,
        Height = size,
        Stretch = Stretch.Uniform,
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>Rasterizes a mark, e.g. for the .ico file.</summary>
    public static BitmapSource Render(ImageSource source, int pixels)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
            dc.DrawImage(source, new Rect(0, 0, pixels, pixels));
        var bitmap = new RenderTargetBitmap(pixels, pixels, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    static DrawingImage Mark(string path, Color color)
    {
        var group = new DrawingGroup();
        // An invisible 24×24 frame keeps every logo on the same grid as its SVG viewBox.
        group.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, 24, 24))));
        group.Children.Add(new GeometryDrawing(new SolidColorBrush(color), null, Geometry.Parse(path)));
        return Frozen(group);
    }

    /// <summary>
    /// The hourglass in deep space: sand running out, and ∫ usage dt. Time and the integral, as the name has it.
    /// The top half is the nebula, the bottom half starlight. On a 16-unit grid, the size it's most often seen at.
    /// </summary>
    static DrawingImage CreateAppMark()
    {
        var hourglass = new DrawingGroup
        {
            // 13.83 units tall scaled to 12, centered on the tile.
            Transform = new MatrixTransform(12 / 13.83, 0, 0, 12 / 13.83, (16 - 10 * 12 / 13.83) / 2, 2),
        };
        hourglass.Children.Add(new GeometryDrawing(Palette.NebulaGradient, null, Geometry.Parse(NebulaHalf)));
        hourglass.Children.Add(new GeometryDrawing(Palette.Brush(Palette.Starlight), null, Geometry.Parse(StarlightHalf)));

        var space = new RadialGradientBrush(Palette.Twilight, Palette.Void) { RadiusX = 0.7, RadiusY = 0.7 };
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(space, null, new RectangleGeometry(new Rect(0, 0, 16, 16), 3.5, 3.5)));
        group.Children.Add(hourglass);
        return Frozen(group);
    }

    static DrawingImage Frozen(Drawing drawing)
    {
        var image = new DrawingImage(drawing);
        image.Freeze();
        return image;
    }
}
