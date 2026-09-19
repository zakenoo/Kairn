using System.Windows;
using System.Windows.Controls;
using Kairn.Services;
using Kairn.Services.Assistant;

namespace Kairn.Views;

/// <summary>Réglages de l'assistant d'objectifs : modèle local (intégré, Ollama, LM Studio) et mode en ligne facultatif.</summary>
public partial class SettingsView
{
    private static Models.AssistantSettings A => Storage.Settings.Assistant;
    private CancellationTokenSource? _installCts;

    private void BuildAssistant()
    {
        LocalSourceChips.Children.Clear();
        foreach (var (key, label) in new[] { ("builtin", L.T("set.ai.src.builtin")), ("ollama", "Ollama"), ("lmstudio", "LM Studio") })
        {
            var rb = new RadioButton { Content = label, GroupName = "aiSrc", IsChecked = A.LocalSource == key };
            rb.SetResourceReference(StyleProperty, "Chip");
            rb.Checked += (_, _) =>
            {
                if (A.LocalSource == key) return;
                A.LocalSource = key;
                A.LocalModel = null;
                Storage.SaveSettings();
                UpdateLocalPanels();
            };
            LocalSourceChips.Children.Add(rb);
        }
        GpuSwitch.IsChecked = A.UseGpu;
        UpdateLocalPanels();

        OnlineSwitch.IsChecked = A.OnlineEnabled;
        ProviderChips.Children.Clear();
        foreach (var (key, label) in new[] { ("claude", "Claude (Anthropic)"), ("openai", L.T("set.ai.provider.openai")) })
        {
            var rb = new RadioButton { Content = label, GroupName = "aiProv", IsChecked = A.OnlineProvider == key };
            rb.SetResourceReference(StyleProperty, "Chip");
            rb.Checked += (_, _) => { A.OnlineProvider = key; Storage.SaveSettings(); UpdateOnlinePanel(); };
            ProviderChips.Children.Add(rb);
        }
        ModelBox.Text = A.OnlineModel ?? "";
        BaseUrlBox.Text = A.OnlineBaseUrl ?? "";
        WebSearchSwitch.IsChecked = A.WebSearch;
        UpdateOnlinePanel();
    }

    // ===================== Local =====================

    private void UpdateLocalPanels()
    {
        bool builtin = A.LocalSource == "builtin";
        BuiltinPanel.Visibility = builtin ? Visibility.Visible : Visibility.Collapsed;
        ServerPanel.Visibility = builtin ? Visibility.Collapsed : Visibility.Visible;
        if (builtin) UpdateEngineStatus();
        else _ = DetectServerAsync();
    }

    private void UpdateEngineStatus()
    {
        bool installed = LocalEngine.IsInstalled;
        EngineStatus.Text = installed
            ? L.F("set.ai.installed", (LocalEngine.InstalledBytes / 1e9).ToString("0.0", L.Culture))
            : L.F("set.ai.notInstalled", LocalEngine.DownloadGb.ToString("0.0", L.Culture));
        InstallBtn.Visibility = installed || _installCts != null ? Visibility.Collapsed : Visibility.Visible;
        UninstallBtn.Visibility = installed && _installCts == null ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        _installCts = new CancellationTokenSource();
        InstallProgressPanel.Visibility = Visibility.Visible;
        UpdateEngineStatus();
        var progress = new Progress<(string Step, double Fraction)>(p =>
        {
            InstallProgress.Value = p.Fraction;
            InstallText.Text = L.F("set.ai.step." + p.Step, (int)(p.Fraction * 100));
        });
        try
        {
            await Task.Run(() => LocalEngine.InstallAsync(progress, _installCts.Token));
            InstallText.Text = "";
        }
        catch (OperationCanceledException) { InstallText.Text = ""; }
        catch (Exception ex)
        {
            MessageBox.Show(Window.GetWindow(this)!, L.T("set.ai.installFailed") + "\n\n" + ex.Message, "Kairn", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _installCts = null;
            InstallProgressPanel.Visibility = Visibility.Collapsed;
            UpdateEngineStatus();
        }
    }

    private void CancelInstall_Click(object sender, RoutedEventArgs e) => _installCts?.Cancel();

    private void Uninstall_Click(object sender, RoutedEventArgs e) => PlanningView.ConfirmTwice(UninstallBtn, () =>
    {
        LocalEngine.Uninstall();
        UpdateEngineStatus();
    });

    private void Gpu_Click(object sender, RoutedEventArgs e)
    {
        A.UseGpu = GpuSwitch.IsChecked == true;
        LocalEngine.Stop(); // redémarrera avec le bon réglage à la prochaine demande
        Storage.SaveSettings();
    }

    private async Task DetectServerAsync()
    {
        var name = A.LocalSource == "ollama" ? "Ollama" : "LM Studio";
        ServerStatus.Text = L.F("set.ai.server.checking", name);
        ServerModels.Children.Clear();
        var models = await GoalAssistant.DetectLocalServerAsync(A.LocalSource);
        if (models.Count == 0)
        {
            ServerStatus.Text = L.F("set.ai.server.none", name);
            return;
        }
        ServerStatus.Text = L.F("set.ai.server.found", name);
        if (A.LocalModel is null || !models.Contains(A.LocalModel)) { A.LocalModel = models[0]; Storage.SaveSettings(); }
        foreach (var m in models)
        {
            var rb = new RadioButton { Content = m, GroupName = "aiModel", IsChecked = m == A.LocalModel };
            rb.SetResourceReference(StyleProperty, "Chip");
            rb.Checked += (_, _) => { A.LocalModel = m; Storage.SaveSettings(); };
            ServerModels.Children.Add(rb);
        }
    }

    private async void RefreshServer_Click(object sender, RoutedEventArgs e) => await DetectServerAsync();

    // ===================== En ligne =====================

    private void UpdateOnlinePanel()
    {
        OnlinePanel.Visibility = A.OnlineEnabled ? Visibility.Visible : Visibility.Collapsed;
        bool claude = A.OnlineProvider != "openai";
        BaseUrlPanel.Visibility = claude ? Visibility.Collapsed : Visibility.Visible;
        WebSearchSwitch.Visibility = claude ? Visibility.Visible : Visibility.Collapsed;
        ModelBox.Tag = claude ? ClaudeLlm.DefaultModel : "gpt-4o-mini";
        bool hasKey = Secret.Unprotect(A.ProtectedKey ?? "") is { Length: > 0 };
        KeyStatus.Text = hasKey ? L.T("set.ai.key.saved") : L.T(claude ? "set.ai.key.none.claude" : "set.ai.key.none");
        ForgetKeyBtn.Visibility = hasKey ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Online_Click(object sender, RoutedEventArgs e)
    {
        A.OnlineEnabled = OnlineSwitch.IsChecked == true;
        Storage.SaveSettings();
        UpdateOnlinePanel();
    }

    private void SaveKey_Click(object sender, RoutedEventArgs e)
    {
        var key = KeyBox.Password.Trim();
        if (key.Length == 0) return;
        A.ProtectedKey = Secret.Protect(key); // chiffrée par Windows : lisible seulement par ce compte, sur ce PC
        KeyBox.Clear();
        Storage.SaveSettings();
        UpdateOnlinePanel();
    }

    private void ForgetKey_Click(object sender, RoutedEventArgs e)
    {
        A.ProtectedKey = null;
        Storage.SaveSettings();
        UpdateOnlinePanel();
    }

    private void OnlineField_LostFocus(object sender, RoutedEventArgs e)
    {
        A.OnlineModel = string.IsNullOrWhiteSpace(ModelBox.Text) ? null : ModelBox.Text.Trim();
        A.OnlineBaseUrl = string.IsNullOrWhiteSpace(BaseUrlBox.Text) ? null : BaseUrlBox.Text.Trim();
        Storage.SaveSettings();
    }

    private void WebSearch_Click(object sender, RoutedEventArgs e)
    {
        A.WebSearch = WebSearchSwitch.IsChecked == true;
        Storage.SaveSettings();
    }
}
