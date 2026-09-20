using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace Kairn.Services;

/// <summary>
/// Raccourci clavier valable partout dans Windows (Ctrl+Alt+K par défaut) :
/// noter une idée sans avoir à retrouver la fenêtre de Kairn. Deux secondes, sinon c'est oublié.
/// </summary>
public static class HotKey
{
    private const int WmHotKey = 0x0312;
    private const uint ModAlt = 0x0001, ModControl = 0x0002, ModNoRepeat = 0x4000;
    private const int Id = 0xBEEF;

    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private static HwndSource? _source;
    private static Action? _action;

    /// <summary>Vrai si Windows a bien accepté le raccourci (une autre app peut l'avoir pris avant nous).</summary>
    public static bool Registered { get; private set; }

    public static void Register(Action action)
    {
        Unregister();
        _action = action;
        // Fenêtre invisible de 0 pixel : juste une boîte aux lettres pour recevoir le message de Windows.
        var parameters = new HwndSourceParameters("Kairn.HotKey") { Width = 0, Height = 0, WindowStyle = 0 };
        _source = new HwndSource(parameters);
        _source.AddHook(Hook);
        Registered = RegisterHotKey(_source.Handle, Id, ModControl | ModAlt | ModNoRepeat, 0x4B /* K */);
        if (!Registered) Unregister();
    }

    public static void Unregister()
    {
        if (_source is null) return;
        try { UnregisterHotKey(_source.Handle, Id); } catch { }
        _source.RemoveHook(Hook);
        _source.Dispose();
        _source = null;
        Registered = false;
    }

    /// <summary>Suit le réglage : coché, le raccourci est pris ; décoché, il est rendu à Windows.</summary>
    public static void Apply(Action action)
    {
        if (Storage.Settings.QuickAddHotkey) Register(action);
        else Unregister();
    }

    private static IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotKey && wParam.ToInt32() == Id)
        {
            handled = true;
            _action?.Invoke();
        }
        return IntPtr.Zero;
    }
}
