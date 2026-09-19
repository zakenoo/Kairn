using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using Kairn.Models;
using Kairn.Services;

namespace Kairn.Views;

/// <summary>Accueil, colonne latérale : outils de travail, mails, et boutons des liens de la tâche en cours.</summary>
public partial class TodayView
{
    private readonly DispatcherTimer _mailTimer = new() { Interval = TimeSpan.FromMinutes(5) };
    private bool _toolsManage;
    private bool _mailFetching;
    private bool _mailShowAnyway;
    private string? _mailBlockId;
    private MailSummary? _reading;

    private static AppSettings S => Storage.Settings;

    private void InitSide()
    {
        _mailTimer.Tick += (_, _) => FetchMail(force: true);
        IsVisibleChanged += (_, _) => { if (IsVisible) _mailTimer.Start(); else _mailTimer.Stop(); };
    }

    private void RefreshSide()
    {
        RefreshTools();
        RefreshMail();
        FetchMail(force: false);
    }

    // ===================== Mise en page : 2 colonnes si la place le permet =====================

    private void Layout_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        bool wide = e.NewSize.Width >= 940;
        SideCol.Width = wide ? new GridLength(310) : new GridLength(0);
        GapCol.Width = wide ? new GridLength(24) : new GridLength(0);
        Grid.SetColumn(Side, wide ? 2 : 0);
        Grid.SetRow(Side, wide ? 0 : 1);
        Side.Margin = wide ? new Thickness(0) : new Thickness(0, 28, 0, 0);
    }

    // ===================== Liens de la tâche en cours =====================

    private void ShowTaskLinks(PlanTask? task)
    {
        NowLinks.Children.Clear();
        if (task is null || task.Links.Count == 0) { NowLinks.Visibility = Visibility.Collapsed; return; }
        bool first = true;
        foreach (var link in task.Links)
        {
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            var icon = PlanningView.LinkIcon(link.Target, 16);
            content.Children.Add(icon);
            content.Children.Add(new TextBlock { Text = L.F("link.open", link.Title), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
            var b = new Button { Content = content, Margin = new Thickness(0, 0, 8, 8), Padding = new Thickness(14, 8, 14, 8), ToolTip = link.Target };
            // Le premier lien est mis en avant : c'est « le » bouton pour se lancer.
            if (first) b.SetResourceReference(StyleProperty, "Primary");
            if (first && icon is TextBlock glyph) glyph.SetResourceReference(TextBlock.ForegroundProperty, "OnAccentBrush");
            first = false;
            b.Click += (_, _) => Launcher.Open(link.Target);
            NowLinks.Children.Add(b);
        }
        NowLinks.Visibility = Visibility.Visible;
    }

    // ===================== Mes outils =====================

    private void RefreshTools()
    {
        ToolsPanel.Children.Clear();
        foreach (var tool in S.Tools) ToolsPanel.Children.Add(ToolTile(tool));
        ToolsEmpty.Visibility = S.Tools.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ToolsManager.Visibility = _toolsManage ? Visibility.Visible : Visibility.Collapsed;
        ToolsManageBtn.Content = L.T(_toolsManage ? "tools.done" : S.Tools.Count == 0 ? "tools.addShort" : "tools.manage");
    }

    private FrameworkElement ToolTile(Tool tool)
    {
        var icon = PlanningView.LinkIcon(tool.Target, 30);
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        var name = new TextBlock
        {
            Text = tool.Name, FontSize = 11.5, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis, MaxHeight = 32, Margin = new Thickness(0, 6, 0, 0)
        };
        var tile = new Border
        {
            Width = 84, Padding = new Thickness(6, 10, 6, 8), Margin = new Thickness(0, 0, 6, 6), CornerRadius = new CornerRadius(10),
            Cursor = Cursors.Hand, ToolTip = L.F(_toolsManage ? "tools.remove" : "tools.open", tool.Name),
            Child = new StackPanel { Children = { icon, name } }, Background = System.Windows.Media.Brushes.Transparent
        };
        tile.MouseEnter += (_, _) => tile.SetResourceReference(Border.BackgroundProperty, "HoverBrush");
        tile.MouseLeave += (_, _) => tile.Background = System.Windows.Media.Brushes.Transparent;
        Click.Attach(tile, () =>
        {
            if (_toolsManage) { S.Tools.Remove(tool); Storage.SaveSettings(); RefreshTools(); }
            else Launcher.Open(tool.Target);
        });
        if (!_toolsManage) return tile;

        var x = new TextBlock { Text = "", FontSize = 9, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 4, 10, 0), IsHitTestVisible = false };
        x.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
        x.SetResourceReference(TextBlock.ForegroundProperty, "DangerBrush");
        return new Grid { Children = { tile, x } };
    }

    private void ToolsManage_Click(object sender, RoutedEventArgs e)
    {
        _toolsManage = !_toolsManage;
        ToolSearch.Text = "";
        ToolUrl.Text = "";
        ToolResults.Children.Clear();
        RefreshTools();
        if (_toolsManage) ToolSearch.Focus();
    }

    private void AddTool(string name, string target)
    {
        if (S.Tools.Any(t => t.Target.Equals(target, StringComparison.OrdinalIgnoreCase))) return;
        S.Tools.Add(new Tool { Name = name, Target = target });
        Storage.SaveSettings();
        RefreshTools();
    }

    private void ToolSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        ToolResults.Children.Clear();
        var q = ToolSearch.Text.Trim();
        if (q.Length < 2) return;
        var matches = Launcher.StartMenuApps().Where(a => a.Name.Contains(q, StringComparison.OrdinalIgnoreCase)).Take(6).ToList();
        foreach (var (name, path) in matches)
        {
            var row = new DockPanel();
            var icon = PlanningView.LinkIcon(path, 18);
            icon.Margin = new Thickness(0, 0, 8, 0);
            DockPanel.SetDock(icon, Dock.Left);
            var plus = new TextBlock { Text = "", FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
            plus.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
            plus.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
            DockPanel.SetDock(plus, Dock.Right);
            row.Children.Add(icon);
            row.Children.Add(plus);
            row.Children.Add(new TextBlock { Text = name, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
            var b = new Button { Content = row, HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(8, 6, 8, 6), Margin = new Thickness(0, 0, 0, 4) };
            b.SetResourceReference(StyleProperty, "Ghost");
            b.Click += (_, _) => AddTool(name, path);
            ToolResults.Children.Add(b);
        }
        if (matches.Count == 0)
        {
            var none = new TextBlock { Text = L.T("tools.none"), FontSize = 12, TextWrapping = TextWrapping.Wrap };
            none.SetResourceReference(StyleProperty, "Subtle");
            ToolResults.Children.Add(none);
        }
    }

    private void ToolUrl_Click(object sender, RoutedEventArgs e)
    {
        var target = Launcher.Normalize(ToolUrl.Text);
        if (target.Length == 0) return;
        AddTool(Launcher.NameFor(target), target);
        ToolUrl.Text = "";
    }

    private void ToolUrl_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) ToolUrl_Click(sender, e);
    }

    private void ToolExe_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = L.T("dlg.programs") + "|*.exe;*.lnk|" + L.T("dlg.allFiles") + "|*.*",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)
        };
        if (dlg.ShowDialog() == true) AddTool(Launcher.NameFor(dlg.FileName), dlg.FileName);
    }

    // ===================== Mails =====================

    private bool InWorkBlock(out PlanTask? task)
    {
        task = FocusGuard.CurrentTask();
        return task is { IsBreak: false, Done: false };
    }

    private void RefreshMail()
    {
        var acc = S.Mail;
        bool linked = acc != null;
        bool webOnly = !linked && S.Webmail != null;
        MailSetup.Visibility = linked || webOnly ? Visibility.Collapsed : Visibility.Visible;
        MailWeb.Visibility = webOnly ? Visibility.Visible : Visibility.Collapsed;
        MailActions.Visibility = linked ? Visibility.Visible : Visibility.Collapsed;
        if (webOnly)
        {
            WebOpenText.Text = L.F("mail.web.open", S.Webmail!.Name == "ma messagerie" ? L.T("mail.web.mine") : S.Webmail.Name);
            WebFocusText.Text = InWorkBlock(out _)
                ? L.T("mail.web.focus")
                : L.T("mail.web.info");
        }

        int unread = MailService.Unread;
        UnreadBadge.Visibility = linked && unread > 0 ? Visibility.Visible : Visibility.Collapsed;
        UnreadText.Text = unread > 99 ? "99+" : unread.ToString();

        if (!linked)
        {
            MailFocus.Visibility = MailReader.Visibility = MailList.Visibility = Visibility.Collapsed;
            return;
        }

        // Pendant un bloc de travail : juste le compteur. Le contenu attendra la pause.
        bool working = InWorkBlock(out var task);
        if (task?.Id != _mailBlockId) { _mailBlockId = task?.Id; _mailShowAnyway = false; }
        bool hide = working && S.HideMailDuringWork && !_mailShowAnyway;

        MailFocus.Visibility = hide ? Visibility.Visible : Visibility.Collapsed;
        MailFocusText.Text = MailService.LastFetchTime == default ? L.T("mail.focus.never")
            : unread == 0 ? L.T("mail.focus.none")
            : L.P("mail.focus.unread", unread);

        MailReader.Visibility = !hide && _reading != null ? Visibility.Visible : Visibility.Collapsed;
        MailList.Visibility = !hide && _reading == null ? Visibility.Visible : Visibility.Collapsed;
        MailList.ItemsSource = MailService.LastFetch.Take(8).ToList();
    }

    private async void FetchMail(bool force)
    {
        var acc = S.Mail;
        if (acc is null || _mailFetching) return;
        if (!force && DateTime.Now - MailService.LastFetchTime < TimeSpan.FromMinutes(3)) return;
        _mailFetching = true;
        if (MailService.LastFetchTime == default) SetMailStatus(L.T("mail.loading"));
        try
        {
            await Task.Run(() => MailService.FetchAsync(acc));
            SetMailStatus(null);
        }
        catch (Exception ex) { SetMailStatus(MailService.Explain(ex)); }
        finally { _mailFetching = false; }
        RefreshMail();
    }

    private void SetMailStatus(string? text)
    {
        MailStatus.Text = text ?? "";
        MailStatus.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void MailRefresh_Click(object sender, RoutedEventArgs e) => FetchMail(force: true);

    private void MailShowAnyway_Click(object sender, RoutedEventArgs e)
    {
        _mailShowAnyway = true;
        RefreshMail();
    }

    private void MailMenu_Click(object sender, RoutedEventArgs e)
    {
        var acc = S.Mail;
        if (acc is null) return;
        var menu = new ContextMenu { PlacementTarget = MailMenuBtn, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        var header = new MenuItem { Header = acc.Email, IsEnabled = false };
        menu.Items.Add(header);
        if (acc.WebmailUrl != null)
        {
            var web = new MenuItem { Header = L.T("mail.menu.webmail") };
            web.Click += (_, _) => Launcher.Open(acc.WebmailUrl);
            menu.Items.Add(web);
        }
        var hide = new MenuItem { Header = L.T("mail.menu.hide"), IsCheckable = true, IsChecked = S.HideMailDuringWork };
        hide.Click += (_, _) => { S.HideMailDuringWork = hide.IsChecked; Storage.SaveSettings(); RefreshMail(); };
        menu.Items.Add(hide);
        menu.Items.Add(new Separator());
        var remove = new MenuItem { Header = L.T("mail.menu.disconnect") };
        remove.Click += (_, _) =>
        {
            S.Mail = null; // le mot de passe chiffré part avec
            Storage.SaveSettings();
            _reading = null;
            RefreshMail();
        };
        menu.Items.Add(remove);
        menu.IsOpen = true;
    }

    // ---- Lecture ----

    private void MailItem_Down(object sender, MouseButtonEventArgs e) => Click.Down(sender);

    private async void MailItem_Up(object sender, MouseButtonEventArgs e)
    {
        if (!Click.Up(sender) || sender is not FrameworkElement { Tag: MailSummary m } || S.Mail is not { } acc) return;
        _reading = m;
        ReadSubject.Text = m.Subject;
        ReadFrom.Text = $"{m.From} <{m.FromAddress}> · {m.Date.ToString("dddd d MMMM, HH:mm", L.Culture)}";
        ReadBody.Text = L.T("mail.loading");
        RefreshMail();
        try { ReadBody.Text = await Task.Run(() => MailService.ReadAsync(acc, m.Uid)); }
        catch (Exception ex) { ReadBody.Text = MailService.Explain(ex); }
        RefreshMail();
    }

    private void MailBack_Click(object sender, RoutedEventArgs e)
    {
        _reading = null;
        RefreshMail();
    }

    private void MailWebmail_Click(object sender, RoutedEventArgs e)
    {
        if (S.Mail?.WebmailUrl is { } url) { Launcher.Open(url); return; }
        if (_reading is { } m)
            Launcher.Open($"mailto:{m.FromAddress}?subject={Uri.EscapeDataString("Re: " + m.Subject)}");
    }

    // ---- Messagerie dans le navigateur (sans mot de passe) ----

    private void WebChoose_Click(object sender, RoutedEventArgs e)
    {
        if (WebChoices.Visibility == Visibility.Visible) { WebChoices.Visibility = Visibility.Collapsed; return; }
        WebList.Children.Clear();
        // Si l'adresse est déjà tapée, sa messagerie passe en premier.
        var guess = MailService.Detect(MailEmail.Text.Trim()).Name;
        var ordered = MailService.Webmails.OrderByDescending(w => guess.StartsWith(w.Name, StringComparison.OrdinalIgnoreCase));
        foreach (var (name, url) in ordered)
        {
            var b = new Button { Content = name, Margin = new Thickness(0, 0, 6, 6), Padding = new Thickness(12, 6, 12, 6), ToolTip = url };
            b.Click += (_, _) => SetWebmail(name, url);
            WebList.Children.Add(b);
        }
        WebChoices.Visibility = Visibility.Visible;
    }

    private void SetWebmail(string name, string url)
    {
        S.Webmail = new WebmailLink { Name = name, Url = url };
        Storage.SaveSettings();
        WebChoices.Visibility = Visibility.Collapsed;
        RefreshMail();
    }

    private void WebCustom_Click(object sender, RoutedEventArgs e)
    {
        var url = Launcher.Normalize(WebCustom.Text);
        if (!Launcher.IsUrl(url)) return;
        SetWebmail("ma messagerie", url);
        WebCustom.Text = "";
    }

    private void WebCustom_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) WebCustom_Click(sender, e);
    }

    private void WebOpen_Click(object sender, RoutedEventArgs e)
    {
        if (S.Webmail is { } w) Launcher.Open(w.Url);
    }

    private void WebChange_Click(object sender, RoutedEventArgs e)
    {
        S.Webmail = null;
        Storage.SaveSettings();
        RefreshMail();
    }

    // ---- Liaison du compte ----

    private MailProvider _provider = MailService.Providers[^1];

    private void MailEmail_TextChanged(object sender, TextChangedEventArgs e)
    {
        var email = MailEmail.Text.Trim();
        _provider = MailService.Detect(email);
        bool hasDomain = email.Contains('@') && email.IndexOf('@') < email.Length - 1;
        MailHintBox.Visibility = hasDomain ? Visibility.Visible : Visibility.Collapsed;
        MailHint.Text = _provider.Name == "Autre" ? L.T(_provider.Hint) : L.F("mail.hint", _provider.Name, L.T(_provider.Hint));
        MailHelpBtn.Visibility = _provider.HelpUrl != null ? Visibility.Visible : Visibility.Collapsed;
        MailServerBox.Visibility = hasDomain && _provider.Name == "Autre" ? Visibility.Visible : Visibility.Collapsed;
        if (_provider.Name == "Autre" && hasDomain && MailHost.Text.Length == 0)
            MailHost.Text = "imap." + email[(email.IndexOf('@') + 1)..];
        MailConnectBtn.IsEnabled = _provider.Supported;
    }

    private void MailHelp_Click(object sender, RoutedEventArgs e)
    {
        if (_provider.HelpUrl is { } url) Launcher.Open(url);
    }

    private void MailPassword_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) MailConnect_Click(sender, e);
    }

    private async void MailConnect_Click(object sender, RoutedEventArgs e)
    {
        var email = MailEmail.Text.Trim();
        var pwd = MailPassword.Password;
        if (!email.Contains('@') || pwd.Length == 0) { SetMailStatus(L.T("mail.err.fields")); return; }

        bool custom = _provider.Name == "Autre";
        var acc = new MailAccount
        {
            Email = email,
            User = email,
            Host = custom ? MailHost.Text.Trim() : _provider.Host,
            Port = custom && int.TryParse(MailPort.Text, out var port) ? port : _provider.Port,
            WebmailUrl = _provider.Webmail
        };
        if (acc.Host.Length == 0) { SetMailStatus(L.T("mail.err.host")); return; }

        MailConnectBtn.IsEnabled = false;
        MailConnectBtn.Content = L.T("mail.connecting");
        SetMailStatus(null);
        var error = await Task.Run(() => MailService.TestAsync(acc, pwd));
        MailConnectBtn.IsEnabled = true;
        MailConnectBtn.Content = L.T("mail.connect");
        if (error != null) { SetMailStatus(error); return; }

        acc.ProtectedPassword = Secret.Protect(pwd);
        MailPassword.Clear();
        S.Mail = acc;
        Storage.SaveSettings();
        RefreshMail();
        FetchMail(force: true);
    }
}
