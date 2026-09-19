namespace Kairn.Models;

/// <summary>Habillage d'un bloc de l'interface : une couleur ou une image de fond.</summary>
public class ZoneStyle
{
    /// <summary>Couleur du bloc (#RRGGBB ou #AARRGGBB). Null = couleur du thème par défaut pour ce bloc.</summary>
    public string? Color { get; set; }

    /// <summary>Image de fond (nom de fichier dans le dossier « themes »). Null = pas d'image.</summary>
    public string? Image { get; set; }

    /// <summary>Voile posé sur l'image pour garder le texte lisible (0 = aucun, 1 = opaque).</summary>
    public double Veil { get; set; } = 0.45;

    /// <summary>Opacité du bloc (utile pour laisser voir l'image de fond de l'app derrière).</summary>
    public double Opacity { get; set; } = 1;

    public ZoneStyle Clone() => (ZoneStyle)MemberwiseClone();
}

/// <summary>Un thème complet : toutes les couleurs, les blocs, la forme et le texte.</summary>
public class ThemeDef
{
    public string Name { get; set; } = "Mon thème";
    /// <summary>Thème sombre : barre de titre Windows sombre.</summary>
    public bool Dark { get; set; } = true;
    public Dictionary<string, string> Colors { get; set; } = [];
    public Dictionary<string, ZoneStyle> Zones { get; set; } = [];
    public string Font { get; set; } = "Segoe UI Variable Display";
    public double Radius { get; set; } = 12;
    public double BorderWidth { get; set; } = 1;
    /// <summary>Taille globale de l'interface (1 = 100 %).</summary>
    public double UiScale { get; set; } = 1;
    /// <summary>Couleur des pierres de l'icône (barre des tâches, notification). Null = couleur d'accent.</summary>
    public string? IconStones { get; set; }
    /// <summary>Fond de l'icône. Null = fond de l'app.</summary>
    public string? IconTile { get; set; }

    public string Color(string key) =>
        Colors.TryGetValue(key, out var c) ? c : ThemeTokens.Fallback(key);

    public ZoneStyle Zone(string key)
    {
        if (!Zones.TryGetValue(key, out var z)) Zones[key] = z = new ZoneStyle();
        return z;
    }

    public ThemeDef Clone()
    {
        var t = (ThemeDef)MemberwiseClone();
        t.Colors = new Dictionary<string, string>(Colors);
        t.Zones = Zones.ToDictionary(kv => kv.Key, kv => kv.Value.Clone());
        return t;
    }
}

/// <summary>Liste des couleurs et des blocs personnalisables, avec leur nom lisible.</summary>
public static class ThemeTokens
{
    public static readonly (string Key, string Label)[] ColorKeys =
    [
        ("Bg", "Fond de l'app"),
        ("Surface", "Cartes"),
        ("Surface2", "Boutons et champs"),
        ("Hover", "Survol"),
        ("Line", "Bordures"),
        ("Text", "Texte"),
        ("Subtle", "Texte secondaire"),
        ("Faint", "Texte discret"),
        ("Accent", "Accent"),
        ("OnAccent", "Texte sur l'accent"),
        ("Break", "Pauses"),
        ("Danger", "Suppression"),
    ];

    /// <summary>Blocs habillables : clé, nom, couleur du thème utilisée par défaut.</summary>
    public static readonly (string Key, string Label, string DefaultColor)[] ZoneKeys =
    [
        ("Window", "Fond de l'app", "Bg"),
        ("Sidebar", "Barre latérale", "Surface"),
        ("NowCard", "Carte « En cours »", "Surface"),
        ("Card", "Cartes (calendrier, À reprendre, réglages…)", "Surface"),
        ("Row", "Lignes de tâches et éléments", "Surface"),
        ("Guard", "Encart du garde", "Surface2"),
        ("Nudge", "Bandeau de rappel", "Surface"),
    ];

    public static string Fallback(string key) => key switch
    {
        "Bg" => "#000000", "Surface" => "#0A0A0A", "Surface2" => "#151515", "Hover" => "#1E1E1E",
        "Line" => "#1F1F1F", "Text" => "#F2F2F2", "Subtle" => "#8C8C8C", "Faint" => "#4D4D4D",
        "Accent" => "#F2F2F2", "OnAccent" => "#000000", "Break" => "#9AD1A8", "Danger" => "#FF6F61",
        _ => "#FF00FF"
    };
}
