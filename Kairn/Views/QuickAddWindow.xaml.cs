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
        var parsed = Parse();
        if (parsed is { Floating: false })
        {
            Preview.Text = L.F("quick.preview.slot", parsed.TimeRange, parsed.Title);
            Preview.Visibility = Visibility.Visible;
        }
        else Preview.Visibility = Visibility.Collapsed;
    }

    /// <summary>Ce que le texte donnerait : une tâche avec un horaire s'il y en a un, sinon une tâche à reprendre.</summary>
    private PlanTask? Parse()
    {
        var text = Box.Text.Trim();
        if (text.Length == 0) return null;
        var today = DateOnly.FromDateTime(DateTime.Now);
        // « 14h Réviser » place la tâche ; « Réviser » la met de côté sans horaire.
        var parsed = PlanParser.Parse(text, today);
        if (parsed.Count == 1 && parsed[0].Title.Length > 0) return parsed[0];
        return new PlanTask
        {
            Date = today,
            Start = new TimeSpan(DateTime.Now.Hour, 0, 0),
            End = new TimeSpan(DateTime.Now.Hour, 0, 0) + TimeSpan.FromMinutes(30),
            Title = text,
            Floating = true
        };
    }

    private void Box_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { e.Handled = true; Close(); return; }
        if (e.Key != Key.Enter) return;
        e.Handled = true;

        if (Parse() is not { } task) { Close(); return; }
        Storage.Data.Tasks.Add(task);
        Storage.Save();
        App.Current.Refreshed();

        // Confirmation d'une seconde, puis la fenêtre s'efface : on retourne à ce qu'on faisait.
        Header.Text = task.Floating ? L.T("quick.saved.later") : L.F("quick.saved.slot", task.TimeRange);
        Box.Text = "";
        Preview.Visibility = Visibility.Collapsed;
        Hint.Text = L.T("quick.again");
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(950) };
        timer.Tick += (_, _) => { timer.Stop(); if (!_closing && Box.Text.Length == 0) Close(); };
        timer.Start();
    }
}
