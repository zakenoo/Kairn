using System.IO;

namespace Kairn.Services;

/// <summary>
/// Journal de la synchronisation du calendrier, gardé sur ce PC et nulle part ailleurs.
/// Sans lui, un échec côté Apple se résume à « ça ne marche pas » : on note donc la méthode,
/// l'adresse appelée et le code de réponse, jamais le mot de passe ni le contenu des rendez-vous.
/// </summary>
public static class CalDavLog
{
    private static readonly object Lock = new();
    private const int MaxLines = 300;

    public static string Path => System.IO.Path.Combine(Storage.Root, "calendar.log");

    /// <summary>Avec marque d'ordre des octets : le Bloc-notes affiche alors les accents correctement.</summary>
    private static readonly System.Text.UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: true);

    public static void Line(string text)
    {
        lock (Lock)
        {
            try
            {
                File.AppendAllText(Path, $"[{DateTime.Now:HH:mm:ss}] {text}{Environment.NewLine}", Utf8);
                Trim();
            }
            catch { /* le journal ne doit jamais empêcher la synchro */ }
        }
    }

    /// <summary>Le fichier ne grossit pas indéfiniment : on ne garde que la fin.</summary>
    private static void Trim()
    {
        var lines = File.ReadAllLines(Path, Utf8);
        if (lines.Length <= MaxLines * 2) return;
        File.WriteAllLines(Path, lines[^MaxLines..], Utf8);
    }

    public static void Clear()
    {
        lock (Lock)
        {
            try { File.Delete(Path); } catch { }
        }
    }
}
