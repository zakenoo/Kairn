using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace Kairn.Services;

/// <summary>
/// Icône de la zone de notification, en appels Windows directs (Shell_NotifyIcon et un menu natif).
/// Charger WinForms pour une icône et quatre lignes de menu coûtait ~75 ms au lancement et une dizaine de Mo de mémoire.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    /// <summary>Une ligne du menu. <c>Run</c> nul : un séparateur.</summary>
    public sealed record Item(string Text, Action? Run);

    private const int CallbackMessage = 0x8000 + 0x4B; // WM_APP + 'K'
    private const int WmLButtonUp = 0x0202, WmRButtonUp = 0x0205;
    private const uint NimAdd = 0, NimModify = 1, NimDelete = 2;
    private const int NifMessage = 0x1, NifIcon = 0x2, NifTip = 0x4;
    private const uint MfString = 0x0, MfSeparator = 0x800;
    private const uint TpmRightButton = 0x2, TpmReturnCmd = 0x100, TpmLayoutRtl = 0x8000;
    private const int SmCxSmIcon = 49, SmCySmIcon = 50;
    /// <summary>Envoyé à toutes les fenêtres quand l'Explorateur redémarre : les icônes sont à reposer.</summary>
    private static readonly int TaskbarCreated = (int)RegisterWindowMessage("TaskbarCreated");

    private readonly HwndSource _window;
    private readonly string _tip;
    private IntPtr _icon;
    private bool _added, _disposed;

    /// <summary>Clic gauche sur l'icône.</summary>
    public event Action? Click;
    /// <summary>Lignes du menu du clic droit, construites à l'ouverture (dans la langue du moment).</summary>
    public Func<IReadOnlyList<Item>>? Menu { get; set; }
    public bool RightToLeft { get; set; }

    public TrayIcon(string tip, byte[] ico)
    {
        _tip = tip;
        // Fenêtre invisible de 0 pixel : elle reçoit les clics sur l'icône et le signal d'un Explorateur redémarré.
        _window = new HwndSource(new HwndSourceParameters("Kairn.Tray") { Width = 0, Height = 0, WindowStyle = 0 });
        _window.AddHook(Hook);
        SetIcon(ico);
    }

    /// <summary>Change l'image (fichier .ico en mémoire) : la taille adaptée à l'écran est choisie dedans.</summary>
    public void SetIcon(byte[] ico)
    {
        if (_disposed) return;
        var old = _icon;
        _icon = CreateIcon(ico, GetSystemMetrics(SmCxSmIcon), GetSystemMetrics(SmCySmIcon));
        Show();
        if (old != IntPtr.Zero) DestroyIcon(old);
    }

    private void Show()
    {
        var data = Data();
        if (_added && Shell_NotifyIcon(NimModify, ref data)) return;
        _added = Shell_NotifyIcon(NimAdd, ref data);
    }

    private NOTIFYICONDATA Data() => new()
    {
        cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = _window.Handle,
        uID = 1,
        uFlags = NifMessage | NifIcon | NifTip,
        uCallbackMessage = CallbackMessage,
        hIcon = _icon,
        szTip = _tip,
        szInfo = "",
        szInfoTitle = "",
    };

    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == CallbackMessage)
        {
            handled = true;
            switch ((int)(lParam.ToInt64() & 0xFFFF))
            {
                case WmLButtonUp: Click?.Invoke(); break;
                case WmRButtonUp: ShowMenu(); break;
            }
        }
        else if (msg == TaskbarCreated && TaskbarCreated != 0)
        {
            _added = false;
            Show();
        }
        return IntPtr.Zero;
    }

    private void ShowMenu()
    {
        if (Menu?.Invoke() is not { Count: > 0 } items) return;
        var menu = CreatePopupMenu();
        try
        {
            for (int i = 0; i < items.Count; i++)
            {
                if (items[i].Run is null) AppendMenu(menu, MfSeparator, UIntPtr.Zero, null);
                else AppendMenu(menu, MfString, (UIntPtr)(uint)(i + 1), items[i].Text);
            }
            GetCursorPos(out var pt);
            // Sans premier plan, le menu ne se refermerait pas quand on clique ailleurs (comportement documenté de Windows).
            SetForegroundWindow(_window.Handle);
            int chosen = TrackPopupMenuEx(menu, TpmReturnCmd | TpmRightButton | (RightToLeft ? TpmLayoutRtl : 0), pt.X, pt.Y, _window.Handle, IntPtr.Zero);
            PostMessage(_window.Handle, 0, IntPtr.Zero, IntPtr.Zero); // WM_NULL, même raison
            if (chosen > 0 && chosen <= items.Count) items[chosen - 1].Run?.Invoke();
        }
        finally { DestroyMenu(menu); }
    }

    /// <summary>
    /// Icône Windows à la taille voulue, tirée d'un fichier .ico : en-tête de 6 octets, puis une entrée de 16 octets
    /// par image (largeur, hauteur, …, taille, position). On prend la plus petite image au moins aussi grande.
    /// </summary>
    private static IntPtr CreateIcon(byte[] ico, int cx, int cy)
    {
        try
        {
            int count = BitConverter.ToUInt16(ico, 4);
            int best = -1, bestScore = int.MaxValue;
            for (int i = 0; i < count; i++)
            {
                int w = ico[6 + 16 * i] == 0 ? 256 : ico[6 + 16 * i];
                int score = w >= cx ? w - cx : 1000 + cx - w;
                if (score < bestScore) { bestScore = score; best = i; }
            }
            if (best < 0) return IntPtr.Zero;
            int entry = 6 + 16 * best;
            int size = BitConverter.ToInt32(ico, entry + 8), offset = BitConverter.ToInt32(ico, entry + 12);
            var image = new byte[size];
            Buffer.BlockCopy(ico, offset, image, 0, size);
            return CreateIconFromResourceEx(image, (uint)size, true, 0x00030000, cx, cy, 0);
        }
        catch { return IntPtr.Zero; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_added)
        {
            var data = Data();
            Shell_NotifyIcon(NimDelete, ref data);
            _added = false;
        }
        if (_icon != IntPtr.Zero) DestroyIcon(_icon);
        _icon = IntPtr.Zero;
        _window.RemoveHook(Hook);
        _window.Dispose();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public int dwState;
        public int dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public int uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public int dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern bool Shell_NotifyIcon(uint message, ref NOTIFYICONDATA data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern IntPtr CreateIconFromResourceEx(byte[] bits, uint size, bool icon, uint version, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
    [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenu(IntPtr menu, uint flags, UIntPtr id, string? text);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")] private static extern int TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr hwnd, IntPtr options);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT point);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
}
