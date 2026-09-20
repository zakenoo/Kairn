using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Kairn.Services;

namespace Kairn.Views;

/// <summary>
/// « Élément supprimé · Annuler » : la suppression est immédiate (pas de confirmation qui ralentit),
/// et on a quelques secondes pour se rattraper. L'action définitive n'a lieu qu'à la fin du délai.
/// </summary>
public class UndoBar : Border
{
    private readonly TextBlock _text = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 420 };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(8) };
    private readonly Button _undoBtn;
    private Action? _undo, _commit;

    public UndoBar()
    {
        Visibility = Visibility.Collapsed;
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Bottom;
        Margin = new Thickness(0, 0, 0, 12);
        Padding = new Thickness(18, 8, 8, 8);
        BorderThickness = new Thickness(1);
        SetResourceReference(BackgroundProperty, "Surface2Brush");
        SetResourceReference(BorderBrushProperty, "AccentBrush");
        SetResourceReference(CornerRadiusProperty, "Radius");
        Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 3, Opacity = 0.3 };

        _undoBtn = new Button { Content = L.T("undo.action"), Margin = new Thickness(16, 0, 0, 0) };
        _undoBtn.SetResourceReference(StyleProperty, "Primary");
        _undoBtn.Click += (_, _) => Undo();
        var row = new DockPanel();
        DockPanel.SetDock(_undoBtn, Dock.Right);
        row.Children.Add(_undoBtn);
        row.Children.Add(_text);
        Child = row;

        _timer.Tick += (_, _) => Commit();
        // On quitte la page : ce qui a été supprimé l'est pour de bon.
        IsVisibleChanged += (_, _) => { if (!IsVisible) Commit(); };
    }

    /// <summary>Affiche la barre. <paramref name="undo"/> remet l'élément, <paramref name="commit"/> finalise (ex : efface le fichier).</summary>
    public void Show(string text, Action undo, Action? commit = null)
    {
        Commit(); // une suppression précédente encore en attente devient définitive
        _undo = undo;
        _commit = commit;
        _text.Text = text;
        _undoBtn.Visibility = Visibility.Visible;
        Visibility = Visibility.Visible;
        _timer.Stop();
        _timer.Start();
    }

    /// <summary>Même bandeau, mais juste pour dire quelque chose : rien à annuler, pas de bouton.</summary>
    public void Note(string text)
    {
        Commit();
        _text.Text = text;
        _undoBtn.Visibility = Visibility.Collapsed;
        Visibility = Visibility.Visible;
        _timer.Stop();
        _timer.Start();
    }

    private void Undo()
    {
        var undo = _undo;
        _undo = null;
        _commit = null;
        Hide();
        undo?.Invoke();
    }

    public void Commit()
    {
        var commit = _commit;
        _undo = null;
        _commit = null;
        Hide();
        commit?.Invoke();
    }

    private void Hide()
    {
        _timer.Stop();
        Visibility = Visibility.Collapsed;
    }
}
