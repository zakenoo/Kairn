using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;

namespace Kairn.Services;

/// <summary>Une langue disponible : code, nom dans sa propre langue, sens d'écriture.</summary>
public record Language(string Code, string NativeName, string Culture, bool Rtl = false);

/// <summary>
/// Traductions de l'interface. Chaque texte a une clé (ex : « nav.today »).
/// Les fichiers sont intégrés à l'exe (i18n/*.json) et peuvent être complétés ou corrigés
/// par un fichier dans le dossier de données : data/lang/xx.json (pratique pour contribuer une langue).
/// Ordre de secours : langue choisie → anglais → français → la clé elle-même.
/// </summary>
public sealed class Loc : INotifyPropertyChanged
{

    /// <summary>Langues les plus parlées au monde (+ celles ajoutées dans data/lang).</summary>
    public static readonly List<Language> Languages =
    [
        new("en", "English", "en-US"),
        new("zh", "中文（简体）", "zh-CN"),
        new("hi", "हिन्दी", "hi-IN"),
        new("es", "Español", "es-ES"),
        new("fr", "Français", "fr-FR"),
        new("ar", "العربية", "ar-EG", Rtl: true), // ar-EG : calendrier grégorien (ar-SA afficherait des dates hégiriennes)
        new("bn", "বাংলা", "bn-BD"),
        new("pt", "Português", "pt-BR"),
        new("ru", "Русский", "ru-RU"),
        new("ja", "日本語", "ja-JP"),
        new("de", "Deutsch", "de-DE"),
        new("id", "Bahasa Indonesia", "id-ID"),
    ];

    // Déclarée après la liste des langues : l'initialisation statique suit l'ordre du fichier.
    public static Loc Instance { get; } = new();

    private Dictionary<string, string> _current = [];
    private Dictionary<string, string> _en = [];
    private Dictionary<string, string> _fr = [];

    public event PropertyChangedEventHandler? PropertyChanged;
    /// <summary>Levé après un changement de langue (pour reconstruire ce qui est généré en code).</summary>
    public event Action? Changed;

    public Language Current { get; private set; } = Languages[4];
    public CultureInfo Culture { get; private set; } = CultureInfo.GetCultureInfo("fr-FR");

    /// <summary>Utilisé par les liaisons XAML : {l:T clé}.</summary>
    public string this[string key] => Get(key);

    public string Get(string key) =>
        _current.TryGetValue(key, out var v) || _en.TryGetValue(key, out v) || _fr.TryGetValue(key, out v) ? v : key;

    /// <summary>Langue de Windows si elle est disponible, sinon anglais.</summary>
    public static string SystemDefault()
    {
        var two = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        return Languages.Any(l => l.Code == two) ? two : "en";
    }

    public void Load(string code)
    {
        DiscoverExtraLanguages();
        Current = Languages.FirstOrDefault(l => l.Code == code) ?? Languages[0];
        _fr = Read("fr");
        _en = Read("en");
        _current = Read(Current.Code);
        try { Culture = CultureInfo.GetCultureInfo(Current.Culture); } catch { Culture = CultureInfo.InvariantCulture; }
        CultureInfo.DefaultThreadCurrentCulture = Culture;
        CultureInfo.DefaultThreadCurrentUICulture = Culture;
        Thread.CurrentThread.CurrentCulture = Culture;
        Thread.CurrentThread.CurrentUICulture = Culture;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        Changed?.Invoke();
    }

    private static string UserLangDir => Path.Combine(Storage.Root, "lang");

    /// <summary>Un fichier data/lang/xx.json d'une langue inconnue l'ajoute à la liste (clé « _name » = nom affiché).</summary>
    private static void DiscoverExtraLanguages()
    {
        if (!Directory.Exists(UserLangDir)) return;
        foreach (var file in Directory.GetFiles(UserLangDir, "*.json"))
        {
            var code = Path.GetFileNameWithoutExtension(file);
            if (code.StartsWith('_')) continue; // _modele.json et compagnie : des exemples, pas des langues
            if (Languages.Any(l => l.Code == code)) continue;
            try
            {
                var map = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file)) ?? [];
                Languages.Add(new Language(code, map.GetValueOrDefault("_name", code), map.GetValueOrDefault("_culture", code),
                                           map.GetValueOrDefault("_rtl") == "true"));
            }
            catch { }
        }
    }

    /// <summary>
    /// Prépare le dossier des langues avant de l'ouvrir : un mode d'emploi et un modèle complet (tous les textes en anglais),
    /// pour qu'il ne soit pas juste un dossier vide et mystérieux. Réécrits à chaque fois : toujours à jour.
    /// </summary>
    public static string PrepareUserLangDir()
    {
        Directory.CreateDirectory(UserLangDir);
        try
        {
            File.WriteAllText(Path.Combine(UserLangDir, "_" + L.T("set.language.readme.file") + ".txt"), L.T("set.language.readme"));
            var model = new Dictionary<string, string> { ["_name"] = "Language name", ["_culture"] = "xx-XX", ["_rtl"] = "false" };
            foreach (var (k, v) in Read("en")) model[k] = v;
            File.WriteAllText(Path.Combine(UserLangDir, "_modele.json"),
                JsonSerializer.Serialize(model, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        }
        catch { /* le dossier s'ouvrira quand même */ }
        return UserLangDir;
    }

    private static Dictionary<string, string> Read(string code)
    {
        var map = new Dictionary<string, string>();
        try
        {
            using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream($"Kairn.i18n.{code}.json");
            if (s != null) map = JsonSerializer.Deserialize<Dictionary<string, string>>(s) ?? [];
        }
        catch { }
        // Surcharge locale (corrections ou nouvelle langue), sans recompiler.
        try
        {
            var file = Path.Combine(UserLangDir, code + ".json");
            if (File.Exists(file))
                foreach (var (k, v) in JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file)) ?? [])
                    map[k] = v;
        }
        catch { }
        return map;
    }
}

/// <summary>Raccourcis pour le code : L.T("clé"), L.F("clé", arg0, arg1…).</summary>
public static class L
{
    public static string T(string key) => Loc.Instance.Get(key);
    public static string F(string key, params object?[] args)
    {
        try { return string.Format(Loc.Instance.Culture, Loc.Instance.Get(key), args); }
        catch (FormatException) { return Loc.Instance.Get(key); }
    }
    /// <summary>Choisit la forme singulier/pluriel : clés « xxx.one » et « xxx.other ».</summary>
    public static string P(string key, int n, params object?[] args) =>
        F(key + (n == 1 ? ".one" : ".other"), [n, .. args]);
    public static CultureInfo Culture => Loc.Instance.Culture;
}
