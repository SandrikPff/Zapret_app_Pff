using System.Windows;
using System.Windows.Media;

namespace ZapretAppPff;

internal static class Theme
{
    private static readonly string[] Names = ["Window", "Rail", "Surface", "Field", "Hover", "Stroke", "Text", "Muted", "Quiet", "Good"];
    private static readonly string[] Dark = ["#151518", "#19191D", "#1F1F24", "#18181C", "#292930",
        "#34343C", "#FAF8F8", "#B1AFBA", "#8F8D99", "#85D5AB"];
    private static readonly string[] Light = ["#F5EDE0", "#EEE5D7", "#FFF9EF", "#FCF4E9", "#F3E8D9",
        "#DCD0BF", "#26221F", "#59504B", "#6F625B", "#287A56"];

    public static void Apply(Preferences prefs)
    {
        var resources = Application.Current.Resources;
        var colors = prefs.Light ? Light : Dark;
        for (var index = 0; index < Names.Length; index++)
            resources[Names[index] + "Brush"] = Brush(Parse(colors[index]));

        ApplyAccent(prefs);
    }

    public static void ApplyAccent(Preferences prefs)
    {
        var resources = Application.Current.Resources;
        var colors = prefs.Light ? Light : Dark;
        var accent = Parse(prefs.Accent);
        var surface = Parse(colors[2]);
        var text = Parse(colors[6]);
        var accentText = WithContrast(accent, surface, text);
        var blackInk = Parse("#171217");
        var whiteInk = Colors.White;
        var accentInk = Contrast(accent, blackInk) >= Contrast(accent, whiteInk) ? blackInk : whiteInk;

        resources["AccentBrush"] = Brush(accent);
        resources["AccentTextBrush"] = Brush(accentText);
        resources["AccentInkBrush"] = Brush(accentInk);
        resources["AccentHoverBrush"] = Brush(Mix(accent, accentInk == whiteInk ? Colors.Black : Colors.White, .14));
        resources["AccentTintBrush"] = Brush(Alpha(accent, prefs.Light ? (byte)31 : (byte)48));
        resources["AccentGlowColor"] = Alpha(accent, prefs.Light ? (byte)59 : (byte)86);
        resources["AccentHaloColor"] = Alpha(accent, prefs.Light ? (byte)26 : (byte)50);
        resources["AccentClearColor"] = Alpha(accent, 0);
    }

    private static Color Parse(string hex) => (Color)ColorConverter.ConvertFromString(hex);
    private static Color Alpha(Color color, byte alpha) => Color.FromArgb(alpha, color.R, color.G, color.B);

    private static SolidColorBrush Brush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Color WithContrast(Color accent, Color background, Color ink)
    {
        for (var percent = 0; percent <= 100; percent += 5)
        {
            var candidate = Mix(accent, ink, percent / 100d);
            if (Contrast(candidate, background) >= 4.5)
                return candidate;
        }
        return ink;
    }

    private static Color Mix(Color from, Color to, double amount) => Color.FromRgb(
        (byte)Math.Round(from.R + (to.R - from.R) * amount),
        (byte)Math.Round(from.G + (to.G - from.G) * amount),
        (byte)Math.Round(from.B + (to.B - from.B) * amount));

    private static double Contrast(Color first, Color second)
    {
        var a = Luminance(first);
        var b = Luminance(second);
        return (Math.Max(a, b) + .05) / (Math.Min(a, b) + .05);
    }

    private static double Luminance(Color color) =>
        .2126 * Linear(color.R) + .7152 * Linear(color.G) + .0722 * Linear(color.B);

    private static double Linear(byte channel)
    {
        var value = channel / 255d;
        return value <= .04045 ? value / 12.92 : Math.Pow((value + .055) / 1.055, 2.4);
    }
}
