using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using DailyMemo.Core;
using DailyMemo.Services;
using Wpf.Ui.Controls;

namespace DailyMemo.ViewModels;

public abstract class PageViewModel : ObservableObject
{
    private string? _badge;

    protected PageViewModel(string key, string title, SymbolRegular icon)
    {
        Key = key;
        Title = title;
        Icon = icon;
    }

    public string Key { get; }
    public string Title { get; }
    public SymbolRegular Icon { get; }

    public string? Badge
    {
        get => _badge;
        protected set
        {
            if (Set(ref _badge, value)) OnPropertyChanged(nameof(HasBadge));
        }
    }

    public bool HasBadge => !string.IsNullOrEmpty(_badge);

    public abstract void Rebuild();
}

// ───────────────────────── 今天 ─────────────────────────

public sealed class TodayViewModel : PageViewModel
{
    private string _quickText = "";
    private string _quickHint = "";
    private string _dateTitle = "";
    private string _weekday = "";
    private string _summary = "";
    private string _nextUp = "";
    private double _progress;
    private string _progressText = "";
    private bool _isEmpty;

    public TodayViewModel() : base("today", "今天", SymbolRegular.CalendarToday24)
    {
        QuickAddCommand = new AsyncCommand(QuickAddAsync, () => !string.IsNullOrWhiteSpace(QuickText));
        NewTaskCommand = new RelayCommand(() => AppHost.Current.OpenEditor(null, ItemKind.Task, DateTime.Today, null));
        NewEventCommand = new RelayCommand(() => AppHost.Current.OpenEditor(null, ItemKind.Event, DateTime.Today, null));
        RefreshCommand = new AsyncCommand(() => AppHost.Current.Agenda.RefreshAsync());
    }

    public ObservableCollection<SectionViewModel> Sections { get; } = new();

    public string DateTitle { get => _dateTitle; private set => Set(ref _dateTitle, value); }
    public string Weekday { get => _weekday; private set => Set(ref _weekday, value); }
    public string Summary { get => _summary; private set => Set(ref _summary, value); }
    public string NextUp { get => _nextUp; private set { if (Set(ref _nextUp, value)) OnPropertyChanged(nameof(HasNextUp)); } }
    public bool HasNextUp => !string.IsNullOrEmpty(_nextUp);
    public double Progress { get => _progress; private set => Set(ref _progress, value); }
    public string ProgressText { get => _progressText; private set => Set(ref _progressText, value); }
    public bool IsEmpty { get => _isEmpty; private set => Set(ref _isEmpty, value); }

    public string QuickText
    {
        get => _quickText;
        set
        {
            if (!Set(ref _quickText, value)) return;
            var r = string.IsNullOrWhiteSpace(value) ? null : QuickParser.Parse(value, DateTime.Now);
            QuickHint = r?.Date != null ? "📅 " + r.Describe(DateTime.Today) : "";
        }
    }

    public string QuickHint { get => _quickHint; private set { if (Set(ref _quickHint, value)) OnPropertyChanged(nameof(HasQuickHint)); } }
    public bool HasQuickHint => !string.IsNullOrEmpty(_quickHint);

    public ICommand QuickAddCommand { get; }
    public ICommand NewTaskCommand { get; }
    public ICommand NewEventCommand { get; }
    public ICommand RefreshCommand { get; }

    private async System.Threading.Tasks.Task QuickAddAsync()
    {
        var text = QuickText.Trim();
        if (text.Length == 0) return;
        var r = QuickParser.Parse(text, DateTime.Now);
        // 没写日期的默认放到今天，免得「随手记」的事情被遗忘
        var due = r.When ?? DateTime.Today;
        await AppHost.Current.Agenda.AddTaskAsync(r.Title, due, r.Time.HasValue, null);
        QuickText = "";
    }

    public override void Rebuild()
    {
        var host = AppHost.Current;
        var now = DateTime.Now;
        var day = host.Agenda.Day(now.Date, includeCarryOver: true);

        DateTitle = ChineseDate.MonthDay(now);
        Weekday = ChineseDate.Weekday(now);

        var sections = new List<SectionViewModel>();
        ItemViewModel Vm(AgendaItem i, ItemContext c) => new(i, now.Date, now, c);

        if (day.Overdue.Count > 0)
            sections.Add(new SectionViewModel("已过期", day.Overdue.Select(i => Vm(i, ItemContext.Overdue)), warning: true));
        if (day.AllDayEvents.Count > 0)
            sections.Add(new SectionViewModel("全天", day.AllDayEvents.Select(i => Vm(i, ItemContext.Today))));
        if (day.Scheduled.Count > 0)
            sections.Add(new SectionViewModel("时间安排", day.Scheduled.Select(i => Vm(i, ItemContext.Today))));
        if (day.DayTasks.Count > 0)
            sections.Add(new SectionViewModel("今天要做", day.DayTasks.Select(i => Vm(i, ItemContext.Today))));
        if (host.Settings.Current.ShowUndatedInToday && day.Undated.Count > 0)
            sections.Add(new SectionViewModel("随时可做", day.Undated.Select(i => Vm(i, ItemContext.Today)), hint: "没有设日期的待办"));
        if (day.Completed.Count > 0)
            sections.Add(new SectionViewModel("今天已完成", day.Completed.Select(i => Vm(i, ItemContext.Today))));

        Sections.Clear();
        foreach (var s in sections) Sections.Add(s);

        int open = day.OpenTaskCount + day.Overdue.Count;
        var parts = new List<string>();
        if (day.EventCount > 0) parts.Add($"{day.EventCount} 个日程");
        if (open > 0) parts.Add($"{open} 项待办");
        if (day.Overdue.Count > 0) parts.Add($"其中 {day.Overdue.Count} 项已过期");
        Summary = parts.Count > 0 ? string.Join(" · ", parts) : "今天没有安排，给自己放个假吧 🌿";

        int doneToday = day.Completed.Count;
        int totalToday = doneToday + open;
        Progress = totalToday == 0 ? 0 : (double)doneToday / totalToday;
        ProgressText = totalToday == 0 ? "" : $"已完成 {doneToday}/{totalToday}";

        var next = day.Scheduled.FirstOrDefault(i => !i.Completed && (i.Start ?? DateTime.MinValue) >= now.AddMinutes(-1))
                   ?? day.Scheduled.FirstOrDefault(i => i.IsEvent && i.Start <= now && i.EffectiveEnd > now);
        if (next != null)
        {
            var start = next.Start!.Value;
            var mins = (int)Math.Round((start - now).TotalMinutes);
            string when = start <= now ? "进行中" : mins < 60 ? $"{mins} 分钟后" : $"{start:HH:mm}";
            NextUp = $"下一项：{when} · {next.Title}";
        }
        else NextUp = "";

        IsEmpty = sections.Count == 0;
        Badge = open > 0 ? open.ToString() : null;
    }
}

// ───────────────────────── 未来两周 ─────────────────────────

public sealed class DayGroupViewModel
{
    public DayGroupViewModel(string header, string sub, List<ItemViewModel> items, bool isWeekend)
    {
        Header = header;
        Sub = sub;
        Items = items;
        IsWeekend = isWeekend;
    }

    public string Header { get; }
    public string Sub { get; }
    public List<ItemViewModel> Items { get; }
    public bool IsWeekend { get; }
    public bool IsEmpty => Items.Count == 0;
}

public sealed class UpcomingViewModel : PageViewModel
{
    public UpcomingViewModel() : base("upcoming", "未来两周", SymbolRegular.CalendarLtr24)
    {
        NewEventCommand = new RelayCommand(() => AppHost.Current.OpenEditor(null, ItemKind.Event, DateTime.Today.AddDays(1), null));
    }

    public ObservableCollection<DayGroupViewModel> Days { get; } = new();

    public ICommand NewEventCommand { get; }

    public override void Rebuild()
    {
        var now = DateTime.Now;
        var items = AppHost.Current.Agenda.VisibleItems();
        Days.Clear();
        for (int i = 1; i < AgendaService.RangeDays - 1; i++)
        {
            var d = now.Date.AddDays(i);
            var day = AgendaBuilder.Build(items, d, includeCarryOver: false);
            var list = day.AllDayEvents.Concat(day.Scheduled).Concat(day.DayTasks)
                .Select(x => new ItemViewModel(x, d, now, ItemContext.Upcoming)).ToList();
            int events = day.EventCount, tasks = day.OpenTaskCount;
            var sub = events + tasks == 0 ? "无安排" : string.Join(" · ", new[]
            {
                events > 0 ? $"{events} 个日程" : null,
                tasks > 0 ? $"{tasks} 项待办" : null,
            }.Where(x => x != null));
            Days.Add(new DayGroupViewModel(ChineseDate.DayHeader(d, now.Date), sub, list, d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday));
        }
    }
}

// ───────────────────────── 待办清单 ─────────────────────────

public sealed class ListEntryViewModel : ObservableObject
{
    public ListEntryViewModel(SourceContainer container, int openCount)
    {
        Container = container;
        OpenCount = openCount;
    }

    public SourceContainer Container { get; }
    public string Name => Container.Name;
    public string SourceLabel => Container.SourceLabel;
    public System.Windows.Media.Brush ColorBrush => ItemViewModel.BrushFor(Container.Color);
    public int OpenCount { get; }
    public string CountText => OpenCount > 0 ? OpenCount.ToString() : "";
}

public sealed class ListsViewModel : PageViewModel
{
    private ListEntryViewModel? _selected;
    private string _quickText = "";
    private string _quickHint = "";
    private string? _selectedKey;

    public ListsViewModel() : base("lists", "待办清单", SymbolRegular.TaskListLtr24)
    {
        QuickAddCommand = new AsyncCommand(QuickAddAsync, () => !string.IsNullOrWhiteSpace(QuickText) && Selected != null);
    }

    public ObservableCollection<ListEntryViewModel> Lists { get; } = new();
    public ObservableCollection<ItemViewModel> OpenTasks { get; } = new();
    public ObservableCollection<ItemViewModel> DoneTasks { get; } = new();

    public ListEntryViewModel? Selected
    {
        get => _selected;
        set
        {
            if (Set(ref _selected, value))
            {
                if (value != null) _selectedKey = value.Container.Key;
                FillTasks();
            }
        }
    }

    public string QuickText
    {
        get => _quickText;
        set
        {
            if (!Set(ref _quickText, value)) return;
            var r = string.IsNullOrWhiteSpace(value) ? null : QuickParser.Parse(value, DateTime.Now);
            QuickHint = r?.Date != null ? "📅 " + r.Describe(DateTime.Today) : "";
        }
    }

    public string QuickHint { get => _quickHint; private set { if (Set(ref _quickHint, value)) OnPropertyChanged(nameof(HasQuickHint)); } }
    public bool HasQuickHint => !string.IsNullOrEmpty(_quickHint);
    public bool HasDone => DoneTasks.Count > 0;
    public bool IsListEmpty => OpenTasks.Count == 0;

    public ICommand QuickAddCommand { get; }

    private async System.Threading.Tasks.Task QuickAddAsync()
    {
        if (Selected == null) return;
        var r = QuickParser.Parse(QuickText.Trim(), DateTime.Now);
        await AppHost.Current.Agenda.AddTaskAsync(r.Title, r.When, r.Time.HasValue, null, Selected.Container.Key);
        QuickText = "";
    }

    public override void Rebuild()
    {
        var agenda = AppHost.Current.Agenda;
        var items = agenda.VisibleItems();
        Lists.Clear();
        foreach (var c in agenda.TaskContainers.Where(agenda.IsVisible))
            Lists.Add(new ListEntryViewModel(c, items.Count(i => i.IsTask && !i.Completed && i.ContainerKey == c.Key)));

        _selected = Lists.FirstOrDefault(l => l.Container.Key == _selectedKey) ?? Lists.FirstOrDefault();
        OnPropertyChanged(nameof(Selected));
        FillTasks();
    }

    private void FillTasks()
    {
        OpenTasks.Clear();
        DoneTasks.Clear();
        if (_selected != null)
        {
            var now = DateTime.Now;
            var key = _selected.Container.Key;
            var tasks = AppHost.Current.Agenda.VisibleItems().Where(i => i.IsTask && i.ContainerKey == key).ToList();
            foreach (var t in tasks.Where(t => !t.Completed).OrderBy(t => t.Start.HasValue ? 0 : 1).ThenBy(t => t.Start))
                OpenTasks.Add(new ItemViewModel(t, now.Date, now, ItemContext.List));
            foreach (var t in tasks.Where(t => t.Completed).OrderByDescending(t => t.CompletedAt))
                DoneTasks.Add(new ItemViewModel(t, now.Date, now, ItemContext.List));
        }
        OnPropertyChanged(nameof(HasDone));
        OnPropertyChanged(nameof(IsListEmpty));
    }
}
