using Kairn.Models;

namespace Kairn.Services;

/// <summary>Thèmes préfaits. Chacun est un point de départ : tout reste modifiable ensuite.</summary>
public static class ThemePresets
{
    public static ThemeDef Default() => All[0].Clone();

    public static readonly List<ThemeDef> All =
    [
        Make("Pierre & sable", true, "#121110", "#1A1816", "#23201D", "#2B2824", "#2D2A26", "#EEEAE4", "#9A948B", "#5E5952", "#D6A461", "#1A1510", "#8FBF9A", "#E0735F"),
        Make("Grès clair", false, "#F6F3EE", "#FFFDF9", "#EFEAE2", "#E6E0D6", "#E3DDD3", "#1C1915", "#6F685E", "#A9A196", "#B7813F", "#FFFFFF", "#4E9A64", "#C4513C"),
        Make("Sauge & brume", true, "#0F1311", "#161B18", "#1D2420", "#242C27", "#26302A", "#E6ECE8", "#8FA096", "#56635B", "#7FB69A", "#0F1311", "#D6B97A", "#E07A6A"),
        Make("Ardoise & glacier", true, "#0E1114", "#151A1F", "#1C2229", "#232A32", "#252D36", "#E8EDF2", "#8B97A5", "#54606D", "#7FA7C9", "#0E1114", "#8FC7A3", "#E07A6A"),
        Make("Terracotta", true, "#131010", "#1B1716", "#241F1D", "#2C2624", "#2E2826", "#F0E9E6", "#A3948E", "#62574F", "#D9785A", "#1A0F0B", "#8FBF9A", "#E0605A"),
        Make("Forêt nocturne", true, "#0C1210", "#121A17", "#18231F", "#1E2B26", "#1F2C27", "#E3EDE7", "#86A094", "#4E6159", "#5FBF8F", "#06120C", "#E0C27A", "#E07A6A"),
        Make("Crépuscule", true, "#121019", "#1A1724", "#221E2F", "#2A2539", "#2B263A", "#ECE9F5", "#9A93AE", "#5C566E", "#A99BE8", "#15121F", "#8FC7A3", "#E0736F"),
        Make("Encre", true, "#000000", "#0A0A0A", "#151515", "#1E1E1E", "#1F1F1F", "#F2F2F2", "#8C8C8C", "#4D4D4D", "#F2F2F2", "#000000", "#9AD1A8", "#FF6F61", radius: 6),
        Make("Papier", false, "#F4F1EA", "#FBF9F4", "#ECE7DD", "#E3DDD0", "#DDD6C8", "#2A2621", "#6E665B", "#A59C8E", "#2F4B6E", "#FFFFFF", "#4F8A5B", "#B5452F", font: "Georgia", radius: 4),
        Make("Rose poudré", false, "#F8F2F1", "#FFFBFA", "#F0E6E4", "#E8DCD9", "#E6D9D6", "#2B1F1E", "#7A6461", "#B09B98", "#C4717A", "#FFFFFF", "#5E9A73", "#B8453A", radius: 16),
        Make("Néon", true, "#0A0A12", "#11111D", "#181828", "#202035", "#262640", "#EAEAFF", "#8E8EB5", "#4E4E70", "#00E5C7", "#04110F", "#FF71CE", "#FF4D6D", font: "Bahnschrift", radius: 8),
        Make("Haut contraste", true, "#000000", "#000000", "#1A1A1A", "#333333", "#FFFFFF", "#FFFFFF", "#E0E0E0", "#BDBDBD", "#FFD400", "#000000", "#00E676", "#FF5252", radius: 4, border: 2),
    ];

    private static ThemeDef Make(string name, bool dark, string bg, string surface, string surface2, string hover, string line,
        string text, string subtle, string faint, string accent, string onAccent, string brk, string danger,
        string font = "Segoe UI Variable Display", double radius = 12, double border = 1) => new()
    {
        Name = name,
        Dark = dark,
        Font = font,
        Radius = radius,
        BorderWidth = border,
        Colors = new()
        {
            ["Bg"] = bg, ["Surface"] = surface, ["Surface2"] = surface2, ["Hover"] = hover, ["Line"] = line,
            ["Text"] = text, ["Subtle"] = subtle, ["Faint"] = faint, ["Accent"] = accent, ["OnAccent"] = onAccent,
            ["Break"] = brk, ["Danger"] = danger,
        }
    };
}
