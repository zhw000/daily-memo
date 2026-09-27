using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using DailyMemo.Core;

namespace DailyMemo.ViewModels;

public enum ItemContext { Today, Overdue, Upcoming, List, Widget }

/// <summary>列表里的一行（日程或待办）</summary>
public sealed class ItemViewModel : ObservableObject
{
    private static readonly ConcurrentDictionary<string, Brush> Brushes = new();

    private readonly DateTime _day;
    private readonly DateTime _now;
    private readonly ItemContext _context;

    public ItemViewModel(AgendaItem item, DateTime day, DateTime now, ItemContext context)
    {
        Item = item;
        _day = day.Date;
        _now = now;
        _context = context;

        ToggleCommand = new AsyncCommand(() => AppHost.Current.ToggleAsync(Item), () => IsTask && !Item.ReadOnly);
        EditCommand = new RelayCommand(() => AppHost.Current.OpenEditor(Item, Item.Kind, null, OwnerWindow()));
        DeleteCommand = new AsyncCommand(() => AppHost.Current.DeleteAsync(Item, OwnerWindow()), () => !Item.ReadOnly);
        OpenLinkCommand = new RelayCommand(() => AppHost.Current.OpenLink(Item), () => !string.IsNullOrEmpty(Item.Link));
    }

    public AgendaItem Item { get; }

    public string Title => Item.Title;
    public bool IsTask => Item.IsTask;
    public bool IsEvent => Item.IsEvent;
    public bool IsCompleted => Item.Completed;
    public bool IsReadOnly => Item.ReadOnly;
    public bool CanDelete => !Item.ReadOnly;
    public string? Location => Item.Location;
    public bool HasLocation => !string.IsNullOrWhiteSpace(Item.Location);
    public string? Notes => Item.Notes;
    public bool HasNotes => !string.IsNullOrWhiteSpace(Item.Notes);
    public bool IsRecurring => Item.Recurring;

    public bool IsOverdue =>
        IsTask && !Item.Completed && Item.Start.HasValue &&
        (Item.HasTime ? Item.Start.Value < _now : Item.Start.Value.Date < _now.Date);

    /// <summary>已经结束的日程变淡显示</summary>
    public bool IsPast => IsEvent && _day == _now.Date && !Item.AllDay && Item.EffectiveEnd <= _now;

    public bool IsDimmed => IsPast || IsCompleted;

    /// <summary>正在进行的日程</summary>
    public bool IsNow => IsEvent && !Item.AllDay && Item.Start <= _now && Item.EffectiveEnd > _now;

    public Brush ColorBrush => BrushFor(Item.Color);

    public string TimeText => BuildTimeText();
    public bool HasTimeText => TimeText.Length > 0;

    public string MetaText => _context == ItemContext.Widget ? Item.ContainerName : $"{SourceLabel} · {Item.ContainerName}";

    public string SourceLabel => Item.Source switch
    {
        ItemSource.Local => "本机",
        ItemSource.Google => Item.IsTask ? "Google Tasks" : "Google 日历",
        _ => "iCloud",
    };

    public string ToolTipText
    {
        get
        {
            var lines = new List<string> { Item.Title };
            var t = BuildTimeText(forTooltip: true);
            if (t.Length > 0) lines.Add(t);
            if (HasLocation) lines.Add("📍 " + Item.Location);
            lines.Add($"{SourceLabel} · {Item.ContainerName}");
            if (HasNotes) lines.Add(Item.Notes!.Length > 200 ? Item.Notes[..200] + "…" : Item.Notes!);
            return string.Join("\n", lines);
        }
    }

    public ICommand ToggleCommand { get; }
    public ICommand EditCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand OpenLinkCommand { get; }

    private string BuildTimeText(bool forTooltip = false)
    {
        var today = _now.Date;
        if (IsEvent)
        {
            if (!Item.Start.HasValue) return "";
            var s = Item.Start.Value;
            if (Item.AllDay)
            {
                var lastDay = (Item.End ?? s.AddDays(1)).Date.AddDays(-1);
                if (lastDay > s.Date) return $"全天 · {ChineseDate.MonthDay(s.Date, today)} – {ChineseDate.MonthDay(lastDay, today)}";
                return forTooltip ? $"{ChineseDate.MonthDay(s, today)} 全天" : "全天";
            }
            var e = Item.End ?? s;
            string prefix = forTooltip || _context == ItemContext.List ? ChineseDate.Due(s, false, today) + " " : "";
            bool startsBefore = s.Date < _day && !forTooltip;
            bool endsAfter = e.Date > _day && e > _day.AddDays(1) && !forTooltip;
            if (startsBefore && endsAfter) return "全天（持续中）";
            if (startsBefore) return $"至 {e:HH:mm}";
            if (endsAfter || e <= s) return e <= s ? $"{prefix}{s:HH:mm}" : $"{prefix}{s:HH:mm} 起";
            return $"{prefix}{s:HH:mm} – {e:HH:mm}";
        }

        if (!Item.Start.HasValue) return forTooltip ? "无截止日期" : "";
        var due = Item.Start.Value;
        return _context switch
        {
            ItemContext.Today or ItemContext.Upcoming when due.Date == _day && !forTooltip => Item.HasTime ? due.ToString("HH:mm") : "",
            ItemContext.Widget when due.Date == _day => Item.HasTime ? due.ToString("HH:mm") : "",
            _ => (IsOverdue && !forTooltip ? "已过期 · " : "") + ChineseDate.Due(due, Item.HasTime, today),
        };
    }

    private Window? OwnerWindow()
    {
        foreach (Window w in Application.Current.Windows)
            if (w.IsActive) return w;
        return null;
    }

    public static Brush BrushFor(string hex)
    {
        return Brushes.GetOrAdd(hex, h =>
        {
            try
            {
                var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(h));
                b.Freeze();
                return b;
            }
            catch
            {
                return System.Windows.Media.Brushes.SteelBlue;
            }
        });
    }
}

/// <summary>分组：已过期 / 全天 / 今天 / 随时 / 已完成</summary>
public sealed class SectionViewModel
{
    public SectionViewModel(string header, IEnumerable<ItemViewModel> items, bool warning = false, string? hint = null)
    {
        Header = header;
        Items = new List<ItemViewModel>(items);
        IsWarning = warning;
        Hint = hint;
    }

    public string Header { get; }
    public List<ItemViewModel> Items { get; }
    public bool IsWarning { get; }
    public string? Hint { get; }
    public string CountText => Items.Count.ToString();
}

/// <summary>小组件里的分组标题行</summary>
public sealed class WidgetHeaderRow
{
    public WidgetHeaderRow(string text, bool warning = false)
    {
        Text = text;
        IsWarning = warning;
    }

    public string Text { get; }
    public bool IsWarning { get; }
}
