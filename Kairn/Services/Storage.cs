using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Kairn.Models;

namespace Kairn.Services;

/// <summary>
/// Stockage 100 % local en JSON. Par défaut dans %AppData%\Kairn ;
/// si un dossier « data » existe à côté de l'exe, l'app passe en mode portable et l'utilise.
/// </summary>
public static class Storage
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() }
    };

    public static string Root { get; } = ResolveRoot();
    public static string LibraryDir => Path.Combine(Root, "library");
    private static string DataFile => Path.Combine(Root, "data.json");
    private static string SettingsFile => Path.Combine(Root, "settings.json");

    public static AppData Data { get; private set; } = new();
    public static AppSettings Settings { get; private set; } = new();

    private static string ResolveRoot()
    {
        var portable = Path.Combine(AppContext.BaseDirectory, "data");
        var root = Directory.Exists(portable)
            ? portable
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Kairn");
        Directory.CreateDirectory(root);
        return root;
    }

    public static void Load()
    {
        Data = Read<AppData>(DataFile) ?? new AppData();
        MigrateSettings();
        Settings = Read<AppSettings>(SettingsFile) ?? new AppSettings();
    }

    /// <summary>
    /// Ancien format (avant les thèmes complets) : "Theme": "Dark", "Accent", "FontFamily", "CornerRadius".
    /// On le convertit en thème complet sans perdre les choix déjà faits.
    /// </summary>
    private static void MigrateSettings()
    {
        try
        {
            if (!File.Exists(SettingsFile)) return;
            var root = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(SettingsFile))?.AsObject();
            if (root is null || root["Theme"] is not System.Text.Json.Nodes.JsonValue oldTheme) return;

            var theme = (oldTheme.ToString() == "Light" ? ThemePresets.All.First(p => p.Name == "Grès clair") : ThemePresets.All.First(p => p.Name == "Pierre & sable")).Clone();
            if (root["Accent"]?.ToString() is { Length: > 0 } accent) theme.Colors["Accent"] = accent;
            if (root["FontFamily"]?.ToString() is { Length: > 0 } font) theme.Font = font;
            if (root["CornerRadius"] is { } radius && double.TryParse(radius.ToString(), System.Globalization.CultureInfo.InvariantCulture, out var r)) theme.Radius = r;
            theme.Name = "Mon thème";

            foreach (var key in new[] { "Theme", "Accent", "FontFamily", "CornerRadius" }) root.Remove(key);
            root["Theme"] = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(theme, Options));
            File.Copy(SettingsFile, SettingsFile + ".avant-themes", overwrite: true);
            File.WriteAllText(SettingsFile, root.ToJsonString(Options));
        }
        catch { /* au pire, Read gardera une copie et repartira des valeurs par défaut */ }
    }

    /// <summary>Kairn a-t-il déjà été configuré sur ce PC ? (sinon : première installation)</summary>
    public static bool HasSettings => File.Exists(SettingsFile);

    public static void Save() => Write(DataFile, Data);
    public static void SaveSettings() => Write(SettingsFile, Settings);

    private static T? Read<T>(string path) where T : class
    {
        try
        {
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options);
        }
        catch
        {
            // Fichier corrompu : on garde une copie plutôt que de l'écraser.
            try { File.Copy(path, path + ".corrompu-" + DateTime.Now.ToString("yyyyMMddHHmmss"), true); } catch { }
            return null;
        }
    }

    private static void Write<T>(string path, T value)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, Options));
        File.Move(tmp, path, overwrite: true);
    }

    // ---- Requêtes pratiques ----

    public static IEnumerable<PlanTask> TasksFor(DateOnly date) =>
        Data.Tasks.Where(t => t.Date == date).OrderBy(t => t.Floating).ThenBy(t => t.Start);

    public static Category? CategoryById(string? id) =>
        id is null ? null : Data.Categories.FirstOrDefault(c => c.Id == id);

    /// <summary>Parent réel (un parent disparu fait de la catégorie une catégorie principale).</summary>
    public static Category? ParentOf(Category c) => CategoryById(c.ParentId) is { } p && p != c ? p : null;

    public static IEnumerable<Category> ChildrenOf(Category? parent) =>
        Data.Categories.Where(c => ParentOf(c) == parent);

    /// <summary>La catégorie et toutes ses sous-catégories, à n'importe quelle profondeur.</summary>
    public static IEnumerable<Category> WithDescendants(Category c)
    {
        yield return c;
        foreach (var child in ChildrenOf(c))
            foreach (var d in WithDescendants(child)) yield return d;
    }

    /// <summary>Toutes les catégories dans l'ordre de l'arbre, avec leur profondeur.</summary>
    public static IEnumerable<(Category Cat, int Depth)> CategoryTree()
    {
        IEnumerable<(Category, int)> Walk(Category? parent, int depth, HashSet<Category> seen)
        {
            foreach (var c in ChildrenOf(parent))
            {
                if (!seen.Add(c)) continue; // garde-fou contre une boucle dans les données
                yield return (c, depth);
                foreach (var x in Walk(c, depth + 1, seen)) yield return x;
            }
        }
        return Walk(null, 0, []);
    }

    /// <summary>« Dessin › Anatomie ».</summary>
    public static string CategoryPath(Category c)
    {
        var names = new List<string> { c.Name };
        var seen = new HashSet<Category> { c };
        for (var p = ParentOf(c); p != null && seen.Add(p); p = ParentOf(p)) names.Insert(0, p.Name);
        return string.Join(" › ", names);
    }

    public static string AbsoluteLibraryPath(string relative) => Path.Combine(LibraryDir, relative);

    public static void DeleteLibraryFile(string relative)
    {
        try { File.Delete(AbsoluteLibraryPath(relative)); } catch { }
    }
}
