using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Kairn.Services;

namespace Kairn.Views;

/// <summary>Sélecteur de couleur complet : teinte, saturation, luminosité, transparence et code hexadécimal.</summary>
public partial class ColorPicker : UserControl
{
    private double _h, _s, _v, _a = 1;
    private bool _silent;

    /// <summary>Levé à chaque changement (en direct pendant le glisser).</summary>
    public event Action<Color>? ColorChanged;

    public ColorPicker() => InitializeComponent();

    public Color Color
    {
        get => FromHsv(_h, _s, _v, _a);
        set
        {
            ToHsv(value, out _h, out _s, out _v);
            _a = value.A / 255.0;
            _silent = true;
            UpdateVisuals();
            _silent = false;
        }
    }

    private void Changed()
    {
        UpdateVisuals();
        if (!_silent) ColorChanged?.Invoke(Color);
    }

    private void UpdateVisuals()
    {
        var c = Color;
        HueFill.Fill = new SolidColorBrush(FromHsv(_h, 1, 1, 1));
        Canvas.SetLeft(SvThumb, _s * SvArea.Width - 7);
        Canvas.SetTop(SvThumb, (1 - _v) * SvArea.Height - 7);
        Canvas.SetLeft(HueThumb, _h / 360 * (SvArea.Width - 16));
        Canvas.SetLeft(AlphaThumb, _a * (SvArea.Width - 16));
        var opaque = Color.FromRgb(c.R, c.G, c.B);
        AlphaFill.Background = new LinearGradientBrush(Color.FromArgb(0, c.R, c.G, c.B), opaque, 0);
        Preview.Background = new SolidColorBrush(c);
        if (!HexBox.IsKeyboardFocused) HexBox.Text = ThemeService.ToHex(c);
    }

    // ---- Souris ----

    private void Sv_Down(object s, MouseButtonEventArgs e) { SvArea.CaptureMouse(); SetSv(e.GetPosition(SvArea)); }
    private void Sv_Move(object s, MouseEventArgs e) { if (SvArea.IsMouseCaptured) SetSv(e.GetPosition(SvArea)); }
    private void Hue_Down(object s, MouseButtonEventArgs e) { HueArea.CaptureMouse(); SetHue(e.GetPosition(HueArea)); }
    private void Hue_Move(object s, MouseEventArgs e) { if (HueArea.IsMouseCaptured) SetHue(e.GetPosition(HueArea)); }
    private void Alpha_Down(object s, MouseButtonEventArgs e) { AlphaArea.CaptureMouse(); SetAlpha(e.GetPosition(AlphaArea)); }
    private void Alpha_Move(object s, MouseEventArgs e) { if (AlphaArea.IsMouseCaptured) SetAlpha(e.GetPosition(AlphaArea)); }
    private void Any_Up(object s, MouseButtonEventArgs e) => ((UIElement)s).ReleaseMouseCapture();

    private void SetSv(Point p)
    {
        _s = Math.Clamp(p.X / SvArea.Width, 0, 1);
        _v = 1 - Math.Clamp(p.Y / SvArea.Height, 0, 1);
        Changed();
    }

    private void SetHue(Point p)
    {
        _h = Math.Clamp(p.X / HueArea.ActualWidth, 0, 1) * 360;
        Changed();
    }

    private void SetAlpha(Point p)
    {
        _a = Math.Clamp(p.X / AlphaArea.ActualWidth, 0, 1);
        Changed();
    }

    // ---- Saisie hexadécimale ----

    private void Hex_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Hex_LostFocus(sender, e);
    }

    private void Hex_LostFocus(object sender, RoutedEventArgs e)
    {
        var hex = HexBox.Text.Trim();
        if (!hex.StartsWith('#')) hex = "#" + hex;
        if (System.Text.RegularExpressions.Regex.IsMatch(hex, "^#([0-9a-fA-F]{6}|[0-9a-fA-F]{8})$"))
        {
            var c = ThemeService.Parse(hex, "#000000");
            ToHsv(c, out _h, out _s, out _v);
            _a = c.A / 255.0;
            Changed();
        }
        else HexBox.Text = ThemeService.ToHex(Color);
    }

    // ---- Conversions HSV ----

    public static Color FromHsv(double h, double s, double v, double a)
    {
        h = (h % 360 + 360) % 360;
        double c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c;
        var (r, g, b) = (int)(h / 60) switch
        {
            0 => (c, x, 0d), 1 => (x, c, 0d), 2 => (0d, c, x),
            3 => (0d, x, c), 4 => (x, 0d, c), _ => (c, 0d, x)
        };
        return Color.FromArgb((byte)Math.Round(a * 255), (byte)Math.Round((r + m) * 255),
                              (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }

    public static void ToHsv(Color c, out double h, out double s, out double v)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        h = d == 0 ? 0 : max == r ? 60 * ((g - b) / d % 6) : max == g ? 60 * ((b - r) / d + 2) : 60 * ((r - g) / d + 4);
        if (h < 0) h += 360;
        s = max == 0 ? 0 : d / max;
        v = max;
    }
}
