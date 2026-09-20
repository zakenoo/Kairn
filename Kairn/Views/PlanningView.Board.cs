using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Kairn.Models;
using Kairn.Services;

namespace Kairn.Views;

/// <summary>
/// Le tableau : trois colonnes, on déplace une carte d'une colonne à l'autre et c'est tout.
/// Aucun horaire à saisir, aucun formulaire : quand écrire « 14h30 – 15h15 » est déjà trop d'effort,
/// il reste possible d'avancer quand même.
/// </summary>
public partial class PlanningView
{
    private enum Column { Later, Planned, Done }

    private void BuildBoard()
    {
        BoardColumns.Children.Clear();
        // Le tableau ne montre que ce qui t'appartient : un rendez-vous iCloud ne se déplace pas d'une colonne à l'autre.
        var all = Storage.TasksFor(_selected).Where(t => !t.IsBreak && !t.IsExternal).ToList();
        AddColumn(Column.Later, "board.later", "board.later.sub", all.Where(t => t.Floating && !t.Done));
        AddColumn(Column.Planned, "board.planned", "board.planned.sub", all.Where(t => !t.Floating && !t.Done));
        AddColumn(Column.Done, "board.done", "board.done.sub", all.Where(t => t.Done));
    }

    private void AddColumn(Column column, string titleKey, string subKey, IEnumerable<PlanTask> tasks)
    {
        var list = tasks.ToList();
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = L.T(titleKey) + (list.Count > 0 ? $"  {list.Count}" : ""),
            Style = (Style)FindResource("Label"),
            Margin = new Thickness(2, 0, 0, 2)
        });
        var sub = new TextBlock { Text = L.T(subKey), FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 0, 0, 10) };
        sub.SetResourceReference(TextBlock.ForegroundProperty, "FaintBrush");
        stack.Children.Add(sub);

        foreach (var task in list) stack.Children.Add(BoardCard(task, column));

        if (list.Count == 0)
        {
            var empty = new Border { Height = 64, CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1) };
            empty.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
            empty.Child = new TextBlock
            {
                Text = L.T("board.empty"), FontSize = 11.5, HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center, Foreground = (Brush)FindResource("FaintBrush")
            };
            stack.Children.Add(empty);
        }

        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = stack, Padding = new Thickness(0, 0, 8, 0) };
        var pane = new Border { Margin = new Thickness(0, 0, 10, 0), Padding = new Thickness(12, 12, 6, 12), CornerRadius = new CornerRadius(8), AllowDrop = true, Child = scroll };
        pane.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");

        pane.DragOver += (_, e) =>
        {
            e.Effects = TaskDrag.From(e.Data) is null ? DragDropEffects.None : DragDropEffects.Move;
            if (e.Effects != DragDropEffects.None) pane.SetResourceReference(Border.BackgroundProperty, "HoverBrush");
            e.Handled = true;
        };
        pane.DragLeave += (_, _) => pane.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
        pane.Drop += (_, e) =>
        {
            pane.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
            if (TaskDrag.From(e.Data) is not { } moved) return;
            e.Handled = true;
            MoveTo(moved, column);
            Refresh();
        };
        BoardColumns.Children.Add(pane);
    }

    /// <summary>Changer de colonne, c'est changer d'état : rien de plus n'est demandé.</summary>
    private void MoveTo(PlanTask task, Column column)
    {
        task.Date = _selected;
        switch (column)
        {
            case Column.Later:
                task.Done = false;
                task.Floating = true; // « à reprendre » : elle garde sa durée, elle perd son horaire
                break;

            case Column.Planned:
                task.Done = false;
                if (task.Floating)
                {
                    // On lui donne le créneau qui suit la dernière tâche du jour, sans rien demander.
                    var last = Storage.TasksFor(_selected).LastOrDefault(t => !t.Floating && t != task);
                    var start = last?.End ?? new TimeSpan(Math.Max(DateTime.Now.Hour + 1, 9) % 24, 0, 0);
                    var length = task.Duration;
                    task.Floating = false;
                    task.Start = start;
                    task.End = start + length;
                }
                break;

            case Column.Done:
                task.Done = true;
                Reminders.Cancel(task);
                Celebrate.Task();
                break;
        }
        task.CarriedFrom = null;
        Storage.Save();
    }

    private FrameworkElement BoardCard(PlanTask task, Column column)
    {
        var head = new StackPanel { Orientation = Orientation.Horizontal };
        if (task.HasEmoji)
            head.Children.Add(new TextBlock { Text = task.Emoji, FontFamily = new FontFamily("Segoe UI Emoji"), FontSize = 14, Margin = new Thickness(0, 0, 7, 0) });
        var title = new TextBlock { Text = task.Title, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        if (task.Done) title.TextDecorations = TextDecorations.Strikethrough;
        head.Children.Add(title);

        var stack = new StackPanel();
        stack.Children.Add(head);

        var meta = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 0) };
        var slot = new TextBlock { Text = task.SlotText, FontSize = 11.5 };
        slot.SetResourceReference(TextBlock.ForegroundProperty, "SubtleBrush");
        meta.Children.Add(slot);
        if (task.HasSteps)
        {
            var steps = new TextBlock { Text = task.StepsText, FontSize = 11.5, Margin = new Thickness(10, 0, 0, 0) };
            steps.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
            meta.Children.Add(steps);
        }
        stack.Children.Add(meta);

        var card = new Border
        {
            Padding = new Thickness(12, 10, 12, 10),
            Margin = new Thickness(0, 0, 0, 8),
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            Child = stack,
            Tag = task,
            Opacity = task.Done ? 0.55 : 1
        };
        card.SetResourceReference(Border.BackgroundProperty, "ZoneRowBrush");
        // Seul repère : l'étoile de « L'essentiel ». Rien d'autre ne distingue une carte d'une autre.
        card.SetResourceReference(Border.BorderBrushProperty, task.Pinned ? "AccentBrush" : "LineBrush");

        card.MouseLeftButtonDown += (s, e) => { Click.Down(s); _dragFrom = e.GetPosition(this); };
        card.MouseLeftButtonUp += (s, _) => { if (Click.Up(s)) OpenEditor(task, isNew: false); };
        card.PreviewMouseMove += (s, e) =>
        {
            if (_dragging || e.LeftButton != MouseButtonState.Pressed || !TaskDrag.Far(_dragFrom, e.GetPosition(this))) return;
            _dragging = true;
            Click.Up(s);
            try { DragDrop.DoDragDrop(card, new DataObject(TaskDrag.Format, task), DragDropEffects.Move); }
            finally { _dragging = false; }
        };
        _ = column;
        return card;
    }
}
