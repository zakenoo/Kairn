using System.Runtime.InteropServices;
using System.Text;

namespace Kairn.Services;

/// <summary>
/// Chiffrement Windows (DPAPI) lié au compte Windows courant : le mot de passe mail enregistré
/// n'est lisible que par toi, sur ce PC. Copier settings.json ailleurs ne suffit pas à le récupérer.
/// </summary>
public static class Secret
{
    [StructLayout(LayoutKind.Sequential)]
    private struct DATA_BLOB { public int cbData; public IntPtr pbData; }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref DATA_BLOB input, string? desc, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, ref DATA_BLOB output);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(ref DATA_BLOB input, IntPtr desc, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, ref DATA_BLOB output);

    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr h);

    public static string Protect(string plain) => Convert.ToBase64String(Run(Encoding.UTF8.GetBytes(plain), protect: true));

    public static string? Unprotect(string protectedB64)
    {
        try { return Encoding.UTF8.GetString(Run(Convert.FromBase64String(protectedB64), protect: false)); }
        catch { return null; }
    }

    private static byte[] Run(byte[] data, bool protect)
    {
        var input = new DATA_BLOB { cbData = data.Length, pbData = Marshal.AllocHGlobal(data.Length) };
        var output = new DATA_BLOB();
        try
        {
            Marshal.Copy(data, 0, input.pbData, data.Length);
            const int UiForbidden = 0x1;
            bool ok = protect
                ? CryptProtectData(ref input, "Kairn", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden, ref output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden, ref output);
            if (!ok) throw new InvalidOperationException("DPAPI");
            var result = new byte[output.cbData];
            Marshal.Copy(output.pbData, result, 0, output.cbData);
            return result;
        }
        finally
        {
            Marshal.FreeHGlobal(input.pbData);
            if (output.pbData != IntPtr.Zero) LocalFree(output.pbData);
        }
    }
}
