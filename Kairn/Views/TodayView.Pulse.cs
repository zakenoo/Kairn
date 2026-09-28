using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;

namespace Kairn.Views;

/// <summary>
/// La pastille « en direct » de la carte En cours : elle respire (de 1 à 0,25 d'opacité en 1,1 s, puis retour),
/// mais seulement quand la carte est à l'écran. L'ancienne animation XAML tournait sans fin, même Kairn caché
/// dans la zone de notification : ~1 % d'un cœur et près de mille réveils du processeur par seconde, pour rien.
/// </summary>
public partial class TodayView
{
    private readonly DispatcherTimer _pulse = new() { Interval = TimeSpan.FromMilliseconds(83) }; // ~12 images par seconde
    private readonly Stopwatch _pulseClock = new();
    /// <summary>La carte montre une tâche déjà finie : la pastille reste immobile.</summary>
    private bool _calm;

    private void InitPulse() => _pulse.Tick += (_, _) =>
    {
        double p = _pulseClock.Elapsed.TotalSeconds % 2.2 / 1.1;
        LiveDot.Opacity = p <= 1 ? 1 - 0.75 * p : 0.25 + 0.75 * (p - 1);
    };

    private void UpdatePulse()
    {
        bool on = _active && NowCard.Visibility == Visibility.Visible && !_calm;
        if (on == _pulse.IsEnabled) return;
        if (on)
        {
            _pulseClock.Restart();
            _pulse.Start();
        }
        else
        {
            _pulse.Stop();
            LiveDot.Opacity = 1;
        }
    }
}
