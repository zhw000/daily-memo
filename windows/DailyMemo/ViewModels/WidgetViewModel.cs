using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using DailyMemo.Core;

namespace DailyMemo.ViewModels;

public sealed class WidgetViewModel : ObservableObject
{
    private string _dayNumber = "";
    private string _dateText = "";
    private string _summary = "";
    private string _quickText = "";
    private string _quickHint = "";
    private bool _isEmpty;
    private bool _isRefreshing;
    private bool _hasErrors;
    private double _progress;

    public WidgetViewModel(AppHost host)
    {
        QuickAddCommand = new AsyncCommand(QuickAddAsync, () => !string.IsNullOrWhiteSpace(QuickText));
        RefreshCommand = new AsyncCommand(() => host.Agenda.RefreshAsync());
        OpenMainCommand = new RelayCommand(() => host.ShowMainWindow());
        NewTaskCommand = new RelayCommand(() => host.OpenEditor(null, ItemKind.Task, DateTime.Today, host.WidgetWindow));
        NewEventCommand = new RelayCommand(() => host.OpenEditor(null, ItemKind.Event, DateTime.Today, host.WidgetWindow));
        HideCommand = new RelayCommand(() => host.SetWidgetVisible(false));
        SettingsCommand = new RelayCommand(() => host.ShowMainWindow("settings"));
        SetModeCommand = new RelayCommand(p =>
        {
            if (p is string s && Enum.TryParse<WidgetMode>(s, out var m))
            {
                host.Settings.Current.WidgetMode = m;
                host.Settings.Save();
                host.ApplyWidgetMode();
                OnPropertyChanged(nameof(Mode));
            }
        });
        ToggleLockCommand = new RelayCommand(() =>
        {
            host.Settings.Current.WidgetLocked = !host.Settings.Current.WidgetLocked;
            host.Settings.Save();
            host.ApplyWidgetMode();
            OnPropertyChanged(nameof(IsLocked));
        });
    }

    public ObservableCollection<object> Rows { get; } = new();

    public string DayNumber { get => _dayNumber; private set => Set(ref _dayNumber, value); }
    public string DateText { get => _dateText; private set => Set(ref _dateText, value); }
    public string Summary { get => _summary; private set => Set(ref _summary, value); }
    public bool IsEmpty { get => _isEmpty; private set => Set(ref _isEmpty, value); }
    public bool IsRefreshing { get => _isRefreshing; private set => Set(ref _isRefreshing, value); }
    public bool HasErrors { get => _hasErrors; private set => Set(ref _hasErrors, value); }
    public double Progress { get => _progress; private set => Set(ref _progress, value); }

    public WidgetMode Mode => AppHost.Current.Settings.Current.WidgetMode;
    public bool IsLocked => AppHost.Current.Settings.Current.WidgetLocked;

    public string QuickText
    {
        get => _quickText;
        set
        {
            if (!Set(ref _quickText, value)) return;
            var r = string.IsNullOrWhiteSpace(value) ? null : QuickParser.Parse(value, DateTime.Now);
            QuickHint = r?.Date != null ? r.Describe(DateTime.Today) : "";
        }
    }

    public string QuickHint { get => _quickHint; private set { if (Set(ref _quickHint, value)) OnPropertyChanged(nameof(HasQuickHint)); } }
    public bool HasQuickHint => !string.IsNullOrEmpty(_quickHint);

    public ICommand QuickAddCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand OpenMainCommand { get; }
    public ICommand NewTaskCommand { get; }
    public ICommand NewEventCommand { get; }
    public ICommand HideCommand { get; }
    public ICommand SettingsCommand { get; }
    public ICommand SetModeCommand { get; }
    public ICommand ToggleLockCommand { get; }

    private async System.Threading.Tasks.Task QuickAddAsync()
    {
        var r = QuickParser.Parse(QuickText.Trim(), DateTime.Now);
        await AppHost.Current.Agenda.AddTaskAsync(r.Title, r.When ?? DateTime.Today, r.Time.HasValue, null);
        QuickText = "";
    }

    public void Rebuild()
    {
        var host = AppHost.Current;
        var s = host.Settings.Current;
        var now = DateTime.Now;
        var day = host.Agenda.Day(now.Date, includeCarryOver: true);

        DayNumber = now.Day.ToString();
        DateText = $"{now.Month}月 · {ChineseDate.Weekday(now)}";
        IsRefreshing = host.Agenda.IsRefreshing;
        HasErrors = host.Agenda.Errors.Any();

        ItemViewModel Vm(AgendaItem i) => new(i, now.Date, now, ItemContext.Widget);
        var rows = new List<object>();
        if (day.Overdue.Count > 0)
        {
            rows.Add(new WidgetHeaderRow($"已过期 {day.Overdue.Count}", warning: true));
            rows.AddRange(day.Overdue.Select(i => new ItemViewModel(i, now.Date, now, ItemContext.Overdue)));
        }
        var todayItems = day.AllDayEvents.Concat(day.Scheduled).Concat(day.DayTasks).ToList();
        if (todayItems.Count > 0)
        {
            rows.Add(new WidgetHeaderRow("今天"));
            rows.AddRange(todayItems.Select(Vm));
        }
        if (s.WidgetShowUndated && day.Undated.Count > 0)
        {
            rows.Add(new WidgetHeaderRow("随时可做"));
            rows.AddRange(day.Undated.Select(Vm));
        }
        if (s.WidgetShowCompleted && day.Completed.Count > 0)
        {
            rows.Add(new WidgetHeaderRow($"已完成 {day.Completed.Count}"));
            rows.AddRange(day.Completed.Select(Vm));
        }

        Rows.Clear();
        foreach (var r in rows) Rows.Add(r);
        IsEmpty = rows.Count == 0;

        int open = day.OpenTaskCount + day.Overdue.Count;
        var parts = new List<string>();
        if (day.EventCount > 0) parts.Add($"{day.EventCount} 个日程");
        if (open > 0) parts.Add($"{open} 项待办");
        Summary = parts.Count > 0 ? string.Join(" · ", parts) : "今天没有安排";
        int total = open + day.Completed.Count;
        Progress = total == 0 ? 0 : (double)day.Completed.Count / total;
        OnPropertyChanged(nameof(Mode));
        OnPropertyChanged(nameof(IsLocked));
    }
}
