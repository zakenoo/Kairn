using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Microsoft.Win32;
using Kairn.Models;
using Kairn.Services;

namespace Kairn.Views;

public partial class LibraryView : UserControl, IRefreshable
{
    private static readonly string[] ImageExt = [".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".tif", ".tiff"];

    private Category? _current;
    private ResourceKind? _filter;
    private ResourceItem? _editingItem;
    private string _editColor = "#D6A461";

    public LibraryView()
    {
        InitializeComponent();
        Focusable = true;
        MouseDown += (_, _) => Focus();
        PreviewKeyDown += (_, e) =>
        {
            // Ctrl+V avec une image dans le presse-papier : on l'ajoute directement.
            if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control && Clipboard.ContainsImage())
            {
                PasteImage();
                e.Handled = true;
            }
        };
    }

    public void Refresh()
    {
        _current ??= Storage.Data.Categories.FirstOrDefault();
        if (_current != null && !Storage.Data.Categories.Contains(_current)) _current = Storage.Data.Categories.FirstOrDefault();
        BuildCategoryList();
        ShowCategory();
    }

    // ===================== Catégories =====================

    private void BuildCategoryList()
    {
        CategoryList.Children.Clear();
        foreach (var (c, depth) in Storage.CategoryTree())
        {
            var rb = new RadioButton { GroupName = "cats", IsChecked = c == _current, Tag = c };
            rb.SetResourceReference(StyleProperty, "NavItem");
            // Sous-catégorie : décalée, avec un petit trait de rattachement à la place de la pastille.
            FrameworkElement marker = depth == 0
                ? new Ellipse { Width = 8, Height = 8, Fill = new SolidColorBrush(ThemeService.Parse(c.Color, "#D6A461")), Margin = new Thickness(0, 0, 10, 0) }
                : new Border { Width = 8, Height = 1.5, Background = new SolidColorBrush(ThemeService.Parse(c.Color, "#D6A461")), Margin = new Thickness(0, 0, 10, 0) };
            var total = Storage.WithDescendants(c).Sum(x => x.Items.Count);
            var count = new TextBlock { Text = total.ToString(), FontSize = 12 };
            count.SetResourceReference(TextBlock.ForegroundProperty, "FaintBrush");
            var row = new DockPanel();
            DockPanel.SetDock(count, Dock.Right);
            row.Children.Add(count);
            row.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(depth * 16, 0, 0, 0),
                Children = { marker, new TextBlock { Text = $"{c.Emoji}  {c.Name}", TextTrimming = TextTrimming.CharacterEllipsis, FontSize = depth == 0 ? 13.5 : 13 } }
            });
            rb.Content = row;
            rb.Width = 250 - 8;
            rb.Checked += (_, _) => { _current = c; CloseEditors(); ShowCategory(); };

            // Clic droit : actions de la catégorie sans passer par l'en-tête.
            var menu = new ContextMenu();
            menu.Items.Add(MenuItemFor(L.T("lib.sub.new"), "", () => { Select(c); AddSubCategory_Click(this, new RoutedEventArgs()); }));
            menu.Items.Add(MenuItemFor(L.T("lib.customize"), "", () => { Select(c); EditCategory_Click(this, new RoutedEventArgs()); }));
            rb.ContextMenu = menu;
            CategoryList.Children.Add(rb);
        }
    }

    private void Select(Category c)
    {
        _current = c;
        CloseEditors();
        Refresh();
    }

    private static MenuItem MenuItemFor(string text, string glyph, Action onClick)
    {
        var icon = new TextBlock { Text = glyph, FontSize = 12 };
        icon.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
        var item = new MenuItem { Header = text, Icon = icon };
        item.Click += (_, _) => onClick();
        return item;
    }

    private void ShowCategory()
    {
        bool has = _current != null;
        Detail.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        NoCategory.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
        if (_current is null) return;

        CatEmoji.Text = _current.Emoji;
        CatName.Text = _current.Name;
        var linked = Storage.Data.Tasks.Count(t => t.CategoryId == _current.Id);
        CatStats.Text = L.F("lib.stats", L.P("lib.items", _current.Items.Count), L.P("lib.tasks", linked));
        BuildBreadcrumb();
        BuildSubTiles();

        var items = _current.Items.Where(i => _filter is null || i.Kind == _filter).OrderByDescending(i => i.Added).ToList();
        Items.ItemsSource = items;
        EmptyText.Visibility = items.Count == 0 && SubsPanel.Visibility != Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>« Dessin › » au-dessus du nom d'une sous-catégorie : chaque parent est cliquable.</summary>
    private void BuildBreadcrumb()
    {
        CatParents.Inlines.Clear();
        var parents = new List<Category>();
        for (var p = Storage.ParentOf(_current!); p != null && !parents.Contains(p); p = Storage.ParentOf(p)) parents.Insert(0, p);
        CatParents.Visibility = parents.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var p in parents)
        {
            var link = new System.Windows.Documents.Run($"{p.Emoji} {p.Name}") { Cursor = Cursors.Hand };
            link.MouseLeftButtonUp += (_, _) => Select(p);
            CatParents.Inlines.Add(link);
            CatParents.Inlines.Add(new System.Windows.Documents.Run("  ›  "));
        }
    }

    /// <summary>Les sous-catégories de la catégorie affichée, comme des dossiers.</summary>
    private void BuildSubTiles()
    {
        Subs.Children.Clear();
        foreach (var sub in Storage.ChildrenOf(_current))
        {
            var total = Storage.WithDescendants(sub).Sum(x => x.Items.Count);
            var name = new TextBlock { Text = sub.Name, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 150 };
            var count = new TextBlock { Text = L.P("lib.items", total), FontSize = 11.5 };
            count.SetResourceReference(TextBlock.ForegroundProperty, "SubtleBrush");
            var bar = new Border { Width = 4, CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 0, 12, 0),
                                   Background = new SolidColorBrush(ThemeService.Parse(sub.Color, "#D6A461")) };
            var emoji = new TextBlock { Text = sub.Emoji, FontSize = 20, FontFamily = new FontFamily("Segoe UI Emoji"),
                                        VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
            var content = new StackPanel { Orientation = Orientation.Horizontal, Children = { bar, emoji, new StackPanel { Children = { name, count } } } };
            var tile = new Button { Content = content, Margin = new Thickness(0, 0, 10, 10), Padding = new Thickness(10, 10, 16, 10),
                                    HorizontalContentAlignment = HorizontalAlignment.Left, MinWidth = 180 };
            tile.Click += (_, _) => Select(sub);
            Subs.Children.Add(tile);
        }
        SubsPanel.Visibility = Subs.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void AddSubCategory_Click(object sender, RoutedEventArgs e)
    {
        if (_current is null) return;
        var sub = new Category { Name = L.T("lib.sub.new"), Emoji = "📂", Color = _current.Color, ParentId = _current.Id };
        Storage.Data.Categories.Add(sub);
        Storage.Save();
        _current = sub;
        Refresh();
        EditCategory_Click(sender, e);
        EdName.SelectAll();
    }

    private void AddCategory_Click(object sender, RoutedEventArgs e)
    {
        var color = ThemeService.AccentPresets[Storage.Data.Categories.Count % ThemeService.AccentPresets.Length];
        var cat = new Category { Name = L.T("lib.newCategory"), Emoji = "✨", Color = color };
        Storage.Data.Categories.Add(cat);
        Storage.Save();
        _current = cat;
        Refresh();
        EditCategory_Click(sender, e);
        EdName.SelectAll();
    }

    private void EditCategory_Click(object sender, RoutedEventArgs e)
    {
        if (_current is null) return;
        CatEditor.Visibility = Visibility.Visible;
        EdEmoji.Text = _current.Emoji;
        EdName.Text = _current.Name;
        _editColor = _current.Color;
        BuildColorSwatches();
        BuildParentChips();
        EdName.Focus();
    }

    /// <summary>Où ranger la catégorie : principale, ou dans une autre (pas dans elle-même ni dans ses propres sous-catégories).</summary>
    private void BuildParentChips()
    {
        EdParent.Children.Clear();
        var excluded = Storage.WithDescendants(_current!).ToHashSet();
        var parent = Storage.ParentOf(_current!);
        EdParent.Children.Add(Chip(L.T("lib.parent.none"), null, parent is null));
        foreach (var (c, _) in Storage.CategoryTree().Where(x => !excluded.Contains(x.Cat)))
            EdParent.Children.Add(Chip($"{c.Emoji} {Storage.CategoryPath(c)}", c.Id, c == parent));

        RadioButton Chip(string text, string? id, bool isChecked)
        {
            var rb = new RadioButton { Content = text, Tag = id, GroupName = "catParent", IsChecked = isChecked, Padding = new Thickness(0) };
            rb.SetResourceReference(StyleProperty, "Chip");
            return rb;
        }
    }

    private void BuildColorSwatches()
    {
        EdColors.Children.Clear();
        foreach (var hex in ThemeService.AccentPresets)
            EdColors.Children.Add(Swatch(hex, hex.Equals(_editColor, StringComparison.OrdinalIgnoreCase), () =>
            {
                _editColor = hex;
                BuildColorSwatches();
            }));
    }

    /// <summary>Pastille de couleur cliquable (réutilisée par les Réglages).</summary>
    public static FrameworkElement Swatch(string hex, bool selected, Action onClick)
    {
        var inner = new Ellipse { Width = 22, Height = 22, Fill = new SolidColorBrush(ThemeService.Parse(hex, "#888888")) };
        var ring = new Ellipse { Width = 30, Height = 30, StrokeThickness = 2 };
        if (selected) ring.SetResourceReference(Shape.StrokeProperty, "TextBrush");
        var g = new Grid { Width = 32, Height = 32, Margin = new Thickness(0, 0, 6, 6), Cursor = Cursors.Hand, Background = Brushes.Transparent, ToolTip = hex };
        g.Children.Add(ring);
        g.Children.Add(inner);
        Click.Attach(g, onClick);
        return g;
    }

    private void SaveCategory_Click(object sender, RoutedEventArgs e)
    {
        if (_current is null) return;
        _current.Name = string.IsNullOrWhiteSpace(EdName.Text) ? L.T("lib.noName") : EdName.Text.Trim();
        _current.Emoji = string.IsNullOrWhiteSpace(EdEmoji.Text) ? "📁" : EdEmoji.Text.Trim();
        _current.Color = _editColor;
        _current.ParentId = EdParent.Children.OfType<RadioButton>().FirstOrDefault(r => r.IsChecked == true)?.Tag as string;
        Storage.Save();
        LibraryFiles.Sync(); // le dossier sur le disque prend le nouveau nom / la nouvelle place
        CatEditor.Visibility = Visibility.Collapsed;
        Refresh();
    }

    private void CancelCategory_Click(object sender, RoutedEventArgs e) => CatEditor.Visibility = Visibility.Collapsed;

    private void DeleteCategory_Click(object sender, RoutedEventArgs e) => PlanningView.ConfirmTwice(DeleteCatBtn, () =>
    {
        if (_current is null) return;
        foreach (var t in Storage.Data.Tasks.Where(t => t.CategoryId == _current.Id)) t.CategoryId = null;
        // Ses sous-catégories ne disparaissent pas avec elle : elles remontent d'un cran.
        foreach (var child in Storage.ChildrenOf(_current).ToList()) child.ParentId = _current.ParentId;
        var parentAfter = Storage.ParentOf(_current);
        // Ses propres fichiers partent avec elle ; ceux des sous-catégories suivront leur nouveau dossier (Sync).
        foreach (var item in _current.Items.Where(i => i.Kind is ResourceKind.Image or ResourceKind.File)) Storage.DeleteLibraryFile(item.Content);
        Storage.Data.Categories.Remove(_current);
        Storage.Save();
        LibraryFiles.Sync();
        _current = parentAfter; // on revient au parent plutôt qu'à la première catégorie
        CatEditor.Visibility = Visibility.Collapsed;
        Refresh();
    });

    // ===================== Ajout d'éléments =====================

    private void AddBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) AddText_Click(sender, e);
    }

    private void AddText_Click(object sender, RoutedEventArgs e)
    {
        var text = AddBox.Text.Trim();
        if (_current is null || text.Length == 0) return;

        // Plusieurs liens collés d'un coup : un élément par lien.
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.All(IsUrl))
            foreach (var url in lines) AddItem(new ResourceItem { Kind = ResourceKind.Link, Content = url, Title = LinkTitle(url) });
        else
        {
            var first = lines[0];
            AddItem(new ResourceItem { Kind = ResourceKind.Note, Title = first.Length > 60 ? first[..60] + "…" : first, Content = text });
        }
        AddBox.Text = "";
    }

    private static bool IsUrl(string s) =>
        Uri.TryCreate(s, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps);

    /// <summary>Titre lisible sans aller sur Internet (aucune requête réseau).</summary>
    private static string LinkTitle(string url)
    {
        var u = new Uri(url);
        var host = u.Host.Replace("www.", "");
        if (host.Contains("youtu")) return L.T("link.youtube");
        if (host.Contains("pinterest") || host.Contains("pin.it")) return L.T("link.pinterest");
        var path = Uri.UnescapeDataString(u.AbsolutePath.Trim('/'));
        var last = path.Split('/').LastOrDefault(p => p.Length > 0);
        return string.IsNullOrEmpty(last) ? host : $"{host} · {last.Replace('-', ' ')}";
    }

    private void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Multiselect = true, Filter = L.T("dlg.allFiles") + "|*.*|" + L.T("dlg.images") + "|*.png;*.jpg;*.jpeg;*.gif;*.webp;*.bmp" };
        if (dlg.ShowDialog() == true) ImportFiles(dlg.FileNames);
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetDataPresent(DataFormats.Text)
            ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files) ImportFiles(files);
        else if (e.Data.GetData(DataFormats.Text) is string text && IsUrl(text.Trim()))
            AddItem(new ResourceItem { Kind = ResourceKind.Link, Content = text.Trim(), Title = LinkTitle(text.Trim()) });
    }

    private void ImportFiles(IEnumerable<string> paths)
    {
        if (_current is null) return;
        foreach (var p in paths.Where(File.Exists))
        {
            try
            {
                var rel = LibraryFiles.Import(_current, p);
                bool img = ImageExt.Contains(System.IO.Path.GetExtension(p).ToLowerInvariant());
                AddItem(new ResourceItem
                {
                    Kind = img ? ResourceKind.Image : ResourceKind.File,
                    Content = rel,
                    Title = System.IO.Path.GetFileNameWithoutExtension(p)
                }, save: false);
            }
            catch { /* fichier inaccessible */ }
        }
        Storage.Save();
        Refresh();
    }

    private void PasteImage()
    {
        if (_current is null || !Clipboard.ContainsImage()) return;
        // Pas Clipboard.GetImage() tel quel : les captures Windows y arrivent entièrement transparentes.
        var img = ImageFix.FromClipboard();
        if (img is null) return;
        var tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"Capture {DateTime.Now:yyyy-MM-dd HH.mm.ss}.png");
        using (var fs = File.Create(tmp))
        {
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(img));
            enc.Save(fs);
        }
        ImportFiles([tmp]);
        try { File.Delete(tmp); } catch { }
    }

    private void AddItem(ResourceItem item, bool save = true)
    {
        _current?.Items.Add(item);
        if (!save) return;
        Storage.Save();
        Refresh();
    }

    // ===================== Consultation / édition =====================

    private void Filter_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string tag }) return;
        _filter = Enum.TryParse<ResourceKind>(tag, out var k) ? k : null;
        if (IsLoaded) ShowCategory();
    }

    private void Press(object sender, MouseButtonEventArgs e) => Click.Down(sender);

    private void Item_Click(object sender, MouseButtonEventArgs e)
    {
        if (!Click.Up(sender) || sender is not FrameworkElement { Tag: ResourceItem item }) return;
        // Clic simple : ouvre le lien / fichier. Les notes, et Ctrl+clic sur le reste, ouvrent l'édition.
        if (item.Kind == ResourceKind.Note || Keyboard.Modifiers == ModifierKeys.Control) EditItem(item);
        else Open(item);
    }

    private void EditItem(ResourceItem item)
    {
        _editingItem = item;
        ItTitle.Text = item.Title;
        ItContent.Text = item.Content;
        bool editable = item.Kind is ResourceKind.Note or ResourceKind.Link;
        ItContent.IsReadOnly = !editable;
        ItContentLabel.Text = L.T(item.Kind switch { ResourceKind.Link => "lib.kind.address", ResourceKind.Note => "lib.kind.noteLabel", _ => "lib.kind.file" });
        ItOpen.Visibility = item.Kind == ResourceKind.Note ? Visibility.Collapsed : Visibility.Visible;
        ItemEditor.Visibility = Visibility.Visible;
        Dispatcher.BeginInvoke(() => ItemEditor.BringIntoView(), System.Windows.Threading.DispatcherPriority.Loaded);
        ItTitle.Focus();
    }

    private void ItemSave_Click(object sender, RoutedEventArgs e)
    {
        if (_editingItem is null) return;
        _editingItem.Title = ItTitle.Text.Trim();
        if (!ItContent.IsReadOnly) _editingItem.Content = ItContent.Text;
        Storage.Save();
        CloseEditors();
        ShowCategory();
    }

    private void ItemDelete_Click(object sender, RoutedEventArgs e)
    {
        if (_editingItem is { } item) DeleteItem(item);
    }

    // ===================== Actions rapides sur une carte =====================

    private void ItemEdit_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ResourceItem item }) EditItem(item);
    }

    private void ItemQuickDelete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ResourceItem item }) DeleteItem(item);
    }

    /// <summary>Supprime tout de suite, avec quelques secondes pour annuler. Le fichier n'est effacé qu'ensuite.</summary>
    private void DeleteItem(ResourceItem item)
    {
        var cat = Storage.Data.Categories.FirstOrDefault(c => c.Items.Contains(item));
        if (cat is null) return;
        int index = cat.Items.IndexOf(item);
        cat.Items.Remove(item);
        Storage.Save();
        CloseEditors();
        Refresh();
        Undo.Show(L.F("undo.deleted", item.Title),
            undo: () =>
            {
                if (!Storage.Data.Categories.Contains(cat)) return; // catégorie supprimée entre-temps
                cat.Items.Insert(Math.Min(index, cat.Items.Count), item);
                Storage.Save();
                Refresh();
            },
            commit: () => { if (item.Kind is ResourceKind.Image or ResourceKind.File) Storage.DeleteLibraryFile(item.Content); });
    }

    private void MoveItem(ResourceItem item, Category target)
    {
        var from = Storage.Data.Categories.FirstOrDefault(c => c.Items.Contains(item));
        if (from is null || from == target) return;
        from.Items.Remove(item);
        // Le fichier suit l'élément : supprimer l'ancienne catégorie ne l'emportera pas.
        if (item.Kind is ResourceKind.Image or ResourceKind.File)
            try { item.Content = LibraryFiles.Place(item.Content, target); } catch { /* fichier ouvert ailleurs : il reste où il est */ }
        target.Items.Add(item);
        Storage.Save();
        Refresh();
    }

    /// <summary>Clic droit sur une carte : ouvrir, renommer, déplacer, supprimer.</summary>
    private void Item_ContextMenu(object sender, ContextMenuEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ResourceItem item, ContextMenu: { } menu }) return;
        menu.Items.Clear();
        if (item.Kind != ResourceKind.Note) menu.Items.Add(MenuItemFor(L.T("common.open"), "", () => Open(item)));
        menu.Items.Add(MenuItemFor(L.T("lib.item.edit"), "", () => EditItem(item)));

        var move = new MenuItem { Header = L.T("lib.item.move") };
        var moveIcon = new TextBlock { Text = "", FontSize = 12 };
        moveIcon.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
        move.Icon = moveIcon;
        foreach (var (c, depth) in Storage.CategoryTree())
        {
            var target = c;
            var mi = MenuItemFor(new string(' ', depth * 4) + $"{c.Emoji} {c.Name}", "", () => MoveItem(item, target));
            mi.IsEnabled = !c.Items.Contains(item);
            move.Items.Add(mi);
        }
        menu.Items.Add(move);
        menu.Items.Add(new Separator());
        var del = MenuItemFor(L.T("common.delete"), "", () => DeleteItem(item));
        del.SetResourceReference(ForegroundProperty, "DangerBrush");
        menu.Items.Add(del);
    }

    private void ItemOpen_Click(object sender, RoutedEventArgs e)
    {
        if (_editingItem != null) Open(_editingItem);
    }

    private void ItemClose_Click(object sender, RoutedEventArgs e) => CloseEditors();

    private void CloseEditors()
    {
        ItemEditor.Visibility = Visibility.Collapsed;
        CatEditor.Visibility = Visibility.Collapsed;
        _editingItem = null;
    }

    /// <summary>Ouvre un lien dans le navigateur, un fichier avec l'app par défaut, ou une note dans la bibliothèque.</summary>
    public static void Open(ResourceItem item)
    {
        try
        {
            switch (item.Kind)
            {
                case ResourceKind.Link when IsUrl(item.Content):
                    Process.Start(new ProcessStartInfo(item.Content) { UseShellExecute = true });
                    break;
                case ResourceKind.Image:
                case ResourceKind.File:
                    var path = Storage.AbsoluteLibraryPath(item.Content);
                    if (File.Exists(path)) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                    break;
                default:
                    App.Current.MainWin?.Navigate("library");
                    break;
            }
        }
        catch { }
    }
}
