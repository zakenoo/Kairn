using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace Kairn.Models;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>Une tâche planifiée sur une plage horaire d'un jour donné.</summary>
public class PlanTask : Observable
{
    private string _title = "";
    private string _notes = "";
    private bool _done;
    private bool _isBreak;
    private TimeSpan _start;
    private TimeSpan _end;
    private string? _categoryId;
    private string? _section;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateOnly Date { get; set; }

    public TimeSpan Start { get => _start; set { if (Set(ref _start, value)) OnTimeChanged(); } }
    public TimeSpan End { get => _end; set { if (Set(ref _end, value)) OnTimeChanged(); } }
    public string Title { get => _title; set => Set(ref _title, value); }
    public string Notes { get => _notes; set { if (Set(ref _notes, value)) OnPropertyChanged(nameof(HasNotes)); } }
    public bool Done { get => _done; set => Set(ref _done, value); }
    public bool IsBreak { get => _isBreak; set { if (Set(ref _isBreak, value)) OnPropertyChanged(nameof(RhythmBadge)); } }
    public string? CategoryId { get => _categoryId; set => Set(ref _categoryId, value); }
    /// <summary>Titre de section facultatif (ex : « Après-midi : Échauffement »).</summary>
    public string? Section { get => _section; set => Set(ref _section, value); }

    /// <summary>Ce qu'il faut ouvrir pour cette tâche : un outil, un site (Canva, une vidéo YouTube…), un fichier.</summary>
    public List<TaskLink> Links { get; set; } = [];
    /// <summary>Ouvre les liens tout seuls au début du bloc.</summary>
    public bool AutoOpen { get; set; }
    /// <summary>Séance créée par l'assistant pour cet objectif.</summary>
    public string? GoalId { get; set; }
    [JsonIgnore] public bool HasLinks => Links.Count > 0;

    private string? _rhythm;
    /// <summary>
    /// Rythme de travail (« 25/5/15/4 » = Pomodoro). Null : rythme automatique des tâches longues (réglages),
    /// « off » : pas de rythme pour cette tâche.
    /// </summary>
    public string? Rhythm { get => _rhythm; set { if (Set(ref _rhythm, value)) OnPropertyChanged(nameof(RhythmBadge)); } }
    [JsonIgnore] public string RhythmBadge => Services.Rhythm.Badge(this);

    private bool _floating;
    private DateOnly? _carriedFrom;

    /// <summary>
    /// Tâche « à reprendre » : sans horaire fixe, à caser quand on peut.
    /// Start/End sont conservés pour garder la durée prévue.
    /// </summary>
    public bool Floating
    {
        get => _floating;
        set { if (Set(ref _floating, value)) { OnPropertyChanged(nameof(SlotText)); OnPropertyChanged(nameof(RhythmBadge)); } }
    }

    /// <summary>Jour d'origine si la tâche a été reportée.</summary>
    public DateOnly? CarriedFrom
    {
        get => _carriedFrom;
        set { if (Set(ref _carriedFrom, value)) OnPropertyChanged(nameof(CarriedText)); }
    }

    [JsonIgnore] public bool HasNotes => !string.IsNullOrWhiteSpace(Notes);
    [JsonIgnore] public string TimeRange => $"{Fmt(Start)} – {Fmt(End)}";
    [JsonIgnore] public string SlotText => Floating ? Services.L.T("task.carry.slot") : TimeRange;
    [JsonIgnore] public string CarriedText => CarriedFrom switch
    {
        { } d when d == Date => Services.L.F("task.carry.earlierToday", DurationText),
        { } d => Services.L.F("task.carry.from", d.ToString("dddd d", Services.L.Culture), DurationText),
        _ => $"~{DurationText}"
    };
    [JsonIgnore] public TimeSpan Duration => End > Start ? End - Start : End + TimeSpan.FromDays(1) - Start;
    [JsonIgnore] public string DurationText => FormatDuration(Duration);

    // États « live » calculés par la vue Aujourd'hui
    private bool _isCurrent;
    private bool _isPast;
    [JsonIgnore] public bool IsCurrent { get => _isCurrent; set => Set(ref _isCurrent, value); }
    [JsonIgnore] public bool IsPast { get => _isPast; set => Set(ref _isPast, value); }

    private void OnTimeChanged()
    {
        OnPropertyChanged(nameof(TimeRange));
        OnPropertyChanged(nameof(Duration));
        OnPropertyChanged(nameof(DurationText));
        OnPropertyChanged(nameof(SlotText));
        OnPropertyChanged(nameof(CarriedText));
        OnPropertyChanged(nameof(RhythmBadge));
    }

    public static string Fmt(TimeSpan t) => $"{(int)t.TotalHours % 24:00}:{t.Minutes:00}";

    public static string FormatDuration(TimeSpan d)
    {
        if (d.TotalMinutes < 60) return Services.L.F("dur.min", (int)d.TotalMinutes);
        return d.Minutes == 0 ? Services.L.F("dur.h", (int)d.TotalHours) : Services.L.F("dur.hmin", (int)d.TotalHours, d.Minutes.ToString("00"));
    }

    public PlanTask Clone(DateOnly date) => new()
    {
        Date = date, Start = Start, End = End, Title = Title, Notes = Notes,
        IsBreak = IsBreak, CategoryId = CategoryId, Section = Section,
        Links = Links.Select(l => new TaskLink { Title = l.Title, Target = l.Target }).ToList(), AutoOpen = AutoOpen, Rhythm = Rhythm, GoalId = GoalId
    };
}

/// <summary>Un lien attaché à une tâche : URL, chemin d'un programme / fichier, ou raccourci.</summary>
public class TaskLink
{
    public string Title { get; set; } = "";
    public string Target { get; set; } = "";
}

/// <summary>Un outil de travail lancé depuis l'accueil (programme, raccourci du menu Démarrer ou site web).</summary>
public class Tool
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Target { get; set; } = "";
}

/// <summary>Messagerie ouverte dans le navigateur (aucune connexion faite par Kairn).</summary>
public class WebmailLink
{
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
}

/// <summary>Boîte mail liée (IMAP). Le mot de passe est chiffré par Windows (DPAPI) pour ce seul compte Windows.</summary>
public class MailAccount
{
    public string Email { get; set; } = "";
    public string Host { get; set; } = "";
    public int Port { get; set; } = 993;
    public string User { get; set; } = "";
    public string ProtectedPassword { get; set; } = "";
    public string? WebmailUrl { get; set; }
}

public enum ResourceKind { Link, Note, Image, File }

/// <summary>Un élément rangé dans une catégorie : lien, note, image ou fichier.</summary>
public class ResourceItem : Observable
{
    private string _title = "";
    private string _content = "";

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public ResourceKind Kind { get; set; }
    public string Title { get => _title; set => Set(ref _title, value); }
    /// <summary>URL, texte de la note, ou chemin relatif dans la bibliothèque.</summary>
    public string Content { get => _content; set => Set(ref _content, value); }
    public DateTime Added { get; set; } = DateTime.Now;
}

public class Category : Observable
{
    private string _name = "";
    private string _color = "#F2F2F2";
    private string _emoji = "📁";

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get => _name; set => Set(ref _name, value); }
    public string Color { get => _color; set => Set(ref _color, value); }
    public string Emoji { get => _emoji; set => Set(ref _emoji, value); }
    /// <summary>Catégorie parente (sous-catégorie, ex : Dessin › Anatomie). Null = catégorie principale.</summary>
    public string? ParentId { get; set; }
    public ObservableCollection<ResourceItem> Items { get; set; } = [];
}

public enum FocusMode
{
    Off,
    /// <summary>Un bandeau rappelle la tâche en cours si une app bloquée passe au premier plan.</summary>
    Gentle,
    /// <summary>Les apps bloquées sont fermées au début de chaque bloc de travail.</summary>
    CloseAtStart,
    /// <summary>Les apps bloquées sont refermées dès qu'elles sont relancées pendant un bloc de travail.</summary>
    Strict
}

public class AppSettings
{
    /// <summary>Thème actif (copie modifiable d'un thème préfait ou perso).</summary>
    public ThemeDef Theme { get; set; } = Services.ThemePresets.Default();
    /// <summary>Thèmes enregistrés par l'utilisateur.</summary>
    public List<ThemeDef> CustomThemes { get; set; } = [];
    public bool StartWithWindows { get; set; } = true;
    public bool CloseToTray { get; set; } = true;
    public FocusMode FocusMode { get; set; } = FocusMode.Gentle;
    /// <summary>Processus surveillés (sans .exe). Source de vérité pour le garde.</summary>
    public List<string> BlockedApps { get; set; } = ["Discord"];
    /// <summary>Apps ajoutées à la main (hors catalogue), gardées même quand elles sont désactivées.</summary>
    public List<CustomApp> CustomApps { get; set; } = [];
    /// <summary>Rappel quand n'importe quelle fenêtre passe en plein écran (jeux lancés depuis un launcher…).</summary>
    public bool WatchFullscreen { get; set; }
    public int DayStartHour { get; set; } = 6;
    /// <summary>Outils de travail affichés sur l'accueil.</summary>
    public List<Tool> Tools { get; set; } = [];
    public MailAccount? Mail { get; set; }
    /// <summary>Alternative sans mot de passe : l'accueil ouvre simplement la messagerie dans le navigateur.</summary>
    public WebmailLink? Webmail { get; set; }
    /// <summary>Pendant un bloc de travail, n'affiche que le nombre de mails non lus.</summary>
    public bool HideMailDuringWork { get; set; } = true;
    public string? UserName { get; set; }
    /// <summary>Code de langue (« fr », « en »…). Null = langue de Windows au premier lancement.</summary>
    public string? Language { get; set; }
    /// <summary>Rythme appliqué tout seul aux tâches longues (« 25/5/15/4 »…). Null = aucun.</summary>
    public string? AutoRhythm { get; set; }
    /// <summary>Durée à partir de laquelle une tâche est « longue ».</summary>
    public int AutoRhythmMinMinutes { get; set; } = 60;
    /// <summary>Petit son quand vient l'heure de la pause ou de reprendre.</summary>
    public bool RhythmSound { get; set; } = true;
    public AssistantSettings Assistant { get; set; } = new();
}

/// <summary>Une app ajoutée à la main dans l'onglet Garde.</summary>
public class CustomApp
{
    public string Name { get; set; } = "";
    public string Process { get; set; } = "";
    public string? ExePath { get; set; }
}

public class AppData
{
    public List<PlanTask> Tasks { get; set; } = [];
    public List<Category> Categories { get; set; } = [];
    public List<Goal> Goals { get; set; } = [];
}
