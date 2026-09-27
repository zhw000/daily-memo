using System.Text.Json;
using DailyMemo.Core;
using DailyMemo.Services;
using Xunit;

namespace DailyMemo.Tests;

public class SourceParsingTests
{
    private static readonly SourceContainer Cal = new() { Key = "gc:work", Kind = ItemKind.Event, Source = ItemSource.Google, Id = "work", Name = "工作", Color = "#039BE5", Writable = true };
    private static readonly SourceContainer List = new() { Key = "gt:L1", Kind = ItemKind.Task, Source = ItemSource.Google, Id = "L1", Name = "我的任务" };

    [Fact]
    public void MapsTimedGoogleEvent()
    {
        var e = JsonSerializer.Deserialize<GoogleApi.GEvent>("""
            {"id":"abc","status":"confirmed","summary":"组会","location":"A栋","start":{"dateTime":"2026-09-27T10:00:00+08:00"},"end":{"dateTime":"2026-09-27T11:30:00+08:00"}}
            """)!;
        var item = GoogleApi.MapEvent(e, Cal)!;
        Assert.Equal("组会", item.Title);
        Assert.False(item.AllDay);
        Assert.Equal(new DateTimeOffset(2026, 9, 27, 10, 0, 0, TimeSpan.FromHours(8)).LocalDateTime, item.Start);
        Assert.Equal(TimeSpan.FromMinutes(90), item.End - item.Start);
        Assert.Equal("gcal|work|abc", item.Key);
    }

    [Fact]
    public void MapsAllDayGoogleEvent()
    {
        var e = JsonSerializer.Deserialize<GoogleApi.GEvent>("""
            {"id":"h","summary":"国庆节","start":{"date":"2026-10-01"},"end":{"date":"2026-10-08"}}
            """)!;
        var item = GoogleApi.MapEvent(e, Cal)!;
        Assert.True(item.AllDay);
        Assert.Equal(new DateTime(2026, 10, 1), item.Start);
        Assert.Equal(new DateTime(2026, 10, 8), item.End);

        var day = AgendaBuilder.Build(new[] { item }, new DateTime(2026, 10, 7), false);
        Assert.Single(day.AllDayEvents);
        var after = AgendaBuilder.Build(new[] { item }, new DateTime(2026, 10, 8), false);
        Assert.Empty(after.AllDayEvents);
    }

    [Fact]
    public void GoogleTaskDueIsDateOnlyAndTitlePrefixCarriesTime()
    {
        var t = JsonSerializer.Deserialize<GoogleApi.GTask>("""
            {"id":"t1","title":"15:00 交报告","status":"needsAction","due":"2026-09-28T00:00:00.000Z"}
            """)!;
        var item = GoogleApi.MapTask(t, List);
        Assert.Equal("交报告", item.Title);
        Assert.Equal(new DateTime(2026, 9, 28, 15, 0, 0), item.Start);
        Assert.False(item.AllDay);

        var plain = GoogleApi.MapTask(JsonSerializer.Deserialize<GoogleApi.GTask>("""{"id":"t2","title":"买菜","status":"needsAction","due":"2026-09-28T00:00:00.000Z"}""")!, List);
        Assert.Equal(new DateTime(2026, 9, 28), plain.Start);
        Assert.True(plain.AllDay);

        // 没有日期时，时间前缀保留在标题里
        var noDate = GoogleApi.MapTask(JsonSerializer.Deserialize<GoogleApi.GTask>("""{"id":"t3","title":"8:00 跑步","status":"needsAction"}""")!, List);
        Assert.Equal("8:00 跑步", noDate.Title);
        Assert.Null(noDate.Start);
    }

    [Fact]
    public void ComposesTaskTitles()
    {
        Assert.Equal("15:00 交报告", GoogleApi.ComposeTaskTitle("交报告", new DateTime(2026, 9, 28, 15, 0, 0), true));
        Assert.Equal("交报告", GoogleApi.ComposeTaskTitle("09:00 交报告", new DateTime(2026, 9, 28), false));
        Assert.Equal("2026-09-28T00:00:00.000Z", GoogleApi.TaskDue(new DateTime(2026, 9, 28, 15, 0, 0)));
    }

    [Fact]
    public void ParsesOAuthRedirect()
    {
        var q = GoogleAuth.ParseQuery("/?state=abc&code=4%2F0AbCd&scope=email%20openid");
        Assert.Equal("4/0AbCd", q["code"]);
        Assert.Equal("abc", q["state"]);
        Assert.Equal("email openid", q["scope"]);
    }

    [Fact]
    public void ReadsEmailFromIdToken()
    {
        var payload = Convert.ToBase64String("{\"email\":\"me@example.com\"}"u8.ToArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Assert.Equal("me@example.com", GoogleAuth.EmailFromIdToken($"xx.{payload}.yy"));
    }

    [Fact]
    public void ExpandsRecurringICloudEvents()
    {
        var ics = string.Join("\r\n", new[]
        {
            "BEGIN:VCALENDAR", "VERSION:2.0", "PRODID:test",
            "BEGIN:VTIMEZONE", "TZID:Asia/Shanghai", "BEGIN:STANDARD", "DTSTART:19700101T000000", "TZOFFSETFROM:+0800", "TZOFFSETTO:+0800", "TZNAME:CST", "END:STANDARD", "END:VTIMEZONE",
            "BEGIN:VEVENT", "UID:weekly-1", "DTSTAMP:20260901T000000Z",
            "DTSTART;TZID=Asia/Shanghai:20260907T090000", "DTEND;TZID=Asia/Shanghai:20260907T100000",
            "RRULE:FREQ=WEEKLY;BYDAY=MO,WE", "SUMMARY:晨会", "END:VEVENT",
            "END:VCALENDAR", "",
        });
        var cal = new SourceContainer { Key = "ic:x", Kind = ItemKind.Event, Source = ItemSource.ICloud, Id = "https://example.com/cal/", Name = "家庭", Color = "#FF9500" };
        var from = new DateTime(2026, 9, 27);
        var items = CalDavClient.ExpandIcs(ics, cal, "https://example.com/cal/1.ics", null, from, from.AddDays(7)).ToList();
        // 9/28 周一、9/30 周三
        Assert.Equal(2, items.Count);
        Assert.All(items, i => Assert.Equal("晨会", i.Title));
        Assert.All(items, i => Assert.True(i.Recurring));
        var shanghai = TimeZoneInfo.FindSystemTimeZoneById("China Standard Time");
        var expected = TimeZoneInfo.ConvertTime(new DateTime(2026, 9, 28, 9, 0, 0), shanghai, TimeZoneInfo.Local);
        Assert.Contains(items, i => i.Start == expected);
    }

    [Fact]
    public void ExpandsAllDayICloudEvent()
    {
        var ics = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nBEGIN:VEVENT\r\nUID:bd\r\nDTSTAMP:20260901T000000Z\r\nDTSTART;VALUE=DATE:20260930\r\nDTEND;VALUE=DATE:20261001\r\nSUMMARY:妈妈生日\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        var cal = new SourceContainer { Key = "ic:x", Kind = ItemKind.Event, Source = ItemSource.ICloud, Id = "https://example.com/cal/", Name = "家庭" };
        var items = CalDavClient.ExpandIcs(ics, cal, "h", null, new DateTime(2026, 9, 27), new DateTime(2026, 10, 10)).ToList();
        var item = Assert.Single(items);
        Assert.True(item.AllDay);
        Assert.Equal(new DateTime(2026, 9, 30), item.Start);
        Assert.Equal(new DateTime(2026, 10, 1), item.End);
    }

    [Fact]
    public void BuildsValidIcs()
    {
        var ics = CalDavClient.BuildIcs("U1", "开会; 讨论, 方案", new DateTime(2026, 9, 28, 15, 0, 0), new DateTime(2026, 9, 28, 16, 0, 0), false, "第一行\n第二行", null);
        Assert.Contains("SUMMARY:开会\\; 讨论\\, 方案", ics);
        Assert.Contains("DESCRIPTION:第一行\\n第二行", ics);
        var parsed = Ical.Net.Calendar.Load(ics);
        Assert.Single(parsed.Events);
    }

    [Fact]
    public void AgendaGroupsOverdueUndatedAndCompleted()
    {
        var today = new DateTime(2026, 9, 27);
        var items = new[]
        {
            new AgendaItem { Key = "1", Kind = ItemKind.Task, Title = "过期", Start = today.AddDays(-2), AllDay = true },
            new AgendaItem { Key = "2", Kind = ItemKind.Task, Title = "今天", Start = today, AllDay = true },
            new AgendaItem { Key = "3", Kind = ItemKind.Task, Title = "带时间", Start = today.AddHours(15), AllDay = false },
            new AgendaItem { Key = "4", Kind = ItemKind.Task, Title = "无日期" },
            new AgendaItem { Key = "5", Kind = ItemKind.Task, Title = "完成了", Start = today, Completed = true, CompletedAt = today.AddHours(9) },
            new AgendaItem { Key = "6", Kind = ItemKind.Event, Title = "跨夜", Start = today.AddDays(-1).AddHours(22), End = today.AddHours(2) },
        };
        var d = AgendaBuilder.Build(items, today, true);
        Assert.Equal("过期", Assert.Single(d.Overdue).Title);
        Assert.Equal("今天", Assert.Single(d.DayTasks).Title);
        Assert.Equal("无日期", Assert.Single(d.Undated).Title);
        Assert.Equal("完成了", Assert.Single(d.Completed).Title);
        Assert.Equal(new[] { "跨夜", "带时间" }, d.Scheduled.Select(i => i.Title));
        Assert.Equal(2, d.OpenTaskCount);
        Assert.Equal(1, d.EventCount);
    }
}
