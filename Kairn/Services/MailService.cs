using System.Net;
using System.Text.RegularExpressions;
using Kairn.Models;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Security;

namespace Kairn.Services;

/// <summary>Fournisseur de messagerie connu : serveur IMAP et aide pour créer un mot de passe d'application.</summary>
public record MailProvider(string Name, string[] Domains, string Host, int Port, string? Webmail, string? HelpUrl, string Hint, bool Supported = true);

public record MailSummary(uint Uid, string From, string FromAddress, string Subject, DateTimeOffset Date, string Preview, bool Seen);

/// <summary>
/// Lecture des mails en IMAP, directement entre ce PC et ton serveur mail. Aucun intermédiaire, aucun compte Kairn.
/// </summary>
public static class MailService
{
    public static readonly List<MailProvider> Providers =
    [
        new("Gmail", ["gmail.com", "googlemail.com"], "imap.gmail.com", 993, "https://mail.google.com", "https://myaccount.google.com/apppasswords",
            "mail.hint.gmail"),
        new("iCloud", ["icloud.com", "me.com", "mac.com"], "imap.mail.me.com", 993, "https://www.icloud.com/mail", "https://account.apple.com",
            "mail.hint.icloud"),
        new("Yahoo", ["yahoo.com", "yahoo.fr", "ymail.com"], "imap.mail.yahoo.com", 993, "https://mail.yahoo.com", "https://login.yahoo.com/account/security",
            "mail.hint.yahoo"),
        new("Orange", ["orange.fr", "wanadoo.fr"], "imap.orange.fr", 993, "https://messagerie.orange.fr", null, "mail.hint.usual"),
        new("Free", ["free.fr"], "imap.free.fr", 993, "https://webmail.free.fr", null, "mail.hint.usual"),
        new("SFR", ["sfr.fr", "neuf.fr"], "imap.sfr.fr", 993, "https://webmail.sfr.fr", null, "mail.hint.usual"),
        new("La Poste", ["laposte.net"], "imap.laposte.net", 993, "https://www.laposte.net", null, "mail.hint.usual"),
        new("GMX", ["gmx.fr", "gmx.com", "gmx.net"], "imap.gmx.net", 993, "https://www.gmx.fr", null, "mail.hint.gmx"),
        new("Outlook / Hotmail", ["outlook.com", "outlook.fr", "hotmail.com", "hotmail.fr", "live.com", "live.fr", "msn.com"], "outlook.office365.com", 993,
            "https://outlook.live.com", null,
            "mail.hint.outlook", Supported: false),
        new("Autre", [], "", 993, null, null, "mail.hint.other"),
    ];

    /// <summary>Messageries à ouvrir dans le navigateur (option sans mot de passe).</summary>
    public static readonly (string Name, string Url)[] Webmails =
    [
        ("Gmail", "https://mail.google.com"),
        ("Outlook", "https://outlook.live.com/mail"),
        ("iCloud", "https://www.icloud.com/mail"),
        ("Yahoo", "https://mail.yahoo.com"),
        ("Proton Mail", "https://mail.proton.me"),
        ("Orange", "https://messagerie.orange.fr"),
        ("Free", "https://webmail.free.fr"),
        ("SFR", "https://webmail.sfr.fr"),
        ("La Poste", "https://www.laposte.net"),
        ("GMX", "https://www.gmx.fr"),
    ];

    public static MailProvider Detect(string email)
    {
        var domain = email.Contains('@') ? email[(email.IndexOf('@') + 1)..].Trim().ToLowerInvariant() : "";
        return Providers.FirstOrDefault(p => p.Domains.Contains(domain)) ?? Providers[^1];
    }

    public static List<MailSummary> LastFetch { get; private set; } = [];
    public static DateTime LastFetchTime { get; private set; }
    public static int Unread => LastFetch.Count(m => !m.Seen);

    private static async Task<ImapClient> ConnectAsync(MailAccount a, string? password = null, CancellationToken ct = default)
    {
        var pwd = password ?? Secret.Unprotect(a.ProtectedPassword) ?? throw new AuthenticationException("mot de passe illisible");
        var client = new ImapClient { Timeout = 20000 };
        await client.ConnectAsync(a.Host, a.Port, a.Port == 993 ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTlsWhenAvailable, ct);
        await client.AuthenticateAsync(string.IsNullOrWhiteSpace(a.User) ? a.Email : a.User, pwd, ct);
        return client;
    }

    /// <summary>Teste la connexion. Renvoie null si tout va bien, sinon un message clair.</summary>
    public static async Task<string?> TestAsync(MailAccount a, string password)
    {
        try
        {
            using var client = await ConnectAsync(a, password);
            await client.Inbox.OpenAsync(FolderAccess.ReadOnly);
            await client.DisconnectAsync(true);
            return null;
        }
        catch (Exception ex) { return Explain(ex); }
    }

    /// <summary>Les derniers mails de la boîte de réception (plus récents d'abord).</summary>
    public static async Task<List<MailSummary>> FetchAsync(MailAccount a, int count = 20)
    {
        using var client = await ConnectAsync(a);
        var inbox = client.Inbox;
        await inbox.OpenAsync(FolderAccess.ReadOnly);
        var list = new List<MailSummary>();
        if (inbox.Count > 0)
        {
            int start = Math.Max(0, inbox.Count - count);
            var items = await inbox.FetchAsync(start, -1,
                MessageSummaryItems.UniqueId | MessageSummaryItems.Envelope | MessageSummaryItems.Flags | MessageSummaryItems.PreviewText);
            foreach (var m in items.Reverse())
            {
                var env = m.Envelope;
                var from = env?.From.Mailboxes.FirstOrDefault();
                list.Add(new MailSummary(
                    m.UniqueId.Id,
                    string.IsNullOrWhiteSpace(from?.Name) ? from?.Address ?? "?" : from!.Name,
                    from?.Address ?? "",
                    string.IsNullOrWhiteSpace(env?.Subject) ? L.T("mail.noSubject") : env.Subject,
                    env?.Date ?? m.InternalDate ?? DateTimeOffset.Now,
                    Clean(m.PreviewText ?? ""),
                    m.Flags?.HasFlag(MessageFlags.Seen) ?? true));
            }
        }
        await client.DisconnectAsync(true);
        LastFetch = list;
        LastFetchTime = DateTime.Now;
        return list;
    }

    /// <summary>Contenu d'un mail en texte, et le marque comme lu (comme n'importe quel logiciel de mail).</summary>
    public static async Task<string> ReadAsync(MailAccount a, uint uid)
    {
        using var client = await ConnectAsync(a);
        var inbox = client.Inbox;
        await inbox.OpenAsync(FolderAccess.ReadWrite);
        var id = new UniqueId(uid);
        var msg = await inbox.GetMessageAsync(id);
        await inbox.AddFlagsAsync(id, MessageFlags.Seen, true);
        await client.DisconnectAsync(true);

        LastFetch = LastFetch.Select(m => m.Uid == uid ? m with { Seen = true } : m).ToList();
        var text = msg.TextBody;
        if (string.IsNullOrWhiteSpace(text) && msg.HtmlBody is { } html) text = HtmlToText(html);
        return string.IsNullOrWhiteSpace(text) ? L.T("mail.noText") : text.Trim();
    }

    public static string Explain(Exception ex) => ex switch
    {
        AuthenticationException => L.T("mail.err.auth"),
        System.Net.Sockets.SocketException or TimeoutException => L.T("mail.err.network"),
        SslHandshakeException => L.T("mail.err.ssl"),
        OperationCanceledException => L.T("mail.err.slow"),
        _ => L.F("mail.err.other", ex.Message)
    };

    private static string Clean(string s) => Regex.Replace(s, @"\s+", " ").Trim();

    /// <summary>Version texte d'un mail HTML (pas de moteur web : on garde le texte et les sauts de ligne).</summary>
    public static string HtmlToText(string html)
    {
        var s = Regex.Replace(html, @"<(style|script|head)[^>]*>.*?</\1>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"<br\s*/?>|</p>|</div>|</tr>|</h\d>|</li>", "\n", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"<li[^>]*>", "• ", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"<[^>]+>", "");
        s = WebUtility.HtmlDecode(s);
        s = Regex.Replace(s, @"[ \t ]+", " ");
        s = Regex.Replace(s, @"\n\s*\n\s*\n+", "\n\n");
        return s.Trim();
    }
}
