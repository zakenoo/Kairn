using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace Kairn.Services.Assistant;

/// <summary>
/// Le modèle local intégré : llama.cpp (llama-server) + un petit modèle (Qwen3 4B, licence Apache 2.0).
/// Rien n'est installé tant qu'on ne le demande pas. Le serveur ne tourne que pendant une génération
/// (sur 127.0.0.1 uniquement), puis s'arrête tout seul : aucune mémoire occupée au repos.
/// </summary>
public static class LocalEngine
{
    public const string ModelRepo = "unsloth/Qwen3-4B-Instruct-2507-GGUF";
    public const string ModelFile = "Qwen3-4B-Instruct-2507-Q4_K_M.gguf";
    private const long ModelSize = 2_497_281_120;
    private const string ModelSha256 = "3605803b982cb64aead44f6c1b2ae36e3acdb41d8e46c8a94c6533bc4c67e597";
    /// <summary>Taille approximative annoncée avant l'installation (modèle + moteur).</summary>
    public const double DownloadGb = 2.6;

    private static string Dir => Path.Combine(Storage.Root, "assistant");
    private static string EngineDir => Path.Combine(Dir, "engine");
    private static string ModelPath => Path.Combine(Dir, "models", ModelFile);
    private static string ServerExe => Path.Combine(EngineDir, "llama-server.exe");

    public static bool IsInstalled => File.Exists(ServerExe) && File.Exists(ModelPath) && new FileInfo(ModelPath).Length == ModelSize;

    public static long InstalledBytes =>
        Directory.Exists(Dir) ? new DirectoryInfo(Dir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length) : 0;

    private static readonly HttpClient Http = CreateHttp();

    private static HttpClient CreateHttp()
    {
        var h = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("Kairn");
        return h;
    }

    // ===================== Installation =====================

    /// <summary>Progression : (étape, fraction 0-1). Reprend là où un téléchargement interrompu s'était arrêté.</summary>
    public static async Task InstallAsync(IProgress<(string Step, double Fraction)> progress, CancellationToken ct)
    {
        Directory.CreateDirectory(EngineDir);
        Directory.CreateDirectory(Path.GetDirectoryName(ModelPath)!);

        if (!File.Exists(ServerExe))
        {
            progress.Report(("engine", 0));
            var (url, sha) = await FindEngineAsync(ct);
            var zip = Path.Combine(Dir, "engine.zip");
            await DownloadAsync(url, zip, null, f => progress.Report(("engine", f)), ct);
            if (sha != null && !string.Equals(await Sha256Async(zip, ct), sha, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(zip);
                throw new InvalidDataException("engine checksum");
            }
            ZipFile.ExtractToDirectory(zip, EngineDir, overwriteFiles: true);
            File.Delete(zip);
            // Certaines versions rangent les fichiers dans un sous-dossier : on remonte l'exe et ses dll.
            if (!File.Exists(ServerExe) && Directory.GetFiles(EngineDir, "llama-server.exe", SearchOption.AllDirectories).FirstOrDefault() is { } found)
                foreach (var f in Directory.GetFiles(Path.GetDirectoryName(found)!))
                    File.Move(f, Path.Combine(EngineDir, Path.GetFileName(f)), overwrite: true);
        }

        if (!File.Exists(ModelPath) || new FileInfo(ModelPath).Length != ModelSize)
        {
            var part = ModelPath + ".part";
            await DownloadAsync($"https://huggingface.co/{ModelRepo}/resolve/main/{ModelFile}", part, ModelSize, f => progress.Report(("model", f)), ct);
            progress.Report(("verify", 1));
            if (!string.Equals(await Sha256Async(part, ct), ModelSha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(part);
                throw new InvalidDataException("model checksum");
            }
            File.Move(part, ModelPath, overwrite: true);
        }
    }

    public static void Uninstall()
    {
        Stop();
        try { if (Directory.Exists(Dir)) Directory.Delete(Dir, recursive: true); } catch { }
    }

    /// <summary>Dernière version compilée de llama.cpp pour Windows (Vulkan : carte graphique, et processeur en secours).</summary>
    private static async Task<(string Url, string? Sha)> FindEngineAsync(CancellationToken ct)
    {
        const string api = "https://api.github.com/repos/ggml-org/llama.cpp/releases";
        // La version stable indique quelle compilation lui correspond (« Nightly build: b10964 ») : on prend celle-là,
        // plutôt que la toute dernière compilation du jour.
        try
        {
            using var latest = JsonDocument.Parse(await Http.GetStringAsync(api + "/latest", ct));
            if (Pick(latest.RootElement) is { } direct) return direct;
            var body = latest.RootElement.GetProperty("body").GetString() ?? "";
            var m = System.Text.RegularExpressions.Regex.Match(body, @"releases/tag/(b\d+)");
            if (m.Success)
            {
                using var build = JsonDocument.Parse(await Http.GetStringAsync($"{api}/tags/{m.Groups[1].Value}", ct));
                if (Pick(build.RootElement) is { } found) return found;
            }
        }
        catch (HttpRequestException) { }

        using var doc = JsonDocument.Parse(await Http.GetStringAsync(api + "?per_page=20", ct));
        foreach (var rel in doc.RootElement.EnumerateArray())
            if (Pick(rel) is { } any) return any;
        throw new InvalidOperationException("no engine build found");

        static (string, string?)? Pick(JsonElement rel)
        {
            foreach (var a in rel.GetProperty("assets").EnumerateArray())
            {
                var name = a.GetProperty("name").GetString() ?? "";
                if (!name.StartsWith("llama-", StringComparison.Ordinal) || !name.EndsWith("-bin-win-vulkan-x64.zip", StringComparison.Ordinal)) continue;
                string? sha = a.TryGetProperty("digest", out var d) && d.GetString() is { } dig && dig.StartsWith("sha256:") ? dig[7..] : null;
                return (a.GetProperty("browser_download_url").GetString()!, sha);
            }
            return null;
        }
    }

    private static async Task DownloadAsync(string url, string dest, long? expected, Action<double> report, CancellationToken ct)
    {
        long have = File.Exists(dest) ? new FileInfo(dest).Length : 0;
        if (expected is { } e && have > e) { File.Delete(dest); have = 0; }
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (have > 0) req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(have, null);
        using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (have > 0 && resp.StatusCode != System.Net.HttpStatusCode.PartialContent) have = 0; // pas de reprise possible : on recommence
        resp.EnsureSuccessStatusCode();
        long total = (resp.Content.Headers.ContentLength ?? 0) + have;
        if (expected is { } ex) total = ex;

        await using var src = await resp.Content.ReadAsStreamAsync(ct);
        await using var fs = new FileStream(dest, have > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
        var buf = new byte[1 << 20];
        long done = have;
        var last = DateTime.MinValue;
        int n;
        while ((n = await src.ReadAsync(buf, ct)) > 0)
        {
            await fs.WriteAsync(buf.AsMemory(0, n), ct);
            done += n;
            if (total > 0 && (DateTime.Now - last).TotalMilliseconds > 200) { last = DateTime.Now; report(Math.Min(1, (double)done / total)); }
        }
        report(1);
    }

    private static async Task<string> Sha256Async(string path, CancellationToken ct)
    {
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true);
        return Convert.ToHexString(await SHA256.HashDataAsync(fs, ct));
    }

    // ===================== Démarrage à la demande =====================

    private static Process? _server;
    private static int _port;
    private static bool _serverGpu;
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static Timer? _idle;

    /// <summary>Démarre le serveur si besoin et renvoie son adresse (compatible OpenAI).</summary>
    public static async Task<string> EnsureRunningAsync(bool gpu, CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        try
        {
            _idle?.Dispose();
            _idle = null;
            if (_server is { HasExited: false } && (_serverGpu == gpu || !gpu)) return BaseUrl;
            Stop();
            if (!IsInstalled) throw new InvalidOperationException("not installed");

            // Sur la carte graphique d'abord si demandé ; si elle refuse (pilote, mémoire), on retente sur le processeur.
            foreach (var useGpu in gpu ? new[] { true, false } : new[] { false })
                if (await StartAsync(useGpu, ct)) return BaseUrl;
            throw new InvalidOperationException("server exited");
        }
        finally { Gate.Release(); }
    }

    private static async Task<bool> StartAsync(bool gpu, CancellationToken ct)
    {
        _port = FreePort();
        _serverGpu = gpu;
        var psi = new ProcessStartInfo(ServerExe)
        {
            WorkingDirectory = EngineDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in new[] { "-m", ModelPath, "--host", "127.0.0.1", "--port", _port.ToString(), "-c", "8192", "--no-webui",
                                  "-ngl", gpu ? "99" : "0" })
            psi.ArgumentList.Add(a);
        _server = Process.Start(psi) ?? throw new InvalidOperationException("server start");
        _server.OutputDataReceived += (_, _) => { };
        _server.ErrorDataReceived += (_, _) => { };
        _server.BeginOutputReadLine();
        _server.BeginErrorReadLine();
        ChildProcessGuard.Attach(_server);

        // Le modèle se charge : on attend que le serveur réponde « prêt ».
        var deadline = DateTime.Now.AddMinutes(3);
        while (DateTime.Now < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (_server.HasExited) { Stop(); return false; }
            try
            {
                using var r = await Http.GetAsync($"http://127.0.0.1:{_port}/health", ct);
                if (r.IsSuccessStatusCode) return true;
            }
            catch (HttpRequestException) { }
            await Task.Delay(400, ct);
        }
        Stop();
        throw new TimeoutException("server start");
    }

    /// <summary>À appeler après chaque génération : le serveur s'arrêtera s'il n'est plus sollicité.</summary>
    public static void Release()
    {
        _idle?.Dispose();
        _idle = new Timer(_ => Stop(), null, TimeSpan.FromMinutes(3), Timeout.InfiniteTimeSpan); // plus de demande depuis 3 min : on rend la mémoire
    }

    private static string BaseUrl => $"http://127.0.0.1:{_port}/v1";

    public static void Stop()
    {
        try { if (_server is { HasExited: false }) _server.Kill(entireProcessTree: true); } catch { }
        _server?.Dispose();
        _server = null;
    }

    private static int FreePort()
    {
        var l = new TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        int p = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }
}

/// <summary>
/// Lie le serveur local à Kairn via un « job » Windows : si Kairn se ferme ou plante, Windows arrête le serveur aussi.
/// Pas de processus orphelin qui garderait 3 Go de mémoire.
/// </summary>
internal static class ChildProcessGuard
{
    [StructLayout(LayoutKind.Sequential)]
    private struct BASIC_LIMIT { public long PerProcessUserTimeLimit, PerJobUserTimeLimit; public uint LimitFlags; public UIntPtr MinWs, MaxWs; public uint ActiveProcessLimit; public UIntPtr Affinity; public uint PriorityClass, SchedulingClass; }
    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS { public ulong a, b, c, d, e, f; }
    [StructLayout(LayoutKind.Sequential)]
    private struct EXTENDED_LIMIT { public BASIC_LIMIT Basic; public IO_COUNTERS Io; public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed; }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateJobObject(IntPtr attrs, string? name);
    [DllImport("kernel32.dll")] private static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref EXTENDED_LIMIT info, int size);
    [DllImport("kernel32.dll")] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    private static readonly Lazy<IntPtr> Job = new(() =>
    {
        var job = CreateJobObject(IntPtr.Zero, null);
        var info = new EXTENDED_LIMIT { Basic = new BASIC_LIMIT { LimitFlags = 0x2000 } }; // KILL_ON_JOB_CLOSE
        SetInformationJobObject(job, 9, ref info, Marshal.SizeOf<EXTENDED_LIMIT>());
        return job;
    });

    public static void Attach(Process p)
    {
        try { AssignProcessToJobObject(Job.Value, p.Handle); } catch { }
    }
}
