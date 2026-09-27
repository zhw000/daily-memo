using System;
using System.Collections.Generic;
using System.Linq;
using DailyMemo.Core;

namespace DailyMemo.Services;

/// <summary>未登录 Google 时使用的本机待办（不同步）。登录后可一键迁移到 Google Tasks。</summary>
public sealed class LocalTaskStore
{
    public const string Color = "#7C5CFA";
    public const string Name = "本机待办";

    public sealed class LocalTask
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Title { get; set; } = "";
        public string? Notes { get; set; }
        public DateTime? Due { get; set; }
        public bool HasTime { get; set; }
        public bool Completed { get; set; }
        public DateTime? CompletedAt { get; set; }
        public DateTime Created { get; set; } = DateTime.Now;
    }

    private List<LocalTask> _tasks;

    public LocalTaskStore()
    {
        _tasks = JsonFile.Load(AppPaths.LocalTasks, () => new List<LocalTask>());
    }

    public static SourceContainer Container => new()
    {
        Key = ContainerKeys.Local,
        Kind = ItemKind.Task,
        Source = ItemSource.Local,
        Id = "",
        Name = Name,
        Color = Color,
        Writable = true,
        DefaultVisible = true,
    };

    public int OpenCount => _tasks.Count(t => !t.Completed);

    public List<AgendaItem> GetItems()
    {
        // 完成超过 7 天的本机待办自动清理
        var cutoff = DateTime.Today.AddDays(-7);
        if (_tasks.RemoveAll(t => t.Completed && (t.CompletedAt ?? t.Created) < cutoff) > 0) Save();
        return _tasks.Select(ToItem).ToList();
    }

    public AgendaItem Add(string title, string? notes, DateTime? due, bool hasTime)
    {
        var t = new LocalTask { Title = title, Notes = notes, Due = due, HasTime = hasTime && due.HasValue };
        _tasks.Add(t);
        Save();
        return ToItem(t);
    }

    public AgendaItem? Update(string id, string title, string? notes, DateTime? due, bool hasTime)
    {
        var t = _tasks.FirstOrDefault(x => x.Id == id);
        if (t == null) return null;
        t.Title = title;
        t.Notes = notes;
        t.Due = due;
        t.HasTime = hasTime && due.HasValue;
        Save();
        return ToItem(t);
    }

    public void SetCompleted(string id, bool completed)
    {
        var t = _tasks.FirstOrDefault(x => x.Id == id);
        if (t == null) return;
        t.Completed = completed;
        t.CompletedAt = completed ? DateTime.Now : null;
        Save();
    }

    public void Delete(string id)
    {
        if (_tasks.RemoveAll(x => x.Id == id) > 0) Save();
    }

    private void Save() => JsonFile.Save(AppPaths.LocalTasks, _tasks);

    private static AgendaItem ToItem(LocalTask t) => new()
    {
        Key = "local|" + t.Id,
        Kind = ItemKind.Task,
        Source = ItemSource.Local,
        Id = t.Id,
        ContainerId = "",
        ContainerName = Name,
        Color = Color,
        Title = t.Title,
        Notes = t.Notes,
        Start = t.Due.HasValue ? (t.HasTime ? t.Due : t.Due.Value.Date) : null,
        AllDay = !t.HasTime,
        Completed = t.Completed,
        CompletedAt = t.CompletedAt,
    };
}
