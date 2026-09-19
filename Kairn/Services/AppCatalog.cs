namespace Kairn.Services;

/// <summary>Une app connue que le garde peut surveiller.</summary>
/// <param name="Name">Nom affiché.</param>
/// <param name="Group">Famille (messagerie, jeux…).</param>
/// <param name="Processes">Noms des processus (sans .exe) à surveiller ou fermer.</param>
/// <param name="InstallNames">Débuts de noms dans la liste des programmes installés de Windows, pour la détecter.</param>
/// <param name="Note">Précision affichée sous le nom (facultatif).</param>
public record CatalogApp(string Name, string Group, string[] Processes, string[] InstallNames, string? Note = null);

/// <summary>Catalogue des distractions courantes. Chacun active seulement ce qui le concerne.</summary>
public static class AppCatalog
{
    // Clés de traduction (affichées via L.T)
    public const string Messaging = "guard.group.messaging";
    public const string Games = "guard.group.games";
    public const string Media = "guard.group.media";
    public const string Browsers = "guard.group.browsers";

    public static readonly string[] Groups = [Messaging, Games, Media, Browsers];

    public static readonly List<CatalogApp> Apps =
    [
        new("Discord", Messaging, ["Discord", "DiscordPTB", "DiscordCanary"], ["Discord"]),
        new("WhatsApp", Messaging, ["WhatsApp", "WhatsApp.Root"], ["WhatsApp"]),
        new("Telegram", Messaging, ["Telegram"], ["Telegram"]),
        new("Messenger", Messaging, ["Messenger"], ["Messenger"]),
        new("Signal", Messaging, ["Signal"], ["Signal"]),
        new("Slack", Messaging, ["slack"], ["Slack"], "guard.note.work"),
        new("Instagram", Messaging, ["Instagram"], ["Instagram"]),
        new("TikTok", Messaging, ["TikTok"], ["TikTok"]),

        new("Steam", Games, ["steam"], ["Steam"]),
        new("Epic Games", Games, ["EpicGamesLauncher"], ["Epic Games Launcher"]),
        new("Battle.net", Games, ["Battle.net"], ["Battle.net"]),
        new("Riot Client (LoL, Valorant)", Games, ["RiotClientServices", "RiotClientUx", "LeagueClient", "VALORANT"], ["Riot", "League of Legends", "VALORANT"]),
        new("EA app", Games, ["EADesktop", "Origin"], ["EA app", "Origin"]),
        new("Ubisoft Connect", Games, ["UbisoftConnect", "upc"], ["Ubisoft Connect", "Uplay"]),
        new("GOG Galaxy", Games, ["GalaxyClient"], ["GOG GALAXY"]),
        new("Xbox", Games, ["XboxPcApp", "XboxApp"], ["Xbox"]),
        new("Minecraft", Games, ["MinecraftLauncher", "Minecraft.Windows"], ["Minecraft"]),
        new("Roblox", Games, ["RobloxPlayerBeta", "RobloxPlayerLauncher"], ["Roblox"]),

        new("Spotify", Media, ["Spotify"], ["Spotify"], "guard.note.music"),
        new("Netflix", Media, ["Netflix"], ["Netflix"]),
        new("Twitch", Media, ["Twitch"], ["Twitch"]),
        new("Prime Video", Media, ["PrimeVideo"], ["Prime Video"]),
        new("VLC", Media, ["vlc"], ["VLC media player"]),

        new("Google Chrome", Browsers, ["chrome"], ["Google Chrome"], "guard.note.browser"),
        new("Firefox", Browsers, ["firefox"], ["Mozilla Firefox"], "guard.note.browser"),
        new("Microsoft Edge", Browsers, ["msedge"], ["Microsoft Edge"], "guard.note.browser"),
        new("Opera / Opera GX", Browsers, ["opera"], ["Opera"], "guard.note.browser"),
        new("Brave", Browsers, ["brave"], ["Brave"], "guard.note.browser"),
    ];

    /// <summary>L'app du catalogue qui correspond à ce nom de processus, s'il y en a une.</summary>
    public static CatalogApp? ForProcess(string process) =>
        Apps.FirstOrDefault(a => a.Processes.Any(p => p.Equals(process, StringComparison.OrdinalIgnoreCase)));
}
