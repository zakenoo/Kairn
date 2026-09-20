using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace Kairn.Services;

/// <summary>
/// La petite récompense immédiate quand quelque chose est fait : un son doux et une animation.
/// Les sons sont fabriqués à la volée (aucun fichier à embarquer) et restent volontairement
/// courts et graves : de quoi être satisfaisant sans devenir agaçant au bout de trois jours.
/// </summary>
public static class Celebrate
{
    private const int Rate = 22050;

    private static readonly Lazy<byte[]> TaskWav = new(() => Wav([(784, 0.10, 0.22), (1046.5, 0.16, 0.20)]));
    private static readonly Lazy<byte[]> StepWav = new(() => Wav([(1174.7, 0.055, 0.11)]));
    private static readonly Lazy<byte[]> DayWav = new(() => Wav([(784, 0.10, 0.20), (988, 0.10, 0.20), (1318.5, 0.28, 0.18)]));

    /// <summary>Une tâche cochée.</summary>
    public static void Task() => Play(TaskWav);
    /// <summary>Une étape cochée : plus discret, ça peut arriver dix fois dans l'heure.</summary>
    public static void Step() => Play(StepWav);
    /// <summary>Un grand moment (dernière tâche du jour, fin d'une phase d'objectif).</summary>
    public static void Day() => Play(DayWav);

    private static void Play(Lazy<byte[]> wav)
    {
        if (!Storage.Settings.DoneSound) return;
        try
        {
            var player = new System.Media.SoundPlayer(new MemoryStream(wav.Value));
            player.Play(); // joue sur un thread à part : n'attend jamais l'interface
        }
        catch { /* pas de carte son, périphérique occupé : tant pis, ça reste un bonus */ }
    }

    /// <summary>Fabrique un petit WAV mono 16 bits : une suite de notes (fréquence, durée, volume) qui s'éteignent en douceur.</summary>
    private static byte[] Wav((double Hz, double Seconds, double Gain)[] notes)
    {
        var samples = new List<short>();
        foreach (var (hz, seconds, gain) in notes)
        {
            int n = (int)(Rate * seconds);
            for (int i = 0; i < n; i++)
            {
                double t = (double)i / Rate;
                // Attaque très courte puis extinction exponentielle : le son d'une goutte, pas d'un bip.
                double attack = Math.Min(1, i / (Rate * 0.006));
                double decay = Math.Exp(-3.4 * i / n);
                // Un soupçon d'harmonique : moins synthétique qu'une sinusoïde nue.
                double wave = Math.Sin(2 * Math.PI * hz * t) + 0.18 * Math.Sin(4 * Math.PI * hz * t);
                samples.Add((short)(wave * gain * attack * decay * short.MaxValue * 0.55));
            }
        }

        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        int dataBytes = samples.Count * 2;
        w.Write("RIFF"u8); w.Write(36 + dataBytes); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1);
        w.Write(Rate); w.Write(Rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(dataBytes);
        foreach (var s in samples) w.Write(s);
        w.Flush();
        return ms.ToArray();
    }

    // ===================== Confettis =====================

    /// <summary>
    /// Pluie de confettis par-dessus un élément. Réservée aux moments qui le méritent :
    /// si ça arrive à chaque case cochée, ça ne veut plus rien dire.
    /// </summary>
    public static void Confetti(FrameworkElement anchor)
    {
        if (!Storage.Settings.Celebrate || !anchor.IsVisible) return;
        var layer = AdornerLayer.GetAdornerLayer(anchor);
        if (layer is null) return;
        var adorner = new ConfettiAdorner(anchor);
        layer.Add(adorner);
        adorner.Run(() => layer.Remove(adorner));
    }

    private sealed class ConfettiAdorner(FrameworkElement target) : Adorner(target)
    {
        private readonly Canvas _canvas = new() { IsHitTestVisible = false };
        private readonly List<Shape> _bits = [];

        protected override int VisualChildrenCount => 1;
        protected override Visual GetVisualChild(int index) => _canvas;

        protected override Size MeasureOverride(Size c)
        {
            _canvas.Measure(c);
            return base.MeasureOverride(c);
        }

        protected override Size ArrangeOverride(Size size)
        {
            _canvas.Arrange(new Rect(size));
            return size;
        }

        public void Run(Action done)
        {
            AddVisualChild(_canvas);
            double w = Math.Max(120, target.ActualWidth), h = Math.Max(80, target.ActualHeight);
            var rng = new Random();
            var palette = new[] { "AccentBrush", "BreakBrush", "TextBrush", "AccentBrush" };

            for (int i = 0; i < 28; i++)
            {
                double size = 4 + rng.NextDouble() * 5;
                Shape bit = rng.Next(2) == 0
                    ? new Ellipse { Width = size, Height = size }
                    : new Rectangle { Width = size, Height = size * 1.6, RadiusX = 1, RadiusY = 1 };
                bit.SetResourceReference(Shape.FillProperty, palette[rng.Next(palette.Length)]);
                bit.Opacity = 0.9;

                double x = w * (0.15 + rng.NextDouble() * 0.7);
                double fall = h * (0.55 + rng.NextDouble() * 0.8);
                double drift = (rng.NextDouble() - 0.5) * w * 0.35;
                var move = new TranslateTransform();
                var spin = new RotateTransform();
                bit.RenderTransformOrigin = new Point(0.5, 0.5);
                bit.RenderTransform = new TransformGroup { Children = { spin, move } };
                Canvas.SetLeft(bit, x);
                Canvas.SetTop(bit, -size - rng.NextDouble() * 30);
                _canvas.Children.Add(bit);
                _bits.Add(bit);

                var span = TimeSpan.FromMilliseconds(950 + rng.Next(700));
                var delay = TimeSpan.FromMilliseconds(rng.Next(260));
                move.BeginAnimation(TranslateTransform.YProperty,
                    new DoubleAnimation(0, fall, span) { BeginTime = delay, EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn } });
                move.BeginAnimation(TranslateTransform.XProperty,
                    new DoubleAnimation(0, drift, span) { BeginTime = delay, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } });
                spin.BeginAnimation(RotateTransform.AngleProperty,
                    new DoubleAnimation(0, rng.Next(-360, 360), span) { BeginTime = delay });
                bit.BeginAnimation(OpacityProperty,
                    new DoubleAnimation(0.9, 0, TimeSpan.FromMilliseconds(420)) { BeginTime = delay + span - TimeSpan.FromMilliseconds(420) });
            }

            var end = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2100) };
            end.Tick += (_, _) => { end.Stop(); foreach (var b in _bits) b.BeginAnimation(OpacityProperty, null); done(); };
            end.Start();
        }
    }

    /// <summary>Une ligne qui vient d'être cochée s'illumine brièvement : la confirmation se voit du coin de l'œil.</summary>
    public static void Flash(FrameworkElement row)
    {
        if (!Storage.Settings.Celebrate) return;
        var scale = new ScaleTransform(1, 1);
        row.RenderTransformOrigin = new Point(0.5, 0.5);
        row.RenderTransform = scale;
        var bump = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(380) };
        bump.KeyFrames.Add(new EasingDoubleKeyFrame(1.018, KeyTime.FromPercent(0.28), new QuadraticEase { EasingMode = EasingMode.EaseOut }));
        bump.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromPercent(1), new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.5 }));
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, bump);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, bump.Clone());
    }
}
