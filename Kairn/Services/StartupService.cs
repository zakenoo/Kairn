using Microsoft.Win32;

namespace Kairn.Services;

/// <summary>Lancement au démarrage de Windows via la clé « Run » de l'utilisateur (aucun droit admin requis).</summary>
public static class StartupService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Kairn";
    public const string StartupArg = "--startup";

    public static void Apply(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key is null) return;
            var exe = Environment.ProcessPath;
            if (enabled && exe is not null)
                key.SetValue(ValueName, $"\"{exe}\" {StartupArg}");
            // On ne retire l'inscription que si c'est la nôtre : une autre copie de Kairn (portable, de test…)
            // ne doit pas désinscrire celle que tu utilises au quotidien.
            else if (exe is not null && key.GetValue(ValueName) is string current && current.Contains(exe, StringComparison.OrdinalIgnoreCase))
                key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch { /* pas bloquant */ }
    }
}
