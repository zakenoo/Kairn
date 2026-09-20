using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Kairn.Models;
using Kairn.Services;

namespace Kairn.Views;

/// <summary>
/// La semaine posée à plat : sept colonnes, les blocs à leur heure, et on les fait glisser.
/// Voir mardi et jeudi en même temps change tout quand on n'a aucune idée de ce que « dans trois jours » veut dire.
/// </summary>
public partial class PlanningView
{
    /// <summary>Hauteur d'une heure, en pixels. Assez grand pour viser, assez petit pour tenir à l'écran.</summary>
    private const double HourHeight = 46;
    private int _weekFrom = 7, _weekTo = 23;

    private void PrevWeek_Click(object sender, RoutedEventArgs e) => SelectDate(_selected.AddDays(-7));
    private void NextWeek_Click(object sender, RoutedEventArgs e) => SelectDate(_selected.AddDays(7));

    private void BuildWeek()
    {
        // Lundi de la semaine du jour sélectionné (le calendrier de gauche commence lui aussi le lundi).
        var monday = _selected.AddDays(-(((int)_selected.DayOfWeek + 6) % 7));
        var days = Enumerable.Range(0, 7).Select(monday.AddDays).ToList();
        var week = Storage.Data.Tasks.Where(t => !t.Floating && days.Contains(t.Date)).ToList();

        // La plage d'heures s'ajuste à ce qui existe : inutile d'afficher 3 h du matin si personne n'y travaille.
        _weekFrom = week.Count == 0 ? 7 : Math.Max(0, (int)week.Min(t => t.Start.TotalHours) - 1);
        _weekTo = week.Count == 0 ? 23 : Math.Min(24, (int)Math.Ceiling(week.Max(t => Math.Max(t.End.TotalHours, t.Start.TotalHours + 0.5))) + 1);
        if (_weekTo - _weekFrom < 8) _weekTo = Math.Min(24, _weekFrom + 8);
        double height = (_weekTo - _weekFrom) * HourHeight;

        BuildHourRuler(height);
        BuildWeekHeads(days);

        WeekDays.Children.Clear();
        var today = DateOnly.FromDateTime(DateTime.Now);
        foreach (var day in days)
        {
            var canvas = new Canvas { Height = height, Margin = new Thickness(1, 0, 1, 0), AllowDrop = true, VerticalAlignment = VerticalAlignment.Top };
            var back = new Border { Width = double.NaN, Height = height, CornerRadius = new CornerRadius(4) };
            back.SetResourceReference(Border.BackgroundProperty, day == today ? "AccentSoftBrush" : "Surface2Brush");
            back.Opacity = day == today ? 0.55 : 0.5;
            canvas.Children.Add(back);
            canvas.Loaded += (_, _) => back.Width = canvas.ActualWidth;
            canvas.SizeChanged += (_, _) => back.Width = canvas.ActualWidth;

            for (int h = _weekFrom + 1; h < _weekTo; h++)
            {
                var line = new Border { Height = 1, Opacity = 0.5 };
                line.SetResourceReference(Border.BackgroundProperty, "LineBrush");
                Canvas.SetTop(line, (h - _weekFrom) * HourHeight);
                canvas.Children.Add(line);
                canvas.SizeChanged += (_, _) => line.Width = canvas.ActualWidth;
            }

            foreach (var task in Storage.TasksFor(day).Where(t => !t.Floating))
                canvas.Children.Add(WeekBlock(task, canvas));

            var captured = day;
            canvas.DragOver += (_, e) =>
            {
                e.Effects = TaskDrag.From(e.Data) is null ? DragDropEffects.None : DragDropEffects.Move;
                e.Handled = true;
            };
            canvas.Drop += (_, e) =>
            {
                if (TaskDrag.From(e.Data) is not { } moved) return;
                e.Handled = true;
                TaskDrag.MoveToSlot(moved, captured, TimeFromY(e.GetPosition(canvas).Y));
                Refresh();
            };
            // Cliquer dans le vide d'un jour : on crée une tâche à l'heure visée, sans passer par un formulaire vide.
            canvas.MouseLeftButtonDown += (_, e) => Click.Down(canvas);
            canvas.MouseLeftButtonUp += (_, e) =>
            {
                if (!Click.Up(canvas)) return;
                var start = TaskDrag.Round(TimeFromY(e.GetPosition(canvas).Y));
                SelectDate(captured);
                OpenEditor(new PlanTask { Date = captured, Start = start, End = start + TimeSpan.FromHours(1) }, isNew: true);
            };
            WeekDays.Children.Add(canvas);
        }
    }

    private TimeSpan TimeFromY(double y) =>
        TimeSpan.FromHours(Math.Clamp(_weekFrom + y / HourHeight, 0, 23.75));

    private void BuildHourRuler(double height)
    {
        WeekHours.Children.Clear();
        WeekHours.Height = height;
        for (int h = _weekFrom; h < _weekTo; h++)
        {
            var label = new TextBlock { Text = TimeText(TimeSpan.FromHours(h)), FontSize = 11 };
            label.SetResourceReference(TextBlock.ForegroundProperty, "FaintBrush");
            Canvas.SetTop(label, (h - _weekFrom) * HourHeight - 6);
            Canvas.SetLeft(label, 2);
            WeekHours.Children.Add(label);
        }
    }

    private void BuildWeekHeads(List<DateOnly> days)
    {
        WeekHeads.Children.Clear();
        var today = DateOnly.FromDateTime(DateTime.Now);
        foreach (var day in days)
        {
            var name = new TextBlock
            {
                Text = day.ToString("ddd d", Fr),
                FontSize = 12,
                FontWeight = day == today || day == _selected ? FontWeights.SemiBold : FontWeights.Normal,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            name.SetResourceReference(TextBlock.ForegroundProperty, day == today ? "AccentBrush" : "SubtleBrush");
            var head = new Border { Padding = new Thickness(4, 5, 4, 5), Cursor = Cursors.Hand, Child = name, CornerRadius = new CornerRadius(4) };
            if (day == _selected) head.SetResourceReference(Border.BackgroundProperty, "Surface2Brush");
            var captured = day;
            Click.Attach(head, () => SelectDate(captured));
            WeekHeads.Children.Add(head);
        }
    }

    private FrameworkElement WeekBlock(PlanTask task, Canvas column)
    {
        double top = (task.Start.TotalHours - _weekFrom) * HourHeight;
        double height = Math.Max(20, task.Duration.TotalHours * HourHeight - 2);

        var title = new TextBlock
        {
            Text = (task.HasEmoji ? task.Emoji + " " : "") + task.Title,
            FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.Wrap, MaxHeight = Math.Max(14, height - 16)
        };
        var time = new TextBlock { Text = TimeText(task.Start), FontSize = 10 };
        time.SetResourceReference(TextBlock.ForegroundProperty, "SubtleBrush");

        var stack = new StackPanel();
        if (height > 34) stack.Children.Add(time);
        stack.Children.Add(title);

        var block = new Border
        {
            Height = height,
            Padding = new Thickness(7, 4, 6, 4),
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            Child = stack,
            Tag = task,
            AllowDrop = false,
            ToolTip = $"{task.TimeRange} · {task.Title}",
            Opacity = task.Done ? 0.45 : 1
        };
        block.SetResourceReference(Border.BackgroundProperty, task.IsBreak || task.IsExternal ? "Surface2Brush" : "ZoneRowBrush");
        block.SetResourceReference(Border.BorderBrushProperty, task.Pinned ? "AccentBrush" : "LineBrush");
        if (task.IsExternal) block.ToolTip = $"{task.TimeRange} · {task.Title}\n{L.T("cal.external.tip")}";
        if (Storage.CategoryById(task.CategoryId) is { } cat)
            block.BorderBrush = new SolidColorBrush(ThemeService.Parse(cat.Color, "#F2F2F2"));

        Canvas.SetTop(block, top);
        Canvas.SetLeft(block, 2);
        column.SizeChanged += (_, _) => block.Width = Math.Max(10, column.ActualWidth - 4);
        column.Loaded += (_, _) => block.Width = Math.Max(10, column.ActualWidth - 4);

        block.MouseLeftButtonDown += (s, e) => { Click.Down(s); _dragFrom = e.GetPosition(this); e.Handled = true; };
        block.MouseLeftButtonUp += (s, e) =>
        {
            if (Click.Up(s))
            {
                if (task.IsExternal) Undo.Note(L.T("cal.external.readOnly"));
                else { SelectDate(task.Date); OpenEditor(task, isNew: false); }
            }
            e.Handled = true;
        };
        block.PreviewMouseMove += (s, e) =>
        {
            if (task.IsExternal) return; // on ne déplace pas un rendez-vous qui vit chez Apple
            if (_dragging || e.LeftButton != MouseButtonState.Pressed || !TaskDrag.Far(_dragFrom, e.GetPosition(this))) return;
            _dragging = true;
            Click.Up(s);
            try { DragDrop.DoDragDrop(block, new DataObject(TaskDrag.Format, task), DragDropEffects.Move); }
            finally { _dragging = false; }
        };
        return block;
    }
}
