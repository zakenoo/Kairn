using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Kairn.Models;
using Kairn.Services;

namespace Kairn.Views;

public partial class SettingsView : UserControl, IRefreshable
{
    public SettingsView()
    {
        InitializeComponent();
    }

    private static AppSettings S => Storage.Settings;

    public void Refresh()
    {
        BuildLanguages();
        NameBox.Text = S.UserName ?? "";

        BuildRhythm();
        BuildAssistant();
        BuildUpdates();
        StartupSwitch.IsChecked = S.StartWithWindows;
        TraySwitch.IsChecked = S.CloseToTray;
        DataPath.Text = Storage.Root;
    }

    // ===================== Langue =====================

    private void BuildLanguages()
    {
        LanguageChips.Children.Clear();
        foreach (var lang in Loc.Languages)
        {
            var rb = new RadioButton
            {
                Content = lang.NativeName, GroupName = "lang", IsChecked = lang.Code == Loc.Instance.Current.Code,
                FlowDirection = lang.Rtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight
            };
            rb.SetResourceReference(StyleProperty, "Chip");
            rb.Checked += (_, _) =>
            {
                if (lang.Code == Loc.Instance.Current.Code) return;
                S.Language = lang.Code;
                Storage.SaveSettings();
                // Après le clic : changer de langue recrée cette vue, on ne le fait pas en plein milieu de son propre événement.
                Dispatcher.BeginInvoke(() => Loc.Instance.Load(lang.Code));
            };
            LanguageChips.Children.Add(rb);
        }
    }

    private void OpenLang_Click(object sender, RoutedEventArgs e)
    {
        Launcher.Open(Loc.PrepareUserLangDir());
    }

    private void Save()
    {
        Storage.SaveSettings();
        App.Current.MainWin?.RefreshGuard();
    }

    private void NameBox_LostFocus(object sender, RoutedEventArgs e)
    {
        var n = NameBox.Text.Trim();
        S.UserName = n.Length == 0 ? null : n;
        Save();
    }

    // ===================== Rythme de travail =====================

    private static readonly int[] RhythmThresholds = [45, 60, 90, 120];

    private void BuildRhythm()
    {
        AutoRhythmChips.Children.Clear();
        AutoRhythmChips.Children.Add(Chip("autoRhythm", L.T("rhythm.none"), S.AutoRhythm is null, () => S.AutoRhythm = null));
        foreach (var p in Rhythm.Presets)
            AutoRhythmChips.Children.Add(Chip("autoRhythm", L.T(p.Key), S.AutoRhythm == p.Code, () => S.AutoRhythm = p.Code));

        AutoRhythmMinChips.Children.Clear();
        foreach (var m in RhythmThresholds)
            AutoRhythmMinChips.Children.Add(Chip("autoRhythmMin", PlanTask.FormatDuration(TimeSpan.FromMinutes(m)),
                S.AutoRhythmMinMinutes == m, () => S.AutoRhythmMinMinutes = m));
        AutoRhythmMinRow.Visibility = S.AutoRhythm is null ? Visibility.Collapsed : Visibility.Visible;
        RhythmSoundSwitch.IsChecked = S.RhythmSound;

        RadioButton Chip(string group, string text, bool isChecked, Action apply)
        {
            var rb = new RadioButton { Content = text, GroupName = group, IsChecked = isChecked };
            rb.SetResourceReference(StyleProperty, "Chip");
            rb.Checked += (_, _) =>
            {
                apply();
                Storage.SaveSettings();
                AutoRhythmMinRow.Visibility = S.AutoRhythm is null ? Visibility.Collapsed : Visibility.Visible;
            };
            return rb;
        }
    }

    private void RhythmSound_Click(object sender, RoutedEventArgs e)
    {
        S.RhythmSound = RhythmSoundSwitch.IsChecked == true;
        Save();
    }

    // ===================== Mises à jour =====================

    private void BuildUpdates()
    {
        VersionLabel.Text = L.F("set.updates.version", Installer.VersionText(Installer.CurrentVersion));
        UpdatesSwitch.IsChecked = S.CheckUpdates;
        ShowUpdate(Updater.Available, silentIfNone: true);
    }

    private void ShowUpdate(Updater.Update? u, bool silentIfNone)
    {
        UpdateStatus.Text = u is not null ? L.F("set.updates.found", Installer.VersionText(u.Version)) : silentIfNone ? "" : L.T("set.updates.upToDate");
        InstallUpdateBtn.Visibility = u is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void InstallUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (Updater.Available is not { } u) return;
        InstallUpdateBtn.IsEnabled = false;
        try
        {
            await Updater.DownloadAndApplyAsync(u, new Progress<double>(f => UpdateStatus.Text = L.F("update.downloading", (int)(f * 100))), CancellationToken.None);
            App.Current.Quit(); // le setup remplace Kairn puis le relance
        }
        catch
        {
            UpdateStatus.Text = L.T("update.failed");
            InstallUpdateBtn.IsEnabled = true;
        }
    }

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        UpdateStatus.Text = "…";
        try
        {
            ShowUpdate(await Updater.CheckAsync(manual: true), silentIfNone: false);
        }
        catch { UpdateStatus.Text = L.T("set.updates.error"); }
    }

    private void UpdatesSwitch_Click(object sender, RoutedEventArgs e)
    {
        S.CheckUpdates = UpdatesSwitch.IsChecked == true;
        Save();
        if (S.CheckUpdates) Updater.StartAutoCheck();
    }

    // ===================== Système =====================

    private void StartupSwitch_Click(object sender, RoutedEventArgs e)
    {
        S.StartWithWindows = StartupSwitch.IsChecked == true;
        StartupService.Apply(S.StartWithWindows);
        Save();
    }

    private void TraySwitch_Click(object sender, RoutedEventArgs e)
    {
        S.CloseToTray = TraySwitch.IsChecked == true;
        Save();
    }

    private void OpenData_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo(Storage.Root) { UseShellExecute = true });
}
