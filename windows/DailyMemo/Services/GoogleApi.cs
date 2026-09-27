using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using DailyMemo.Core;

namespace DailyMemo.Services;

public sealed class GoogleApiException : Exception
{
    public HttpStatusCode Status { get; }
    public GoogleApiException(HttpStatusCode status, string message) : base(message) => Status = status;
}

/// <summary>Google 日历 v3 和 Google Tasks v1 的 REST 调用</summary>
public sealed class GoogleApi
{
    private const string CalendarBase = "https://www.googleapis.com/calendar/v3";
    private const string TasksBase = "https://tasks.googleapis.com/tasks/v1";
    public const string TasksColor = "#1A73E8";

    private readonly Func<HttpClient> _http;
    private readonly GoogleAuth _auth;

    public GoogleApi(Func<HttpClient> http, GoogleAuth auth)
    {
        _http = http;
        _auth = auth;
    }

    // ───────────── 日历 ─────────────

    public async Task<List<SourceContainer>> GetCalendarsAsync(CancellationToken ct)
    {
        var list = new List<SourceContainer>();
        string? page = null;
        do
        {
            var url = $"{CalendarBase}/users/me/calendarList?maxResults=250" + (page != null ? "&pageToken=" + Uri.EscapeDataString(page) : "");
            var resp = await SendAsync<GCalendarList>(HttpMethod.Get, url, null, ct);
            foreach (var c in resp.Items ?? new())
            {
                if (c.Deleted == true || string.IsNullOrEmpty(c.Id)) continue;
                list.Add(new SourceContainer
                {
                    Key = ContainerKeys.For(ItemSource.Google, ItemKind.Event, c.Id),
                    Kind = ItemKind.Event,
                    Source = ItemSource.Google,
                    Id = c.Id,
                    Name = !string.IsNullOrWhiteSpace(c.SummaryOverride) ? c.SummaryOverride! : c.Summary ?? c.Id,
                    Color = NormalizeColor(c.BackgroundColor, "#4285F4"),
                    Writable = c.AccessRole is "owner" or "writer",
                    DefaultVisible = c.Primary == true || c.Selected == true,
                    IsPrimary = c.Primary == true,
                });
            }
            page = resp.NextPageToken;
        } while (page != null);

        return list.OrderByDescending(c => c.IsPrimary).ThenBy(c => c.Name, StringComparer.CurrentCulture).ToList();
    }

    public async Task<List<AgendaItem>> GetEventsAsync(SourceContainer cal, DateTime from, DateTime to, CancellationToken ct)
    {
        var items = new List<AgendaItem>();
        string? page = null;
        do
        {
            var url = $"{CalendarBase}/calendars/{Uri.EscapeDataString(cal.Id)}/events" +
                      $"?singleEvents=true&orderBy=startTime&maxResults=250" +
                      $"&timeMin={Uri.EscapeDataString(Rfc3339(from))}&timeMax={Uri.EscapeDataString(Rfc3339(to))}" +
                      (page != null ? "&pageToken=" + Uri.EscapeDataString(page) : "");
            var resp = await SendAsync<GEvents>(HttpMethod.Get, url, null, ct);
            foreach (var e in resp.Items ?? new())
            {
                if (e.Status == "cancelled" || e.Start == null) continue;
                var item = MapEvent(e, cal);
                if (item != null) items.Add(item);
            }
            page = resp.NextPageToken;
        } while (page != null);
        return items;
    }

    public async Task<AgendaItem> InsertEventAsync(SourceContainer cal, string title, DateTime start, DateTime end, bool allDay,
        string? notes, string? location, CancellationToken ct)
    {
        var body = EventBody(title, start, end, allDay, notes, location, patch: false);
        var url = $"{CalendarBase}/calendars/{Uri.EscapeDataString(cal.Id)}/events";
        var e = await SendAsync<GEvent>(HttpMethod.Post, url, body, ct);
        return MapEvent(e, cal) ?? throw new GoogleApiException(HttpStatusCode.OK, "Google 返回的日程数据不完整");
    }

    public async Task<AgendaItem> UpdateEventAsync(SourceContainer cal, string eventId, string title, DateTime start, DateTime end, bool allDay,
        string? notes, string? location, CancellationToken ct)
    {
        var body = EventBody(title, start, end, allDay, notes, location, patch: true);
        var url = $"{CalendarBase}/calendars/{Uri.EscapeDataString(cal.Id)}/events/{Uri.EscapeDataString(eventId)}";
        var e = await SendAsync<GEvent>(HttpMethod.Patch, url, body, ct);
        return MapEvent(e, cal) ?? throw new GoogleApiException(HttpStatusCode.OK, "Google 返回的日程数据不完整");
    }

    public Task DeleteEventAsync(string calendarId, string eventId, CancellationToken ct) =>
        SendRawAsync(HttpMethod.Delete, $"{CalendarBase}/calendars/{Uri.EscapeDataString(calendarId)}/events/{Uri.EscapeDataString(eventId)}", null, ct);

    /// <param name="patch">PATCH 会合并字段，所以要显式把另一种时间格式置空</param>
    internal static JsonObject EventBody(string title, DateTime start, DateTime end, bool allDay, string? notes, string? location, bool patch)
    {
        var body = new JsonObject
        {
            ["summary"] = title,
            ["description"] = notes ?? "",
            ["location"] = location ?? "",
        };
        JsonObject Time(string key, string value, bool withZone)
        {
            var o = new JsonObject { [key] = value };
            if (withZone) o["timeZone"] = TimeZoneId();
            if (patch) o[key == "date" ? "dateTime" : "date"] = null;
            return o;
        }
        if (allDay)
        {
            var endDate = end.Date <= start.Date ? start.Date.AddDays(1) : end.Date;
            body["start"] = Time("date", start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), false);
            body["end"] = Time("date", endDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), false);
        }
        else
        {
            if (end <= start) end = start.AddHours(1);
            body["start"] = Time("dateTime", Rfc3339(start), true);
            body["end"] = Time("dateTime", Rfc3339(end), true);
        }
        return body;
    }

    internal static AgendaItem? MapEvent(GEvent e, SourceContainer cal)
    {
        DateTime start, end;
        bool allDay = e.Start?.Date != null;
        if (allDay)
        {
            if (!DateTime.TryParseExact(e.Start!.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out start)) return null;
            if (e.End?.Date == null || !DateTime.TryParseExact(e.End.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out end))
                end = start.AddDays(1);
        }
        else
        {
            if (!DateTimeOffset.TryParse(e.Start?.DateTime, CultureInfo.InvariantCulture, DateTimeStyles.None, out var s)) return null;
            start = s.LocalDateTime;
            end = DateTimeOffset.TryParse(e.End?.DateTime, CultureInfo.InvariantCulture, DateTimeStyles.None, out var en) ? en.LocalDateTime : start;
        }

        return new AgendaItem
        {
            Key = $"gcal|{cal.Id}|{e.Id}",
            Kind = ItemKind.Event,
            Source = ItemSource.Google,
            Id = e.Id ?? "",
            ContainerId = cal.Id,
            ContainerName = cal.Name,
            Color = string.IsNullOrEmpty(e.ColorId) ? cal.Color : EventColor(e.ColorId, cal.Color),
            Title = string.IsNullOrWhiteSpace(e.Summary) ? "（无标题）" : e.Summary!,
            Notes = string.IsNullOrWhiteSpace(e.Description) ? null : e.Description,
            Location = string.IsNullOrWhiteSpace(e.Location) ? null : e.Location,
            Start = start,
            End = end,
            AllDay = allDay,
            Recurring = !string.IsNullOrEmpty(e.RecurringEventId),
            Link = e.HtmlLink,
            ReadOnly = !cal.Writable,
        };
    }

    // ───────────── 待办 ─────────────

    public async Task<List<SourceContainer>> GetTaskListsAsync(CancellationToken ct)
    {
        var list = new List<SourceContainer>();
        string? page = null;
        do
        {
            var url = $"{TasksBase}/users/@me/lists?maxResults=100" + (page != null ? "&pageToken=" + Uri.EscapeDataString(page) : "");
            var resp = await SendAsync<GTaskLists>(HttpMethod.Get, url, null, ct);
            foreach (var l in resp.Items ?? new())
            {
                if (string.IsNullOrEmpty(l.Id)) continue;
                list.Add(new SourceContainer
                {
                    Key = ContainerKeys.For(ItemSource.Google, ItemKind.Task, l.Id),
                    Kind = ItemKind.Task,
                    Source = ItemSource.Google,
                    Id = l.Id,
                    Name = l.Title ?? "我的任务",
                    Color = TasksColor,
                    Writable = true,
                    DefaultVisible = true,
                    IsPrimary = list.Count == 0,
                });
            }
            page = resp.NextPageToken;
        } while (page != null);
        return list;
    }

    /// <summary>取清单里未完成的任务，以及最近 7 天完成的任务</summary>
    public async Task<List<AgendaItem>> GetTasksAsync(SourceContainer list, CancellationToken ct)
    {
        var items = new List<AgendaItem>();
        var completedMin = Rfc3339(DateTime.Today.AddDays(-7));
        foreach (bool completed in new[] { false, true })
        {
            string? page = null;
            do
            {
                var url = $"{TasksBase}/lists/{Uri.EscapeDataString(list.Id)}/tasks?maxResults=100&showHidden=true" +
                          (completed ? "&showCompleted=true&completedMin=" + Uri.EscapeDataString(completedMin) : "&showCompleted=false") +
                          (page != null ? "&pageToken=" + Uri.EscapeDataString(page) : "");
                var resp = await SendAsync<GTasks>(HttpMethod.Get, url, null, ct);
                foreach (var t in resp.Items ?? new())
                {
                    if (t.Deleted == true || string.IsNullOrEmpty(t.Id)) continue;
                    bool isDone = t.Status == "completed";
                    if (isDone != completed) continue;
                    if (string.IsNullOrWhiteSpace(t.Title) && string.IsNullOrWhiteSpace(t.Notes)) continue;
                    items.Add(MapTask(t, list));
                }
                page = resp.NextPageToken;
            } while (page != null);
        }
        return items;
    }

    /// <param name="hasTime">Google Tasks 接口只存日期，具体时间写在标题开头（「15:00 交报告」），iPhone 端同样这样识别</param>
    public async Task<AgendaItem> InsertTaskAsync(SourceContainer list, string title, string? notes, DateTime? due, bool hasTime, CancellationToken ct)
    {
        var body = new JsonObject { ["title"] = ComposeTaskTitle(title, due, hasTime) };
        if (!string.IsNullOrWhiteSpace(notes)) body["notes"] = notes;
        if (due.HasValue) body["due"] = TaskDue(due.Value);
        var t = await SendAsync<GTask>(HttpMethod.Post, $"{TasksBase}/lists/{Uri.EscapeDataString(list.Id)}/tasks", body, ct);
        return MapTask(t, list);
    }

    public async Task<AgendaItem> UpdateTaskAsync(SourceContainer list, string taskId, string title, string? notes, DateTime? due, bool hasTime, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["title"] = ComposeTaskTitle(title, due, hasTime),
            ["notes"] = notes ?? "",
            ["due"] = due.HasValue ? TaskDue(due.Value) : null,
        };
        var t = await SendAsync<GTask>(HttpMethod.Patch, $"{TasksBase}/lists/{Uri.EscapeDataString(list.Id)}/tasks/{Uri.EscapeDataString(taskId)}", body, ct);
        return MapTask(t, list);
    }

    private static readonly System.Text.RegularExpressions.Regex TimePrefix =
        new(@"^\s*([01]?\d|2[0-3])[:：]([0-5]\d)\s+(.+)$", System.Text.RegularExpressions.RegexOptions.Singleline);

    internal static string ComposeTaskTitle(string title, DateTime? due, bool hasTime)
    {
        var (bare, _) = SplitTaskTitle(title);
        return due.HasValue && hasTime ? $"{due.Value:HH:mm} {bare}" : bare;
    }

    /// <summary>「15:00 交报告」→（交报告, 15:00）</summary>
    internal static (string title, TimeSpan? time) SplitTaskTitle(string? raw)
    {
        var text = raw ?? "";
        var m = TimePrefix.Match(text);
        if (!m.Success) return (text.Trim(), null);
        return (m.Groups[3].Value.Trim(), new TimeSpan(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), 0));
    }

    public Task SetTaskCompletedAsync(string listId, string taskId, bool completed, CancellationToken ct)
    {
        var body = completed
            ? new JsonObject { ["status"] = "completed" }
            : new JsonObject { ["status"] = "needsAction", ["completed"] = null };
        return SendRawAsync(HttpMethod.Patch, $"{TasksBase}/lists/{Uri.EscapeDataString(listId)}/tasks/{Uri.EscapeDataString(taskId)}", body, ct);
    }

    public Task DeleteTaskAsync(string listId, string taskId, CancellationToken ct) =>
        SendRawAsync(HttpMethod.Delete, $"{TasksBase}/lists/{Uri.EscapeDataString(listId)}/tasks/{Uri.EscapeDataString(taskId)}", null, ct);

    internal static AgendaItem MapTask(GTask t, SourceContainer list)
    {
        DateTime? due = null;
        bool allDay = true;
        var (title, time) = SplitTaskTitle(t.Title);
        if (!string.IsNullOrEmpty(t.Due) && t.Due.Length >= 10 &&
            DateTime.TryParseExact(t.Due[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
        {
            due = d;
            if (time.HasValue)
            {
                due = d + time.Value;
                allDay = false;
            }
        }
        else if (time.HasValue)
        {
            // 没有日期时，时间前缀原样保留在标题里
            title = (t.Title ?? "").Trim();
        }
        DateTime? completedAt = null;
        if (DateTimeOffset.TryParse(t.Completed, CultureInfo.InvariantCulture, DateTimeStyles.None, out var c)) completedAt = c.LocalDateTime;

        return new AgendaItem
        {
            Key = $"gtask|{list.Id}|{t.Id}",
            Kind = ItemKind.Task,
            Source = ItemSource.Google,
            Id = t.Id ?? "",
            ContainerId = list.Id,
            ContainerName = list.Name,
            Color = TasksColor,
            Title = string.IsNullOrWhiteSpace(title) ? "（无标题）" : title,
            Notes = string.IsNullOrWhiteSpace(t.Notes) ? null : t.Notes,
            Start = due,
            AllDay = allDay,
            Completed = t.Status == "completed",
            CompletedAt = completedAt,
            Link = t.WebViewLink,
        };
    }

    // ───────────── 基础 ─────────────

    private async Task<T> SendAsync<T>(HttpMethod method, string url, JsonNode? body, CancellationToken ct) where T : new()
    {
        var text = await SendRawAsync(method, url, body, ct);
        if (string.IsNullOrWhiteSpace(text)) return new T();
        return JsonSerializer.Deserialize<T>(text) ?? new T();
    }

    private async Task<string> SendRawAsync(HttpMethod method, string url, JsonNode? body, CancellationToken ct)
    {
        for (int attempt = 0; ; attempt++)
        {
            var token = await _auth.GetAccessTokenAsync(ct, forceRefresh: attempt > 0);
            using var req = new HttpRequestMessage(method, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (body != null) req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

            using var resp = await _http().SendAsync(req, ct);
            var text = await resp.Content.ReadAsStringAsync(ct);
            if (resp.StatusCode == HttpStatusCode.Unauthorized && attempt == 0) continue;
            if (resp.StatusCode == HttpStatusCode.TooManyRequests && attempt < 2)
            {
                await Task.Delay(1500 * (attempt + 1), ct);
                continue;
            }
            if (!resp.IsSuccessStatusCode)
            {
                if (method == HttpMethod.Delete && resp.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone) return "";
                throw new GoogleApiException(resp.StatusCode, DescribeError(resp.StatusCode, text));
            }
            return text;
        }
    }

    private static string DescribeError(HttpStatusCode status, string body)
    {
        string? message = null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.Object &&
                err.TryGetProperty("message", out var msg))
                message = msg.GetString();
        }
        catch { }

        return status switch
        {
            HttpStatusCode.Forbidden when message?.Contains("has not been used", StringComparison.OrdinalIgnoreCase) == true
                || message?.Contains("is disabled", StringComparison.OrdinalIgnoreCase) == true
                => "Google 项目里还没有启用对应的 API（Calendar API / Tasks API），请按教程启用。",
            HttpStatusCode.Forbidden => "没有权限：" + (message ?? "请重新登录并勾选所有权限"),
            HttpStatusCode.NotFound => "找不到这条数据，可能已在别处删除。",
            _ => $"Google 返回错误（{(int)status}）：{message ?? "未知错误"}",
        };
    }

    internal static string Rfc3339(DateTime local) =>
        new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Local)).ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);

    internal static string TaskDue(DateTime date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "T00:00:00.000Z";

    private static string TimeZoneId()
    {
        if (TimeZoneInfo.TryConvertWindowsIdToIanaId(TimeZoneInfo.Local.Id, out var iana)) return iana;
        return "Asia/Shanghai";
    }

    internal static string NormalizeColor(string? color, string fallback)
    {
        if (string.IsNullOrWhiteSpace(color) || color[0] != '#') return fallback;
        return color.Length >= 7 ? color[..7].ToUpperInvariant() : fallback;
    }

    // Google 日历事件自定义颜色（colorId 1-11）
    private static string EventColor(string colorId, string fallback) => colorId switch
    {
        "1" => "#7986CB",
        "2" => "#33B679",
        "3" => "#8E24AA",
        "4" => "#E67C73",
        "5" => "#F6BF26",
        "6" => "#F4511E",
        "7" => "#039BE5",
        "8" => "#616161",
        "9" => "#3F51B5",
        "10" => "#0B8043",
        "11" => "#D50000",
        _ => fallback,
    };

    // ───────────── 数据结构 ─────────────

    internal sealed class GCalendarList
    {
        [JsonPropertyName("items")] public List<GCalendarListEntry>? Items { get; set; }
        [JsonPropertyName("nextPageToken")] public string? NextPageToken { get; set; }
    }

    internal sealed class GCalendarListEntry
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("summary")] public string? Summary { get; set; }
        [JsonPropertyName("summaryOverride")] public string? SummaryOverride { get; set; }
        [JsonPropertyName("backgroundColor")] public string? BackgroundColor { get; set; }
        [JsonPropertyName("accessRole")] public string? AccessRole { get; set; }
        [JsonPropertyName("primary")] public bool? Primary { get; set; }
        [JsonPropertyName("selected")] public bool? Selected { get; set; }
        [JsonPropertyName("deleted")] public bool? Deleted { get; set; }
    }

    internal sealed class GEvents
    {
        [JsonPropertyName("items")] public List<GEvent>? Items { get; set; }
        [JsonPropertyName("nextPageToken")] public string? NextPageToken { get; set; }
    }

    internal sealed class GEvent
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("summary")] public string? Summary { get; set; }
        [JsonPropertyName("description")] public string? Description { get; set; }
        [JsonPropertyName("location")] public string? Location { get; set; }
        [JsonPropertyName("htmlLink")] public string? HtmlLink { get; set; }
        [JsonPropertyName("colorId")] public string? ColorId { get; set; }
        [JsonPropertyName("recurringEventId")] public string? RecurringEventId { get; set; }
        [JsonPropertyName("start")] public GEventTime? Start { get; set; }
        [JsonPropertyName("end")] public GEventTime? End { get; set; }
    }

    internal sealed class GEventTime
    {
        [JsonPropertyName("date")] public string? Date { get; set; }
        [JsonPropertyName("dateTime")] public string? DateTime { get; set; }
        [JsonPropertyName("timeZone")] public string? TimeZone { get; set; }
    }

    internal sealed class GTaskLists
    {
        [JsonPropertyName("items")] public List<GTaskList>? Items { get; set; }
        [JsonPropertyName("nextPageToken")] public string? NextPageToken { get; set; }
    }

    internal sealed class GTaskList
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("title")] public string? Title { get; set; }
    }

    internal sealed class GTasks
    {
        [JsonPropertyName("items")] public List<GTask>? Items { get; set; }
        [JsonPropertyName("nextPageToken")] public string? NextPageToken { get; set; }
    }

    internal sealed class GTask
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("title")] public string? Title { get; set; }
        [JsonPropertyName("notes")] public string? Notes { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("due")] public string? Due { get; set; }
        [JsonPropertyName("completed")] public string? Completed { get; set; }
        [JsonPropertyName("deleted")] public bool? Deleted { get; set; }
        [JsonPropertyName("hidden")] public bool? Hidden { get; set; }
        [JsonPropertyName("webViewLink")] public string? WebViewLink { get; set; }
    }
}
