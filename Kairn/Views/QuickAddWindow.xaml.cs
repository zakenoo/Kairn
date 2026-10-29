using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Kairn.Models;
using Kairn.Services;

namespace Kairn.Views;

/// <summary>
/// Capture éclair : Ctrl+Alt+K n'importe où dans Windows, on écrit, Entrée, c'est noté.
/// Une idée qui demande d'ouvrir l'app, de choisir un jour et de saisir deux horaires est une idée perdue.
/// Sans heure, la tâche part dans « À reprendre » : on lui donnera un créneau plus tard, ou jamais, et ce n'est pas grave.
/// « demain 14h », « vendredi », « chaque lundi 9h » sont compris (voir QuickParse), et plusieurs lignes collées
/// d'un coup deviennent autant de tâches : de quoi vider sa tête sans trier.
/// </summary>
public partial class QuickAddWindow : Window
{
    private static QuickAddWindow? _open;
    /// <summary>La fenêtre part déjà : un dernier « Deactivated » ne doit pas la refermer une deuxième fois.</summary>
    private bool _closing;

    private QuickAddWindow()
    {
        InitializeComponent();
        Closing += (_, _) => _closing = true;
        Loaded += (_, _) =>
        {
            var area = SystemParameters.WorkArea;
            Left = area.Left + (area.Width - ActualWidth) / 2;
            Top = area.Top + area.Height * 0.22;
            BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(140)));
            Activate();
            Box.Focus();
        };
        // Cliquer ailleurs referme la fenêtre vide : elle ne doit jamais rester en travers du chemin.
        // Mais si quelque chose est déjà écrit, on ne le jette pas parce que le regard est parti ailleurs.
        Deactivated += (_, _) => { if (!_closing && Box.Text.Trim().Length == 0) Close(); };
        // Un collage de plusieurs lignes est gardé tel quel (une zone d'une ligne n'en garderait que la première).
        DataObject.AddPastingHandler(Box, (_, e) =>
        {
            if (e.DataObject.GetData(DataFormats.UnicodeText) is not string text || !text.Contains('\n')) return;
            e.CancelCommand();
            Box.SelectedText = text.Replace("\r", "").Trim('\n');
            Box.CaretIndex = Box.SelectionStart + Box.SelectionLength;
            Box.SelectionLength = 0;
        });
    }

    /// <summary>Ouvre la capture (ou remet au premier plan celle qui est déjà là).</summary>
    public static void Open()
    {
        if (_open is { IsLoaded: true })
        {
            _open.Activate();
            _open.Box.Focus();
            return;
        }
        var w = new QuickAddWindow();
        _open = w;
        w.Closed += (_, _) => { if (ReferenceEquals(_open, w)) _open = null; };
        w.Show();
    }

    private void Box_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        var items = Parse();
        string? text = items.Count switch
        {
            0 => null,
            1 => Describe(items[0]),
            _ => L.P("quick.preview.many", items.Count)
        };
        Preview.Text = text ?? "";
        Preview.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Ce que le texte donnerait, une ligne par idée.</summary>
    private List<QuickParse.Result> Parse()
    {
        var now = DateTime.Now;
        return Box.Text.Replace("\r", "").Split('\n')
            .Select(l => QuickParse.Parse(l, now))
            .OfType<QuickParse.Result>()
            .ToList();
    }

    /// <summary>« Demain · 14:00 – 15:00 · Dentiste », ou rien pour une simple idée sans jour ni heure.</summary>
    private static string? Describe(QuickParse.Result r)
    {
        var t = r.Task;
        var today = DateOnly.FromDateTime(DateTime.Now);
        if (r.Repeat != null) return L.F("quick.preview.repeat", Routines.Label(r.Repeat), t.TimeRange, t.Title);
        if (t.Date == today) return t.Floating ? null : L.F("quick.preview.slot", t.TimeRange, t.Title);
        var day = Capitalize(CarryOver.DayName(t.Date));
        return t.Floating ? L.F("quick.preview.slot", day, t.Title) : L.F("quick.preview.repeat", day, t.TimeRange, t.Title);
    }

    private static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpper(s[0], L.Culture) + s[1..];

    private void Box_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { e.Handled = true; Close(); return; }
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        // Maj+Entrée : une ligne de plus, pour noter plusieurs idées d'affilée.
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            int at = Box.CaretIndex;
            Box.Text = Box.Text.Insert(at, "\n");
            Box.CaretIndex = at + 1;
            return;
        }

        var items = Parse();
        if (items.Count == 0) { Close(); return; }
        foreach (var r in items)
        {
            Storage.Data.Tasks.Add(r.Task);
            if (r.Repeat != null) Routines.Create(r.Task, r.Repeat);
        }
        Storage.Save();
        App.Current.Refreshed();

        // Confirmation d'une seconde, puis la fenêtre s'efface : on retourne à ce qu'on faisait.
        Header.Text = items.Count > 1 ? L.P("quick.saved.many", items.Count) : Saved(items[0]);
        Box.Text = "";
        Preview.Visibility = Visibility.Collapsed;
        Hint.Text = L.T("quick.again");
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(950) };
        timer.Tick += (_, _) => { timer.Stop(); if (!_closing && Box.Text.Length == 0) Close(); };
        timer.Start();
    }

    private static string Saved(QuickParse.Result r)
    {
        var t = r.Task;
        if (r.Repeat != null) return L.F("quick.saved.repeat", Routines.Label(r.Repeat).ToUpper(L.Culture));
        var today = DateOnly.FromDateTime(DateTime.Now);
        if (t.Date == today) return t.Floating ? L.T("quick.saved.later") : L.F("quick.saved.slot", t.TimeRange);
        var day = CarryOver.DayName(t.Date).ToUpper(L.Culture);
        return L.F("quick.saved.slot", t.Floating ? day : $"{day} · {t.TimeRange}");
    }
}
