using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using DailyMemo.Core;
using Microsoft.Toolkit.Uwp.Notifications;

namespace DailyMemo.Services;

/// <summary>
/// Windows 通知：每天第一次开机（或到设定时间）推送当天安排；日程/带时间的待办开始前提醒。
/// </summary>
public sealed class NotificationService
{
    private readonly SettingsService _settings;
    private readonly AgendaService _agenda;
    private readonly DispatcherTimer _timer;
    private readonly HashSet<string> _notified = new();
    private readonly DateTime _startedAt = DateTime.Now;

    public NotificationService(SettingsService settings, AgendaService agenda)
    {
        _settings = settings;
        _agenda = agenda;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _timer.Tick += (_, _) => Tick();
    }

    /// <summary>用户点击通知时触发（在 UI 线程）</summary>
    public event Action<string>? Activated;

    public void Start()
    {
        if (StartupManager.IsDevMode) return;
        ToastNotificationManagerCompat.OnActivated += e =>
        {
            var args = ToastArguments.Parse(e.Argument);
            var action = args.Contains("action") ? args["action"] : "open";
            Application.Current.Dispatcher.BeginInvoke(() => Activated?.Invoke(action));
        };
        _timer.Start();
    }

    private void Tick()
    {
        var s = _settings.Current;
        var now = DateTime.Now;

        // 等第一次同步完成（最多等 2 分钟），免得早报内容是空的
        bool ready = _agenda.LastRefresh.HasValue && (_agenda.LastRefresh > _startedAt || now - _startedAt > TimeSpan.FromMinutes(2));
        if (!ready && now - _startedAt < TimeSpan.FromMinutes(2)) return;

        var today = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (s.DailySummaryEnabled && s.LastSummaryDate != today &&
            TimeSpan.TryParse(s.DailySummaryTime, CultureInfo.InvariantCulture, out var at) && now.TimeOfDay >= at)
        {
            s.LastSummaryDate = today;
            _settings.Save();
            ShowDailySummary();
        }

        if (s.EventReminderEnabled) CheckReminders(now, Math.Max(0, s.EventReminderMinutes));
    }

    private void CheckReminders(DateTime now, int minutes)
    {
        var horizon = now.AddMinutes(minutes);
        var items = _agenda.Day(now.Date, false).Scheduled.AsEnumerable();
        if (horizon.Date > now.Date) items = items.Concat(_agenda.Day(horizon.Date, false).Scheduled);

        foreach (var it in items)
        {
            if (it.Completed || !it.Start.HasValue) continue;
            var start = it.Start.Value;
            if (start < now.AddSeconds(-45) || start > horizon) continue;
            if (!_notified.Add($"{it.Key}|{start.Ticks}")) continue;
            ShowReminder(it, start, now);
        }
    }

    public void ShowDailySummary()
    {
        var day = _agenda.Day(DateTime.Today, includeCarryOver: true);
        var parts = new List<string>();
        if (day.EventCount > 0) parts.Add($"{day.EventCount} 个日程");
        if (day.OpenTaskCount > 0) parts.Add($"{day.OpenTaskCount} 项待办");
        if (day.Overdue.Count > 0) parts.Add($"{day.Overdue.Count} 项已过期");

        string greeting = DateTime.Now.Hour < 11 ? "早上好" : "今日安排";
        string line1 = parts.Count > 0 ? string.Join(" · ", parts) : "今天没有安排，放松一下～";
        var top = day.AllDayEvents.Concat(day.Scheduled).Concat(day.Overdue).Concat(day.DayTasks)
            .Where(i => !i.Completed).Take(4)
            .Select(i => i.HasTime ? $"{i.Start:HH:mm} {i.Title}" : i.Title)
            .ToList();

        var builder = new ToastContentBuilder()
            .AddArgument("action", "open")
            .AddText($"{greeting} · {ChineseDate.DayTitle(DateTime.Today)}")
            .AddText(line1);
        if (top.Count > 0) builder.AddText(string.Join("、", top));
        builder.AddButton(new ToastButton().SetContent("打开今日事").AddArgument("action", "open"));
        Show(builder);
    }

    public void ShowTest()
    {
        Show(new ToastContentBuilder()
            .AddArgument("action", "open")
            .AddText("今日事 · 测试通知")
            .AddText("能看到这条通知，说明提醒功能正常。"));
    }

    private void ShowReminder(AgendaItem it, DateTime start, DateTime now)
    {
        int mins = (int)Math.Ceiling((start - now).TotalMinutes);
        string head = it.IsTask ? "到时间了" : mins <= 0 ? "现在开始" : $"{mins} 分钟后";
        string range = it.IsEvent && it.End.HasValue && it.End > start
            ? $"{start:HH:mm} – {it.End:HH:mm}"
            : $"{start:HH:mm}";
        string detail = string.IsNullOrWhiteSpace(it.Location) ? range : $"{range} · {it.Location}";

        Show(new ToastContentBuilder()
            .AddArgument("action", "open")
            .AddText($"{head} · {it.Title}")
            .AddText(detail)
            .AddAttributionText(it.ContainerName));
    }

    private static void Show(ToastContentBuilder builder)
    {
        try
        {
            builder.Show();
        }
        catch (Exception ex)
        {
            Log.Error("显示通知失败", ex);
        }
    }
}
