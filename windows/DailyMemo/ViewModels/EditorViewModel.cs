using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Input;
using DailyMemo.Core;

namespace DailyMemo.ViewModels;

/// <summary>新建 / 编辑一条待办或日程</summary>
public sealed class EditorViewModel : ObservableObject
{
    private readonly AppHost _host;
    private readonly TimeSpan _origDuration;
    private readonly int _origSpanDays = 1;
    private bool _isTask;
    private string _title = "";
    private string _notes = "";
    private string _location = "";
    private bool _hasDate;
    private DateTime? _date;
    private bool _hasTime;
    private string _startTime = "";
    private string _endTime = "";
    private TargetOption? _target;
    private string? _error;
    private string _parseHint = "";

    public EditorViewModel(AppHost host, AgendaItem? item, ItemKind kind, DateTime? date)
    {
        _host = host;
        Original = item;
        if (item != null)
        {
            _isTask = item.IsTask;
            _title = item.Title;
            _notes = item.Notes ?? "";
            _location = item.Location ?? "";
            _hasDate = item.Start.HasValue;
            _date = item.Start?.Date ?? DateTime.Today;
            _hasTime = item.HasTime;
            _startTime = item.HasTime ? item.Start!.Value.ToString("HH:mm") : DefaultStart().ToString("HH:mm");
            var end = item.End ?? item.Start ?? DateTime.Now;
            _endTime = item.HasTime && item.IsEvent ? end.ToString("HH:mm") : DefaultStart().AddHours(1).ToString("HH:mm");
            if (item.IsEvent && item.Start.HasValue)
            {
                _origDuration = end - item.Start.Value;
                if (item.AllDay) _origSpanDays = Math.Max(1, (int)Math.Round((end.Date - item.Start.Value.Date).TotalDays));
            }
        }
        else
        {
            _isTask = kind == ItemKind.Task;
            _date = (date ?? DateTime.Today).Date;
            _hasDate = true;
            _hasTime = !_isTask;
            var start = DefaultStart();
            if (_date != DateTime.Today) start = _date.Value.AddHours(9);
            _startTime = start.ToString("HH:mm");
            _endTime = start.AddHours(1).ToString("HH:mm");
        }

        SaveCommand = new AsyncCommand(SaveAsync, () => !IsReadOnly);
        DeleteCommand = new AsyncCommand(DeleteAsync, () => CanDelete);
        CancelCommand = new RelayCommand(() => CloseRequested?.Invoke(false));
        QuickDateCommand = new RelayCommand(p => QuickDate(p as string));
        BuildTargets();
    }

    public event Action<bool>? CloseRequested;

    public AgendaItem? Original { get; }
    public bool IsNew => Original == null;

    public string WindowTitle => (IsNew ? "新建" : IsReadOnly ? "查看" : "编辑") + (IsTask ? "待办" : "日程");

    public bool IsTask
    {
        get => _isTask;
        set
        {
            if (!Set(ref _isTask, value)) return;
            if (!value) { _hasDate = true; _hasTime = true; }
            else _hasTime = false;
            OnPropertyChanged(nameof(IsEvent));
            OnPropertyChanged(nameof(WindowTitle));
            OnPropertyChanged(nameof(HasDate));
            OnPropertyChanged(nameof(HasTime));
            OnPropertyChanged(nameof(AllDay));
            OnPropertyChanged(nameof(ShowTimeFields));
            OnPropertyChanged(nameof(ShowEndTime));
            BuildTargets();
        }
    }

    public bool IsEvent { get => !_isTask; set => IsTask = !value; }
    public bool CanChangeKind => IsNew;

    public string Title
    {
        get => _title;
        set
        {
            if (!Set(ref _title, value)) return;
            if (IsNew)
            {
                var r = QuickParser.Parse(value, DateTime.Now);
                ParseHint = r.Date.HasValue ? $"识别到「{r.Describe(DateTime.Today)}」，点这里应用" : "";
            }
        }
    }

    public string ParseHint { get => _parseHint; private set { if (Set(ref _parseHint, value)) OnPropertyChanged(nameof(HasParseHint)); } }
    public bool HasParseHint => !string.IsNullOrEmpty(_parseHint);

    public string Notes { get => _notes; set => Set(ref _notes, value); }
    public string Location { get => _location; set => Set(ref _location, value); }

    public bool HasDate
    {
        get => _hasDate || IsEvent;
        set
        {
            if (!Set(ref _hasDate, value)) return;
            if (!value) HasTime = false;
            OnPropertyChanged(nameof(ShowTimeFields));
        }
    }

    public DateTime? Date { get => _date; set => Set(ref _date, value); }

    public bool HasTime
    {
        get => _hasTime;
        set
        {
            if (!Set(ref _hasTime, value)) return;
            OnPropertyChanged(nameof(AllDay));
            OnPropertyChanged(nameof(ShowTimeFields));
            OnPropertyChanged(nameof(ShowEndTime));
        }
    }

    public bool AllDay { get => !_hasTime; set => HasTime = !value; }
    public bool ShowTimeFields => HasTime && HasDate;
    public bool ShowEndTime => HasTime && IsEvent;

    public string StartTime { get => _startTime; set => Set(ref _startTime, value); }
    public string EndTime { get => _endTime; set => Set(ref _endTime, value); }

    public IReadOnlyList<string> TimeOptions { get; } =
        Enumerable.Range(0, 48).Select(i => $"{i / 2:00}:{(i % 2) * 30:00}").ToList();

    public ObservableCollection<TargetOption> Targets { get; } = new();
    public TargetOption? SelectedTarget { get => _target; set => Set(ref _target, value); }

    public string? Error { get => _error; private set { if (Set(ref _error, value)) OnPropertyChanged(nameof(HasError)); } }
    public bool HasError => !string.IsNullOrEmpty(_error);

    public bool IsReadOnly => Original != null && (Original.ReadOnly || (Original.Source == ItemSource.ICloud && Original.IsEvent));
    public bool IsEditable => !IsReadOnly;

    public string? Hint =>
        Original == null ? (IsTask ? "小技巧：标题里写「明天下午3点」「周五」会自动识别日期。" : null)
        : Original.ReadOnly ? "这个日历是只读的，不能修改。"
        : Original.Source == ItemSource.ICloud ? "iCloud 日程请在 iPhone「日历」里修改；这里可以删除非重复日程。"
        : Original.Recurring ? "这是重复日程中的一次，修改和删除只影响这一次。"
        : Original.Source == ItemSource.Local ? "本机待办不会同步到手机。连接 Google 后可以搬到 Google Tasks。"
        : null;

    public bool HasHint => !string.IsNullOrEmpty(Hint);

    public bool CanDelete => Original != null && !Original.ReadOnly && !(Original.Source == ItemSource.ICloud && Original.Recurring);

    public ICommand SaveCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand QuickDateCommand { get; }

    public void ApplyParsedTitle()
    {
        var r = QuickParser.Parse(Title, DateTime.Now);
        if (!r.Date.HasValue) return;
        _title = r.Title;
        OnPropertyChanged(nameof(Title));
        Date = r.Date.Value.Date;
        HasDate = true;
        if (r.Time.HasValue)
        {
            HasTime = true;
            StartTime = DateTime.Today.Add(r.Time.Value).ToString("HH:mm");
            EndTime = DateTime.Today.Add(r.Time.Value).AddHours(1).ToString("HH:mm");
        }
        ParseHint = "";
    }

    private void QuickDate(string? which)
    {
        var today = DateTime.Today;
        switch (which)
        {
            case "none":
                HasDate = false;
                return;
            case "today": Date = today; break;
            case "tomorrow": Date = today.AddDays(1); break;
            case "weekend":
                int toSat = ((int)DayOfWeek.Saturday - (int)today.DayOfWeek + 7) % 7;
                Date = today.AddDays(toSat);
                break;
            case "nextweek":
                int toMon = ((int)DayOfWeek.Monday - (int)today.DayOfWeek + 7) % 7;
                Date = today.AddDays(toMon == 0 ? 7 : toMon);
                break;
        }
        HasDate = true;
    }

    private void BuildTargets()
    {
        var agenda = _host.Agenda;
        Targets.Clear();
        var containers = IsTask ? agenda.TaskContainers : agenda.EventContainers;
        foreach (var c in containers) Targets.Add(new TargetOption(c.Key, c.DisplayName));

        if (Original != null && Original.Kind == (IsTask ? ItemKind.Task : ItemKind.Event) && Targets.All(t => t.Key != Original.ContainerKey))
        {
            var c = agenda.FindContainer(Original.ContainerKey);
            Targets.Add(new TargetOption(Original.ContainerKey, c?.DisplayName ?? Original.ContainerName));
        }

        string? key = Original != null && Original.Kind == (IsTask ? ItemKind.Task : ItemKind.Event)
            ? Original.ContainerKey
            : IsTask ? agenda.ResolveTaskTarget() : agenda.ResolveEventTarget();
        SelectedTarget = Targets.FirstOrDefault(t => t.Key == key) ?? Targets.FirstOrDefault();
        OnPropertyChanged(nameof(NoTargets));
    }

    public bool NoTargets => Targets.Count == 0;

    private async Task SaveAsync()
    {
        Error = null;
        var title = Title.Trim();
        if (title.Length == 0)
        {
            Error = "请输入标题";
            return;
        }
        if (SelectedTarget == null)
        {
            Error = IsTask ? "没有可用的待办清单" : "还没有可写入的日历：请先在设置里连接 Google 或 iCloud 日历。";
            return;
        }

        var edited = new AgendaItem
        {
            Kind = IsTask ? ItemKind.Task : ItemKind.Event,
            Title = title,
            Notes = string.IsNullOrWhiteSpace(Notes) ? null : Notes.Trim(),
            Location = string.IsNullOrWhiteSpace(Location) ? null : Location.Trim(),
        };

        var day = (Date ?? DateTime.Today).Date;
        if (IsTask)
        {
            if (HasDate)
            {
                if (HasTime)
                {
                    var t = ParseTime(StartTime);
                    if (t == null)
                    {
                        Error = "时间格式不对，例如 09:30";
                        return;
                    }
                    edited.Start = day + t.Value;
                    edited.AllDay = false;
                }
                else
                {
                    edited.Start = day;
                    edited.AllDay = true;
                }
            }
            else
            {
                edited.AllDay = true;
            }
        }
        else if (AllDay)
        {
            edited.AllDay = true;
            edited.Start = day;
            edited.End = day.AddDays(_origSpanDays);
        }
        else
        {
            var st = ParseTime(StartTime);
            if (st == null)
            {
                Error = "开始时间格式不对，例如 09:30";
                return;
            }
            var start = day + st.Value;
            var en = ParseTime(EndTime);
            DateTime end;
            if (en.HasValue)
            {
                end = day + en.Value;
                if (end <= start) end = end.AddDays(1);
            }
            else end = start + (_origDuration > TimeSpan.Zero ? _origDuration : TimeSpan.FromHours(1));
            edited.Start = start;
            edited.End = end;
            edited.AllDay = false;
        }

        var agenda = _host.Agenda;
        if (Original == null)
        {
            if (IsTask) await agenda.AddTaskAsync(edited.Title, edited.Start, edited.HasTime, edited.Notes, SelectedTarget.Key);
            else await agenda.AddEventAsync(edited.Title, edited.Start!.Value, edited.End!.Value, edited.AllDay, edited.Notes, edited.Location, SelectedTarget.Key);
        }
        else
        {
            await agenda.UpdateAsync(Original, edited, SelectedTarget.Key);
        }
        CloseRequested?.Invoke(true);
    }

    private async Task DeleteAsync()
    {
        if (Original == null) return;
        if (!await _host.ConfirmAsync($"确定删除「{Original.Title}」吗？", "删除", null)) return;
        await _host.Agenda.DeleteAsync(Original);
        CloseRequested?.Invoke(true);
    }

    private static DateTime DefaultStart()
    {
        var now = DateTime.Now;
        var next = now.Date.AddHours(now.Hour + 1);
        return next.Date == now.Date ? next : now.Date.AddHours(23);
    }

    internal static TimeSpan? ParseTime(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var m = Regex.Match(text.Trim(), @"^(\d{1,2})\s*[:：.点]?\s*(\d{2})?\s*分?$");
        if (!m.Success) return null;
        int h = int.Parse(m.Groups[1].Value);
        int min = m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : 0;
        if (h > 23 || min > 59) return null;
        return new TimeSpan(h, min, 0);
    }
}
