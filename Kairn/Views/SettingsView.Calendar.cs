using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Kairn.Models;
using Kairn.Services;

namespace Kairn.Views;

/// <summary>
/// Relier son calendrier iCloud, et choisir ce qui circule dans chaque sens.
/// Rien n'est coché d'avance, rien ne part tant qu'on n'a pas demandé, et « Délier » ramène
/// Kairn exactement là où il était : les rendez-vous importés disparaissent d'ici, et rien n'est touché chez Apple.
/// </summary>
public partial class SettingsView
{
    private List<RemoteCalendar> _calendars = [];

    private void BuildCalendar()
    {
        bool linked = CalendarSync.Linked;
        CalSetup.Visibility = linked ? Visibility.Collapsed : Visibility.Visible;
        CalLinked.Visibility = linked ? Visibility.Visible : Visibility.Collapsed;
        ShowCalStatus(CalendarSync.Status, error: true);
        if (!linked || S.Calendar is not { } a) return;

        // Au redémarrage, la liste des calendriers n'est plus en mémoire : on la redemande,
        // sinon la page affiche « aucun calendrier » alors que le compte est bien relié.
        if (_calendars.Count == 0) _ = ReloadCalendarsAsync();

        CalAccount.Text = a.User;
        CalLastSync.Text = CalendarSync.Busy ? L.T("set.cal.syncing")
            : a.LastSync == default ? L.T("set.cal.never")
            : L.F("set.cal.lastSync", a.LastSync.ToString("HH:mm", L.Culture));
        CalPushSwitch.IsChecked = a.Push;
        CalLogBtn.Visibility = Visibility.Visible;
        BuildCalendarChips();

        // Relier un compte ne suffit pas : sans calendrier coché et sans l'envoi, rien ne circule.
        // Le dire clairement, sinon on croit que la synchro marche alors qu'elle ne fait rien.
        bool idle = a.Read.Count == 0 && !a.Push;
        CalIdle.Visibility = idle ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task ReloadCalendarsAsync()
    {
        if (S.Calendar is not { } a) return;
        try
        {
            _calendars = await CalDav.ListAsync(a, CancellationToken.None);
            BuildCalendarChips();
        }
        catch (CalDavException ex) { ShowCalStatus(L.T(ex.Key), error: true); }
        catch { ShowCalStatus(L.T("cal.err.network"), error: true); }
    }

    private void BuildCalendarChips()
    {
        if (S.Calendar is not { } a) return;
        CalList.Children.Clear();
        foreach (var cal in _calendars)
        {
            // Le calendrier que Kairn s'est créé n'est pas à « afficher » : son contenu est déjà là.
            if (cal.Name == CalDav.OwnCalendarName) continue;
            var cb = new CheckBox { Content = cal.Name, Tag = cal.Href, IsChecked = a.Read.Contains(cal.Href), Padding = new Thickness(0) };
            cb.SetResourceReference(StyleProperty, "ChipCheck");
            cb.Click += (_, _) =>
            {
                a.Read = [.. CalList.Children.OfType<CheckBox>().Where(c => c.IsChecked == true).Select(c => (string)c.Tag!)];
                Storage.SaveSettings();
                _ = CalendarSync.SyncAsync();
            };
            CalList.Children.Add(cb);
        }
        if (CalList.Children.Count == 0)
            CalList.Children.Add(new TextBlock { Text = L.T("set.cal.none"), Foreground = (System.Windows.Media.Brush)FindResource("FaintBrush"), FontSize = 12.5 });
    }

    private void ShowCalStatus(string message, bool error)
    {
        CalStatus.Text = message;
        CalStatus.Visibility = message.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        CalStatus.SetResourceReference(ForegroundProperty, error ? "DangerBrush" : "SubtleBrush");
    }

    private void CalAppPassword_Click(object sender, RoutedEventArgs e) =>
        Launcher.Open("https://account.apple.com/account/manage");

    private void CalPassword_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) CalLink_Click(sender, e);
    }

    private async void CalLink_Click(object sender, RoutedEventArgs e)
    {
        var user = CalUser.Text.Trim();
        var password = CalPassword.Password;
        if (user.Length == 0 || password.Length == 0) { ShowCalStatus(L.T("set.cal.err.empty"), error: true); return; }

        CalLinkBtn.IsEnabled = false;
        ShowCalStatus(L.T("set.cal.connecting"), error: false);
        var account = new CalendarAccount { User = user, ProtectedPassword = Secret.Protect(password) };
        try
        {
            account.HomeUrl = await CalDav.FindHomeAsync(account, CancellationToken.None);
            _calendars = await CalDav.ListAsync(account, CancellationToken.None);
            // Rien n'est suivi d'office : c'est à la personne de dire quels calendriers elle accepte de partager.
            S.Calendar = account;
            Storage.SaveSettings();
            CalPassword.Clear();
            ShowCalStatus(L.T("set.cal.linked"), error: false);
            BuildCalendar();
        }
        catch (CalDavException ex) { ShowCalStatus(L.T(ex.Key), error: true); }
        catch { ShowCalStatus(L.T("cal.err.network"), error: true); }
        finally { CalLinkBtn.IsEnabled = true; }
    }

    private async void CalRefresh_Click(object sender, RoutedEventArgs e)
    {
        if (S.Calendar is null) return;
        await ReloadCalendarsAsync();
        await CalendarSync.SyncAsync();
        BuildCalendar();
    }

    /// <summary>Ouvre le journal de synchronisation : de quoi voir ce qu'Apple a réellement répondu.</summary>
    private void CalLog_Click(object sender, RoutedEventArgs e)
    {
        if (System.IO.File.Exists(CalDavLog.Path)) Launcher.Open(CalDavLog.Path);
        else ShowCalStatus(L.T("set.cal.log.empty"), error: false);
    }

    private void CalUnlink_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b) return;
        PlanningView.ConfirmTwice(b, () =>
        {
            CalendarSync.Unlink();
            _calendars = [];
            BuildCalendar();
        });
    }

    private async void CalPush_Click(object sender, RoutedEventArgs e)
    {
        if (S.Calendar is not { } a) return;
        bool on = CalPushSwitch.IsChecked == true;
        CalPushSwitch.IsEnabled = false;
        try
        {
            if (on)
            {
                // Le calendrier « Kairn » est créé maintenant : c'est le seul endroit où l'app écrira.
                ShowCalStatus(L.T("set.cal.creating"), error: false);
                a.WriteHref = await CalDav.EnsureOwnCalendarAsync(a, CancellationToken.None);
                a.Push = true;
                Storage.SaveSettings();
                ShowCalStatus(L.T("set.cal.push.on"), error: false);
                await CalendarSync.SyncAsync();
            }
            else
            {
                // On décoche : Kairn retire ce qu'il avait posé, et laisse le calendrier vide derrière lui.
                a.Push = false;
                Storage.SaveSettings();
                await CalendarSync.ClearPushedAsync();
                ShowCalStatus(L.T("set.cal.push.off"), error: false);
            }
        }
        catch (CalDavException ex) { CalPushSwitch.IsChecked = a.Push; ShowCalStatus(L.T(ex.Key), error: true); }
        catch { CalPushSwitch.IsChecked = a.Push; ShowCalStatus(L.T("cal.err.network"), error: true); }
        finally { CalPushSwitch.IsEnabled = true; }
    }
}
