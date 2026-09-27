using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DailyMemo.Core;

namespace DailyMemo.Services;

/// <summary>
/// 汇总所有来源（本机待办 / Google 日历 / Google Tasks / iCloud 日历），负责刷新、缓存和增删改。
/// 所有公开方法都在 UI 线程调用，内部状态不需要加锁。
/// </summary>
public sealed class AgendaService
{
    public const int RangeDays = 15;

    private const string SrcLocal = "local";
    private const string SrcGCal = "gcal";
    private const string SrcGTasks = "gtasks";
    private const string SrcICloud = "icloud";

    private readonly SettingsService _settings;
    private readonly GoogleAuth _auth;
    private readonly GoogleApi _google;
    private readonly CalDavClient _caldav;
    private readonly LocalTaskStore _local;
    private readonly SynchronizationContext? _ui;
    private readonly Dictionary<string, SourceState> _states = new();
    private List<SourceContainer>? _icloudCalendars;
    private DateTime _icloudCalendarsAt;
    private bool _refreshQueued;

    public sealed class SourceState
    {
        public List<AgendaItem> Items { get; set; } = new();
        public List<SourceContainer> Containers { get; set; } = new();
        public string? Error { get; set; }
        public DateTime? LastSuccess { get; set; }
    }

    private sealed class CacheFile
    {
        public int Version { get; set; } = 1;
        public DateTime? LastRefresh { get; set; }
        public Dictionary<string, SourceState> States { get; set; } = new();
    }

    public AgendaService(SettingsService settings, GoogleAuth auth, GoogleApi google, CalDavClient caldav, LocalTaskStore local, bool demo = false)
    {
        Demo = demo;
        _settings = settings;
        _auth = auth;
        _google = google;
        _caldav = caldav;
        _local = local;
        _ui = SynchronizationContext.Current;

        var cache = JsonFile.Load(AppPaths.Cache, () => new CacheFile());
        foreach (var (key, state) in cache.States)
            if (key != SrcLocal) _states[key] = state;
        LastRefresh = cache.LastRefresh;
        _states[SrcLocal] = LocalState();

        _auth.StateChanged += (_, _) => OnUi(() =>
        {
            if (!_auth.IsSignedIn)
            {
                _states.Remove(SrcGCal);
                _states.Remove(SrcGTasks);
                RaiseChanged();
            }
            _ = RefreshAsync();
        });
    }

    public event EventHandler? Changed;

    /// <summary>预览模式：不联网、不写文件</summary>
    public bool Demo { get; }

    public bool IsRefreshing { get; private set; }
    public DateTime? LastRefresh { get; private set; }

    public bool GoogleConnected => _auth.IsSignedIn;
    public bool ICloudConnected => _settings.ICloudConfigured;

    // ───────────── 查询 ─────────────

    public IReadOnlyList<SourceContainer> Containers =>
        new[] { SrcLocal, SrcGTasks, SrcGCal, SrcICloud }
            .Where(_states.ContainsKey)
            .SelectMany(k => _states[k].Containers)
            .ToList();

    public IReadOnlyList<SourceContainer> TaskContainers => Containers.Where(c => c.Kind == ItemKind.Task).ToList();

    public IReadOnlyList<SourceContainer> EventContainers => Containers.Where(c => c.Kind == ItemKind.Event && c.Writable).ToList();

    public SourceContainer? FindContainer(string key) => Containers.FirstOrDefault(c => c.Key == key);

    public bool IsVisible(SourceContainer c) =>
        _settings.Current.Visibility.TryGetValue(c.Key, out var v) ? v : c.DefaultVisible;

    public List<AgendaItem> VisibleItems()
    {
        var visible = Containers.ToDictionary(c => c.Key, IsVisible);
        return _states.Values.SelectMany(s => s.Items)
            .Where(i => !visible.TryGetValue(i.ContainerKey, out var v) || v)
            .ToList();
    }

    public DayAgenda Day(DateTime day, bool includeCarryOver) => AgendaBuilder.Build(VisibleItems(), day, includeCarryOver);

    public IReadOnlyList<(string Name, string? Error, DateTime? LastSuccess)> SourceStatuses =>
        _states.Where(kv => kv.Key != SrcLocal)
            .Select(kv => (SourceName(kv.Key), kv.Value.Error, kv.Value.LastSuccess))
            .ToList();

    public IEnumerable<string> Errors => _states.Values.Where(s => s.Error != null).Select(s => s.Error!);

    public string ResolveTaskTarget(string? preferred = null)
    {
        var key = preferred ?? _settings.Current.DefaultTaskTarget;
        var lists = TaskContainers;
        if (lists.Any(c => c.Key == key)) return key;
        // 没有指定时：优先和 iPhone「提醒事项」配对的 Google 清单（手机上会变成带闹钟的提醒），其次是第一个 Google 清单
        var google = lists.Where(c => c.Source == ItemSource.Google).ToList();
        var paired = google.FirstOrDefault(c => c.Name is "提醒事项" or "Reminders" or "提醒");
        return (paired ?? google.FirstOrDefault())?.Key ?? ContainerKeys.Local;
    }

    public string? ResolveEventTarget(string? preferred = null)
    {
        var key = preferred ?? _settings.Current.DefaultEventTarget;
        var cals = EventContainers;
        if (cals.Any(c => c.Key == key)) return key;
        return (cals.FirstOrDefault(c => c.IsPrimary) ?? cals.FirstOrDefault())?.Key;
    }

    // ───────────── 刷新 ─────────────

    public async Task RefreshAsync()
    {
        if (Demo)
        {
            RaiseChanged();
            return;
        }
        if (IsRefreshing)
        {
            _refreshQueued = true;
            return;
        }
        IsRefreshing = true;
        RaiseChanged();
        try
        {
            var from = DateTime.Today.AddDays(-1);
            var to = DateTime.Today.AddDays(RangeDays);
            var s = _settings.Current;
            _states[SrcLocal] = LocalState();

            var jobs = new List<Task>();
            if (_auth.IsSignedIn && s.GoogleCalendarEnabled) jobs.Add(RunSourceAsync(SrcGCal, ct => FetchGoogleCalendarAsync(from, to, ct)));
            else _states.Remove(SrcGCal);
            if (_auth.IsSignedIn && s.GoogleTasksEnabled) jobs.Add(RunSourceAsync(SrcGTasks, FetchGoogleTasksAsync));
            else _states.Remove(SrcGTasks);
            if (_settings.ICloudConfigured) jobs.Add(RunSourceAsync(SrcICloud, ct => FetchICloudAsync(from, to, ct)));
            else _states.Remove(SrcICloud);

            await Task.WhenAll(jobs);
            LastRefresh = DateTime.Now;
            SaveCache();
        }
        finally
        {
            IsRefreshing = false;
            RaiseChanged();
            if (_refreshQueued)
            {
                _refreshQueued = false;
                _ = RefreshAsync();
            }
        }
    }

    public async Task<int> ConnectICloudAsync(string appleId, string appPassword)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        var user = appleId.Trim();
        var pwd = appPassword.Replace(" ", "").Trim();
        var (cals, server) = await _caldav.DiscoverCalendarsAsync(user, pwd, null, cts.Token);
        _settings.Current.ICloudAppleId = user;
        _settings.Current.ICloudServer = server;
        _settings.ICloudPassword = pwd;
        _settings.Save();
        _icloudCalendars = cals;
        _icloudCalendarsAt = DateTime.Now;
        await RefreshAsync();
        return cals.Count;
    }

    public void DisconnectICloud()
    {
        _settings.Current.ICloudAppleId = "";
        _settings.ICloudPassword = null;
        _settings.Save();
        _icloudCalendars = null;
        _states.Remove(SrcICloud);
        SaveCache();
        RaiseChanged();
    }

    /// <summary>界面预览用：直接填入示例数据</summary>
    internal void LoadDemo(IEnumerable<AgendaItem> items, IEnumerable<SourceContainer> containers)
    {
        _states.Clear();
        var all = items.ToList();
        var cons = containers.ToList();
        foreach (var group in cons.GroupBy(c => c.Source == ItemSource.Local ? SrcLocal
                     : c.Source == ItemSource.ICloud ? SrcICloud
                     : c.Kind == ItemKind.Task ? SrcGTasks : SrcGCal))
        {
            var keys = group.Select(c => c.Key).ToHashSet();
            _states[group.Key] = new SourceState
            {
                Containers = group.ToList(),
                Items = all.Where(i => keys.Contains(i.ContainerKey)).ToList(),
                LastSuccess = DateTime.Now,
            };
        }
        LastRefresh = DateTime.Now;
        RaiseChanged();
    }

    private async Task RunSourceAsync(string key, Func<CancellationToken, Task<(List<AgendaItem>, List<SourceContainer>)>> fetch)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        try
        {
            var (items, containers) = await fetch(cts.Token);
            _states[key] = new SourceState { Items = items, Containers = containers, LastSuccess = DateTime.Now };
        }
        catch (Exception ex)
        {
            var state = _states.TryGetValue(key, out var prev) ? prev : new SourceState();
            state.Error = FriendlyError(ex);
            _states[key] = state;
            Log.Error($"同步 {SourceName(key)} 失败", ex);
        }
    }

    private async Task<(List<AgendaItem>, List<SourceContainer>)> FetchGoogleCalendarAsync(DateTime from, DateTime to, CancellationToken ct)
    {
        var cals = await _google.GetCalendarsAsync(ct);
        var visible = cals.Where(IsVisible).ToList();
        var tasks = visible.Select(async c =>
        {
            try { return (items: await _google.GetEventsAsync(c, from, to, ct), error: (Exception?)null); }
            catch (Exception ex) when (ex is not GoogleAuthException) { return (items: new List<AgendaItem>(), error: ex); }
        }).ToList();
        var results = await Task.WhenAll(tasks);
        var failed = results.Where(r => r.error != null).ToList();
        if (failed.Count > 0 && failed.Count == results.Length) throw failed[0].error!;
        foreach (var f in failed) Log.Error("读取某个 Google 日历失败", f.error);
        return (results.SelectMany(r => r.items).ToList(), cals);
    }

    private async Task<(List<AgendaItem>, List<SourceContainer>)> FetchGoogleTasksAsync(CancellationToken ct)
    {
        var lists = await _google.GetTaskListsAsync(ct);
        var results = await Task.WhenAll(lists.Select(l => _google.GetTasksAsync(l, ct)));
        return (results.SelectMany(x => x).ToList(), lists);
    }

    private async Task<(List<AgendaItem>, List<SourceContainer>)> FetchICloudAsync(DateTime from, DateTime to, CancellationToken ct)
    {
        var user = _settings.Current.ICloudAppleId.Trim();
        var pwd = _settings.ICloudPassword ?? "";
        if (_icloudCalendars == null || DateTime.Now - _icloudCalendarsAt > TimeSpan.FromHours(6))
        {
            var (cals0, server) = await _caldav.DiscoverCalendarsAsync(user, pwd, _settings.Current.ICloudServer, ct);
            _icloudCalendars = cals0;
            _icloudCalendarsAt = DateTime.Now;
            if (_settings.Current.ICloudServer != server)
            {
                _settings.Current.ICloudServer = server;
                _settings.Save();
            }
        }
        var cals = _icloudCalendars;
        var results = await Task.WhenAll(cals.Where(IsVisible).Select(c => _caldav.GetEventsAsync(c, user, pwd, from, to, ct)));
        return (results.SelectMany(x => x).ToList(), cals);
    }

    // ───────────── 修改 ─────────────

    public async Task ToggleCompletedAsync(AgendaItem item)
    {
        if (!item.IsTask) return;
        var updated = item.Clone();
        updated.Completed = !item.Completed;
        updated.CompletedAt = updated.Completed ? DateTime.Now : null;
        Replace(item, updated);
        try
        {
            switch (item.Source)
            {
                case ItemSource.Local:
                    _local.SetCompleted(item.Id, updated.Completed);
                    break;
                case ItemSource.Google:
                    await _google.SetTaskCompletedAsync(item.ContainerId, item.Id, updated.Completed, Timeout());
                    break;
            }
            SaveCache();
        }
        catch
        {
            Replace(updated, item);
            throw;
        }
    }

    public async Task<AgendaItem> AddTaskAsync(string title, DateTime? due, bool hasTime, string? notes, string? targetKey = null)
    {
        var key = ResolveTaskTarget(targetKey);
        AgendaItem item;
        if (key == ContainerKeys.Local)
        {
            item = _local.Add(title, notes, due, hasTime);
        }
        else
        {
            var list = FindContainer(key) ?? throw new InvalidOperationException("找不到这个待办清单，请刷新后重试。");
            item = await _google.InsertTaskAsync(list, title, notes, due, hasTime, Timeout());
        }
        Add(item);
        return item;
    }

    public async Task<AgendaItem> AddEventAsync(string title, DateTime start, DateTime end, bool allDay, string? notes, string? location, string? targetKey = null)
    {
        var key = ResolveEventTarget(targetKey) ?? throw new InvalidOperationException("还没有可写入的日历：请先在设置里连接 Google 或 iCloud 日历。");
        var cal = FindContainer(key) ?? throw new InvalidOperationException("找不到这个日历，请刷新后重试。");
        AgendaItem item = cal.Source switch
        {
            ItemSource.Google => await _google.InsertEventAsync(cal, title, start, end, allDay, notes, location, Timeout()),
            ItemSource.ICloud => await _caldav.CreateEventAsync(cal, _settings.Current.ICloudAppleId.Trim(), _settings.ICloudPassword ?? "",
                title, start, end, allDay, notes, location, Timeout()),
            _ => throw new InvalidOperationException("这个位置不能保存日程"),
        };
        Add(item);
        return item;
    }

    /// <summary>保存编辑。改了保存位置时会在新位置新建、旧位置删除。</summary>
    public async Task<AgendaItem> UpdateAsync(AgendaItem original, AgendaItem edited, string targetKey)
    {
        if (targetKey != original.ContainerKey)
        {
            AgendaItem created = edited.IsTask
                ? await AddTaskAsync(edited.Title, edited.Start, edited.HasTime, edited.Notes, targetKey)
                : await AddEventAsync(edited.Title, edited.Start!.Value, edited.End ?? edited.Start!.Value, edited.AllDay, edited.Notes, edited.Location, targetKey);
            if (original.Completed && created.IsTask) await ToggleCompletedAsync(created);
            await DeleteAsync(original);
            return created;
        }

        AgendaItem result;
        switch (original.Source)
        {
            case ItemSource.Local:
                result = _local.Update(original.Id, edited.Title, edited.Notes, edited.Start, edited.HasTime) ?? edited;
                break;
            case ItemSource.Google when original.IsTask:
            {
                var list = FindContainer(original.ContainerKey) ?? throw new InvalidOperationException("找不到这个待办清单");
                result = await _google.UpdateTaskAsync(list, original.Id, edited.Title, edited.Notes, edited.Start, edited.HasTime, Timeout());
                result.Completed = original.Completed;
                result.CompletedAt = original.CompletedAt;
                break;
            }
            case ItemSource.Google:
            {
                var cal = FindContainer(original.ContainerKey) ?? throw new InvalidOperationException("找不到这个日历");
                result = await _google.UpdateEventAsync(cal, original.Id, edited.Title, edited.Start!.Value, edited.End ?? edited.Start!.Value,
                    edited.AllDay, edited.Notes, edited.Location, Timeout());
                break;
            }
            default:
                throw new InvalidOperationException("iCloud 日程请在 iPhone 或 Mac 的「日历」里编辑。");
        }
        Replace(original, result);
        return result;
    }

    public async Task DeleteAsync(AgendaItem item)
    {
        switch (item.Source)
        {
            case ItemSource.Local:
                _local.Delete(item.Id);
                break;
            case ItemSource.Google when item.IsTask:
                await _google.DeleteTaskAsync(item.ContainerId, item.Id, Timeout());
                break;
            case ItemSource.Google:
                await _google.DeleteEventAsync(item.ContainerId, item.Id, Timeout());
                break;
            case ItemSource.ICloud:
                if (item.Recurring) throw new InvalidOperationException("重复的 iCloud 日程请在 iPhone「日历」里删除。");
                await _caldav.DeleteAsync(item.Href ?? throw new InvalidOperationException("缺少日程地址"),
                    _settings.Current.ICloudAppleId.Trim(), _settings.ICloudPassword ?? "", Timeout());
                break;
        }
        foreach (var s in _states.Values) s.Items.RemoveAll(i => i.Key == item.Key);
        SaveCache();
        RaiseChanged();
    }

    /// <summary>把本机待办搬到 Google Tasks，这样手机上也能看到</summary>
    public async Task<int> MigrateLocalTasksAsync(string targetKey)
    {
        var list = FindContainer(targetKey) ?? throw new InvalidOperationException("找不到目标清单");
        int n = 0;
        foreach (var t in _local.GetItems().Where(i => !i.Completed).ToList())
        {
            await _google.InsertTaskAsync(list, t.Title, t.Notes, t.Start, t.HasTime, Timeout());
            _local.Delete(t.Id);
            n++;
        }
        await RefreshAsync();
        return n;
    }

    public int LocalOpenCount => _local.OpenCount;

    // ───────────── 内部 ─────────────

    private SourceState LocalState() => new()
    {
        Items = _local.GetItems(),
        Containers = new List<SourceContainer> { LocalTaskStore.Container },
        LastSuccess = DateTime.Now,
    };

    private void Add(AgendaItem item)
    {
        var key = item.Source switch
        {
            ItemSource.Local => SrcLocal,
            ItemSource.Google => item.IsTask ? SrcGTasks : SrcGCal,
            _ => SrcICloud,
        };
        if (!_states.TryGetValue(key, out var state)) _states[key] = state = new SourceState();
        state.Items.RemoveAll(i => i.Key == item.Key);
        state.Items.Add(item);
        SaveCache();
        RaiseChanged();
    }

    private void Replace(AgendaItem oldItem, AgendaItem newItem)
    {
        foreach (var s in _states.Values)
        {
            int idx = s.Items.FindIndex(i => i.Key == oldItem.Key);
            if (idx >= 0)
            {
                s.Items[idx] = newItem;
                break;
            }
        }
        RaiseChanged();
    }

    private void SaveCache()
    {
        if (Demo) return;
        try
        {
            var cache = new CacheFile { LastRefresh = LastRefresh };
            foreach (var (k, v) in _states)
                if (k != SrcLocal) cache.States[k] = v;
            JsonFile.Save(AppPaths.Cache, cache);
        }
        catch (Exception ex)
        {
            Log.Error("保存缓存失败", ex);
        }
    }

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    private void OnUi(Action action)
    {
        if (_ui == null || SynchronizationContext.Current == _ui) action();
        else _ui.Post(_ => action(), null);
    }

    private static CancellationToken Timeout() => new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token;

    private static string SourceName(string key) => key switch
    {
        SrcGCal => "Google 日历",
        SrcGTasks => "Google Tasks",
        SrcICloud => "iCloud 日历",
        _ => "本机待办",
    };

    public static string FriendlyError(Exception ex) => ex switch
    {
        GoogleAuthException or GoogleApiException or CalDavException or InvalidOperationException => ex.Message,
        HttpRequestException => "网络连接失败，请检查网络（Google 服务需要能访问外网）。",
        TaskCanceledException or OperationCanceledException => "连接超时，请检查网络。",
        _ => ex.Message,
    };
}
