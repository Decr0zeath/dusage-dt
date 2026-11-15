using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace Dusage;

/// <summary>
/// The tooltip of a service in the taskbar: the widget's tooltip, opened by resting the mouse on it. The taskbar
/// isn't the app's to put a WPF tooltip in, so this is a small window of its own, in the widget tooltip's colors,
/// that never takes the focus and lets the mouse through.
/// </summary>
sealed class TrayTip : Window
{
    readonly Border _box;

    public TrayTip()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        TextOptions.SetTextRenderingMode(this, TextRenderingMode.Grayscale);
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 12;
        Foreground = Palette.Brush(Palette.Starlight);
        _box = new Border
        {
            Background = Palette.Brush(Palette.Deep, 0xF2),
            BorderBrush = Palette.Brush(Colors.White, 0x33),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 8, 10, 8),
        };
        Content = _box;
        Left = Top = -20000; // drawn off screen first, then placed by the taskbar in pixels
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        Native.MakeHoverOnly(new WindowInteropHelper(this).Handle);
    }

    /// <summary>Shows <paramref name="content"/> next to a spot on the taskbar, on the screen side of it.</summary>
    public void ShowAt(UIElement content, Native.PixelRect spot)
    {
        _box.Child = content;
        if (!IsVisible) Show();
        UpdateLayout();
        Native.PlaceNear(new WindowInteropHelper(this).Handle, spot);
    }
}
