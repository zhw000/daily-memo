using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace DailyMemo.Core;

public enum ItemKind { Event, Task }

public enum ItemSource { Local, Google, ICloud }

/// <summary>
/// 一条日程或待办。时间全部是本地时间。
/// 日程：Start/End；全天日程 End 为不含的结束日期（次日 00:00）。
/// 待办：Start 为截止日期（AllDay=true 表示没有具体时间）。
/// </summary>
public sealed class AgendaItem
{
    public string Key { get; set; } = "";
    public ItemKind Kind { get; set; }
    public ItemSource Source { get; set; }
    public string Id { get; set; } = "";
    public string ContainerId { get; set; } = "";
    public string ContainerName { get; set; } = "";
    public string Color { get; set; } = "#4C6EF5";
    public string Title { get; set; } = "";
    public string? Notes { get; set; }
    public string? Location { get; set; }
    public DateTime? Start { get; set; }
    public DateTime? End { get; set; }
    public bool AllDay { get; set; }
    public bool Completed { get; set; }
    public DateTime? CompletedAt { get; set; }
    public bool Recurring { get; set; }
    public string? Link { get; set; }
    public string? Href { get; set; }
    public string? ETag { get; set; }
    public bool ReadOnly { get; set; }

    [JsonIgnore] public bool IsTask => Kind == ItemKind.Task;
    [JsonIgnore] public bool IsEvent => Kind == ItemKind.Event;
    [JsonIgnore] public bool HasTime => Start.HasValue && !AllDay;

    /// <summary>用于显示开关和默认保存位置的容器键：local / gt:清单 / gc:日历 / ic:日历URL</summary>
    [JsonIgnore]
    public string ContainerKey => ContainerKeys.For(Source, Kind, ContainerId);

    public AgendaItem Clone() => (AgendaItem)MemberwiseClone();

    /// <summary>日程在某天内的结束时间（用于判断是否已结束）</summary>
    [JsonIgnore]
    public DateTime EffectiveEnd => End ?? (AllDay ? (Start ?? DateTime.MinValue).Date.AddDays(1) : Start ?? DateTime.MinValue);
}

public static class ContainerKeys
{
    public const string Local = "local";

    public static string For(ItemSource source, ItemKind kind, string containerId) => source switch
    {
        ItemSource.Local => Local,
        ItemSource.Google => (kind == ItemKind.Task ? "gt:" : "gc:") + containerId,
        _ => "ic:" + containerId,
    };

    public static (ItemSource source, ItemKind kind, string id) Parse(string key)
    {
        if (key == Local) return (ItemSource.Local, ItemKind.Task, "");
        if (key.StartsWith("gt:")) return (ItemSource.Google, ItemKind.Task, key[3..]);
        if (key.StartsWith("gc:")) return (ItemSource.Google, ItemKind.Event, key[3..]);
        if (key.StartsWith("ic:")) return (ItemSource.ICloud, ItemKind.Event, key[3..]);
        throw new ArgumentException("未知的保存位置：" + key);
    }
}

/// <summary>日历或待办清单</summary>
public sealed class SourceContainer
{
    public string Key { get; set; } = "";
    public ItemKind Kind { get; set; }
    public ItemSource Source { get; set; }
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Color { get; set; } = "#4C6EF5";
    public bool Writable { get; set; } = true;
    public bool DefaultVisible { get; set; } = true;
    public bool IsPrimary { get; set; }

    [JsonIgnore]
    public string SourceLabel => Source switch
    {
        ItemSource.Local => "本机",
        ItemSource.Google => Kind == ItemKind.Task ? "Google Tasks" : "Google 日历",
        _ => "iCloud 日历",
    };

    [JsonIgnore] public string DisplayName => $"{Name}（{SourceLabel}）";
}

/// <summary>某一天要显示的内容</summary>
public sealed class DayAgenda
{
    public DateTime Date { get; init; }
    public List<AgendaItem> Overdue { get; } = new();
    public List<AgendaItem> AllDayEvents { get; } = new();
    public List<AgendaItem> Scheduled { get; } = new();
    public List<AgendaItem> DayTasks { get; } = new();
    public List<AgendaItem> Undated { get; } = new();
    public List<AgendaItem> Completed { get; } = new();

    public int EventCount => AllDayEvents.Count + Scheduled.Count(i => i.IsEvent);
    public int OpenTaskCount => DayTasks.Count + Scheduled.Count(i => i.IsTask);
    public bool IsEmpty => Overdue.Count + AllDayEvents.Count + Scheduled.Count + DayTasks.Count + Undated.Count + Completed.Count == 0;
}

public static class AgendaBuilder
{
    /// <param name="includeCarryOver">是否包含已过期和无日期的待办（只对「今天」有意义）</param>
    public static DayAgenda Build(IEnumerable<AgendaItem> items, DateTime day, bool includeCarryOver)
    {
        var d0 = day.Date;
        var d1 = d0.AddDays(1);
        var a = new DayAgenda { Date = d0 };

        foreach (var it in items)
        {
            if (it.IsEvent)
            {
                if (!it.Start.HasValue) continue;
                var s = it.Start.Value;
                if (it.AllDay)
                {
                    var e = it.End ?? s.Date.AddDays(1);
                    if (e <= s.Date) e = s.Date.AddDays(1);
                    if (s.Date < d1 && e > d0) a.AllDayEvents.Add(it);
                }
                else
                {
                    var e = it.End ?? s;
                    bool overlaps = s < d1 && (e > d0 || (e == s && s >= d0));
                    if (overlaps) a.Scheduled.Add(it);
                }
                continue;
            }

            if (it.Completed)
            {
                var cd = (it.CompletedAt ?? it.Start)?.Date;
                if (cd == d0) a.Completed.Add(it);
                continue;
            }

            if (!it.Start.HasValue)
            {
                if (includeCarryOver) a.Undated.Add(it);
                continue;
            }

            var due = it.Start.Value.Date;
            if (due == d0)
            {
                if (it.HasTime) a.Scheduled.Add(it);
                else a.DayTasks.Add(it);
            }
            else if (due < d0 && includeCarryOver)
            {
                a.Overdue.Add(it);
            }
        }

        a.AllDayEvents.Sort((x, y) => string.Compare(x.Title, y.Title, StringComparison.CurrentCulture));
        a.Scheduled.Sort((x, y) =>
        {
            var sx = x.Start!.Value < d0 ? d0 : x.Start!.Value;
            var sy = y.Start!.Value < d0 ? d0 : y.Start!.Value;
            int c = sx.CompareTo(sy);
            if (c != 0) return c;
            c = x.Kind.CompareTo(y.Kind);
            return c != 0 ? c : string.Compare(x.Title, y.Title, StringComparison.CurrentCulture);
        });
        StableSort(a.DayTasks, (x, y) => string.Compare(x.ContainerName, y.ContainerName, StringComparison.CurrentCulture));
        StableSort(a.Overdue, (x, y) => Nullable.Compare(x.Start, y.Start));
        StableSort(a.Completed, (x, y) => Nullable.Compare(y.CompletedAt, x.CompletedAt));
        return a;
    }

    private static void StableSort<T>(List<T> list, Comparison<T> cmp)
    {
        var sorted = list.Select((v, i) => (v, i)).OrderBy(p => p, Comparer<(T v, int i)>.Create((a, b) =>
        {
            int c = cmp(a.v, b.v);
            return c != 0 ? c : a.i.CompareTo(b.i);
        })).Select(p => p.v).ToList();
        list.Clear();
        list.AddRange(sorted);
    }
}
