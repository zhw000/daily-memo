using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using DailyMemo.Core;
using Ical.Net.CalendarComponents;
using IcalCalendar = Ical.Net.Calendar;

namespace DailyMemo.Services;

public sealed class CalDavException : Exception
{
    public CalDavException(string message, bool authFailure = false) : base(message) => IsAuthFailure = authFailure;

    public bool IsAuthFailure { get; }
}

/// <summary>
/// iCloud 日历（CalDAV）。用 Apple ID + App 专用密码登录，读取 iPhone「日历」里的 iCloud 日历。
/// 注意：新版 iOS「提醒事项」已不再通过 CalDAV 提供，提醒事项由 iPhone 端同步到 Google Tasks。
/// </summary>
public sealed class CalDavClient
{
    /// <summary>海外区和中国大陆（云上贵州）两个 iCloud CalDAV 服务器</summary>
    public static readonly string[] ICloudServers = { "https://caldav.icloud.com/", "https://caldav.icloud.com.cn/" };

    private static readonly XNamespace D = "DAV:";
    private static readonly XNamespace C = "urn:ietf:params:xml:ns:caldav";
    private static readonly XNamespace A = "http://apple.com/ns/ical/";

    private readonly HttpClient _http;

    public CalDavClient(HttpClient noRedirectHttp)
    {
        _http = noRedirectHttp;
    }

    /// <summary>找到账号下的所有日历。先试上次成功的服务器，登录失败再换另一个区。</summary>
    public async Task<(List<SourceContainer> calendars, string server)> DiscoverCalendarsAsync(string user, string password, string? preferredServer, CancellationToken ct)
    {
        var servers = ICloudServers.OrderBy(s => s == preferredServer ? 0 : 1).ToList();
        CalDavException? lastAuthError = null;
        foreach (var server in servers)
        {
            try
            {
                return (await DiscoverOnAsync(new Uri(server), user, password, ct), server);
            }
            catch (CalDavException ex) when (ex.IsAuthFailure)
            {
                lastAuthError = ex;
            }
        }
        throw lastAuthError ?? new CalDavException("无法连接 iCloud 日历");
    }

    private async Task<List<SourceContainer>> DiscoverOnAsync(Uri root, string user, string password, CancellationToken ct)
    {
        var principal = await FindHrefAsync(root, user, password, "<d:current-user-principal/>", D + "current-user-principal", ct)
                        ?? throw new CalDavException("iCloud 没有返回用户信息，请检查 Apple ID 和 App 专用密码。");
        var home = await FindHrefAsync(principal, user, password, "<c:calendar-home-set/>", C + "calendar-home-set", ct)
                   ?? throw new CalDavException("iCloud 没有返回日历目录。");

        const string body = """
            <?xml version="1.0" encoding="utf-8"?>
            <d:propfind xmlns:d="DAV:" xmlns:c="urn:ietf:params:xml:ns:caldav" xmlns:a="http://apple.com/ns/ical/" xmlns:cs="http://calendarserver.org/ns/">
              <d:prop>
                <d:resourcetype/>
                <d:displayname/>
                <c:supported-calendar-component-set/>
                <a:calendar-color/>
                <d:current-user-privilege-set/>
              </d:prop>
            </d:propfind>
            """;
        var (doc, baseUri) = await SendXmlAsync(new HttpMethod("PROPFIND"), home, user, password, body, depth: "1", ct);

        var list = new List<SourceContainer>();
        foreach (var resp in doc.Root?.Elements(D + "response") ?? Enumerable.Empty<XElement>())
        {
            var prop = OkProp(resp);
            if (prop == null) continue;
            var rt = prop.Element(D + "resourcetype");
            if (rt?.Element(C + "calendar") == null) continue;

            var comps = prop.Element(C + "supported-calendar-component-set")?.Elements(C + "comp")
                .Select(e => (string?)e.Attribute("name")).ToList();
            if (comps != null && comps.Count > 0 && !comps.Contains("VEVENT")) continue;

            var href = resp.Element(D + "href")?.Value;
            if (string.IsNullOrEmpty(href)) continue;
            var url = new Uri(baseUri, href).ToString();
            var privileges = prop.Element(D + "current-user-privilege-set")?.Descendants(D + "privilege")
                .SelectMany(p => p.Elements()).Select(e => e.Name.LocalName).ToHashSet() ?? new HashSet<string>();

            list.Add(new SourceContainer
            {
                Key = ContainerKeys.For(ItemSource.ICloud, ItemKind.Event, url),
                Kind = ItemKind.Event,
                Source = ItemSource.ICloud,
                Id = url,
                Name = prop.Element(D + "displayname")?.Value is { Length: > 0 } n ? n : "iCloud 日历",
                Color = AppleColor(prop.Element(A + "calendar-color")?.Value),
                Writable = privileges.Count == 0 || privileges.Contains("write") || privileges.Contains("write-content") || privileges.Contains("all"),
                DefaultVisible = true,
            });
        }
        return list.OrderBy(c => c.Name, StringComparer.CurrentCulture).ToList();
    }

    public async Task<List<AgendaItem>> GetEventsAsync(SourceContainer cal, string user, string password, DateTime from, DateTime to, CancellationToken ct)
    {
        string body = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <c:calendar-query xmlns:d="DAV:" xmlns:c="urn:ietf:params:xml:ns:caldav">
              <d:prop><d:getetag/><c:calendar-data/></d:prop>
              <c:filter>
                <c:comp-filter name="VCALENDAR">
                  <c:comp-filter name="VEVENT">
                    <c:time-range start="{Utc(from)}" end="{Utc(to)}"/>
                  </c:comp-filter>
                </c:comp-filter>
              </c:filter>
            </c:calendar-query>
            """;
        var (doc, baseUri) = await SendXmlAsync(new HttpMethod("REPORT"), new Uri(cal.Id), user, password, body, depth: "1", ct);

        var items = new List<AgendaItem>();
        foreach (var resp in doc.Root?.Elements(D + "response") ?? Enumerable.Empty<XElement>())
        {
            var prop = OkProp(resp);
            var data = prop?.Element(C + "calendar-data")?.Value;
            if (string.IsNullOrWhiteSpace(data)) continue;
            var href = new Uri(baseUri, resp.Element(D + "href")?.Value ?? "").ToString();
            var etag = prop?.Element(D + "getetag")?.Value;
            try
            {
                items.AddRange(ExpandIcs(data, cal, href, etag, from, to));
            }
            catch (Exception ex)
            {
                Log.Error("解析 iCloud 日程失败 " + href, ex);
            }
        }
        return items;
    }

    /// <summary>把一个 .ics 资源展开成时间范围内的各次日程（处理重复规则）</summary>
    internal static IEnumerable<AgendaItem> ExpandIcs(string ics, SourceContainer cal, string href, string? etag, DateTime from, DateTime to)
    {
        var calendar = IcalCalendar.Load(ics);
        var results = new List<AgendaItem>();
        var seen = new HashSet<string>();

        foreach (var occ in calendar.GetOccurrences(from.AddDays(-1), to.AddDays(1)))
        {
            if (occ.Source is not CalendarEvent ev) continue;
            if (string.Equals(ev.Status, "CANCELLED", StringComparison.OrdinalIgnoreCase)) continue;

            var startTime = occ.Period.StartTime;
            if (startTime == null) continue;
            bool allDay = ev.IsAllDay || !startTime.HasTime;
            DateTime start, end;
            if (allDay)
            {
                start = startTime.Value.Date;
                var endValue = occ.Period.EndTime?.Value.Date;
                if (endValue == null || endValue <= start)
                {
                    var days = ev.Duration.TotalDays >= 1 ? (int)Math.Round(ev.Duration.TotalDays) : 1;
                    endValue = start.AddDays(days);
                }
                end = endValue.Value;
            }
            else
            {
                start = startTime.AsSystemLocal;
                if (occ.Period.EndTime != null) end = occ.Period.EndTime.AsSystemLocal;
                else end = start + (ev.Duration > TimeSpan.Zero ? ev.Duration : TimeSpan.Zero);
            }

            // 过滤到真正的时间范围
            var effectiveEnd = end > start ? end : start.AddMinutes(1);
            if (effectiveEnd <= from || start >= to) continue;

            bool recurring = ev.RecurrenceRules.Count > 0 || ev.RecurrenceId != null || ev.RecurrenceDates.Count > 0;
            var key = $"ical|{href}|{start:yyyyMMddHHmm}";
            if (!seen.Add(key)) continue;

            results.Add(new AgendaItem
            {
                Key = key,
                Kind = ItemKind.Event,
                Source = ItemSource.ICloud,
                Id = ev.Uid ?? href,
                ContainerId = cal.Id,
                ContainerName = cal.Name,
                Color = cal.Color,
                Title = string.IsNullOrWhiteSpace(ev.Summary) ? "（无标题）" : ev.Summary,
                Notes = string.IsNullOrWhiteSpace(ev.Description) ? null : ev.Description,
                Location = string.IsNullOrWhiteSpace(ev.Location) ? null : ev.Location,
                Start = start,
                End = end,
                AllDay = allDay,
                Recurring = recurring,
                Href = href,
                ETag = etag,
                ReadOnly = !cal.Writable,
            });
        }
        return results;
    }

    public async Task<AgendaItem> CreateEventAsync(SourceContainer cal, string user, string password, string title, DateTime start, DateTime end,
        bool allDay, string? notes, string? location, CancellationToken ct)
    {
        var uid = Guid.NewGuid().ToString().ToUpperInvariant();
        var href = new Uri(new Uri(cal.Id.EndsWith('/') ? cal.Id : cal.Id + "/"), uid + ".ics");
        var ics = BuildIcs(uid, title, start, end, allDay, notes, location);

        using var req = new HttpRequestMessage(HttpMethod.Put, href);
        req.Content = new StringContent(ics, Encoding.UTF8);
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("text/calendar") { CharSet = "utf-8" };
        req.Headers.TryAddWithoutValidation("If-None-Match", "*");
        using var resp = await SendWithRedirectsAsync(req, user, password, ct);
        if (!resp.IsSuccessStatusCode)
            throw new CalDavException($"iCloud 保存日程失败（{(int)resp.StatusCode}）");

        if (allDay && end.Date <= start.Date) end = start.Date.AddDays(1);
        if (!allDay && end <= start) end = start.AddHours(1);
        return new AgendaItem
        {
            Key = $"ical|{href}|{start:yyyyMMddHHmm}",
            Kind = ItemKind.Event,
            Source = ItemSource.ICloud,
            Id = uid,
            ContainerId = cal.Id,
            ContainerName = cal.Name,
            Color = cal.Color,
            Title = title,
            Notes = notes,
            Location = location,
            Start = allDay ? start.Date : start,
            End = allDay ? end.Date : end,
            AllDay = allDay,
            Href = href.ToString(),
            ETag = resp.Headers.ETag?.Tag,
        };
    }

    public async Task DeleteAsync(string href, string user, string password, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Delete, href);
        using var resp = await SendWithRedirectsAsync(req, user, password, ct);
        if (!resp.IsSuccessStatusCode && resp.StatusCode != HttpStatusCode.NotFound)
            throw new CalDavException($"iCloud 删除日程失败（{(int)resp.StatusCode}）");
    }

    internal static string BuildIcs(string uid, string title, DateTime start, DateTime end, bool allDay, string? notes, string? location)
    {
        var sb = new StringBuilder();
        void Line(string s) => sb.Append(Fold(s)).Append("\r\n");
        Line("BEGIN:VCALENDAR");
        Line("VERSION:2.0");
        Line("PRODID:-//DailyMemo//ZH");
        Line("CALSCALE:GREGORIAN");
        Line("BEGIN:VEVENT");
        Line("UID:" + uid);
        Line("DTSTAMP:" + DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture));
        if (allDay)
        {
            var endDate = end.Date <= start.Date ? start.Date.AddDays(1) : end.Date;
            Line("DTSTART;VALUE=DATE:" + start.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
            Line("DTEND;VALUE=DATE:" + endDate.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
        }
        else
        {
            if (end <= start) end = start.AddHours(1);
            Line("DTSTART:" + Utc(start));
            Line("DTEND:" + Utc(end));
        }
        Line("SUMMARY:" + Escape(title));
        if (!string.IsNullOrWhiteSpace(notes)) Line("DESCRIPTION:" + Escape(notes));
        if (!string.IsNullOrWhiteSpace(location)) Line("LOCATION:" + Escape(location));
        Line("END:VEVENT");
        Line("END:VCALENDAR");
        return sb.ToString();
    }

    private static string Escape(string s) =>
        s.Replace("\\", "\\\\").Replace(";", "\\;").Replace(",", "\\,").Replace("\r\n", "\\n").Replace("\n", "\\n");

    /// <summary>iCalendar 要求每行不超过 75 字节，按 UTF-8 字节数折行</summary>
    private static string Fold(string line)
    {
        if (Encoding.UTF8.GetByteCount(line) <= 75) return line;
        var sb = new StringBuilder();
        int bytes = 0;
        foreach (var ch in line)
        {
            int n = Encoding.UTF8.GetByteCount(ch.ToString());
            if (bytes + n > 74)
            {
                sb.Append("\r\n ");
                bytes = 1;
            }
            sb.Append(ch);
            bytes += n;
        }
        return sb.ToString();
    }

    private static string Utc(DateTime local) =>
        DateTime.SpecifyKind(local, DateTimeKind.Local).ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);

    internal static string AppleColor(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "#FF9500";
        var v = value.Trim();
        if (v.StartsWith('#') && v.Length >= 7) return v[..7].ToUpperInvariant();
        return "#FF9500";
    }

    private static XElement? OkProp(XElement response) =>
        response.Elements(D + "propstat")
            .FirstOrDefault(ps => (ps.Element(D + "status")?.Value ?? "").Contains(" 200"))
            ?.Element(D + "prop");

    private async Task<Uri?> FindHrefAsync(Uri url, string user, string password, string propXml, XName propName, CancellationToken ct)
    {
        string body = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <d:propfind xmlns:d="DAV:" xmlns:c="urn:ietf:params:xml:ns:caldav"><d:prop>{propXml}</d:prop></d:propfind>
            """;
        var (doc, baseUri) = await SendXmlAsync(new HttpMethod("PROPFIND"), url, user, password, body, depth: "0", ct);
        var href = doc.Descendants(propName).Elements(D + "href").FirstOrDefault()?.Value;
        return string.IsNullOrWhiteSpace(href) ? null : new Uri(baseUri, href.Trim());
    }

    private async Task<(XDocument doc, Uri baseUri)> SendXmlAsync(HttpMethod method, Uri url, string user, string password, string body, string depth, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, url);
        req.Content = new StringContent(body, Encoding.UTF8, "application/xml");
        req.Headers.TryAddWithoutValidation("Depth", depth);
        using var resp = await SendWithRedirectsAsync(req, user, password, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (resp.StatusCode == HttpStatusCode.Unauthorized)
            throw new CalDavException("iCloud 登录失败：请确认 Apple ID 正确，并使用「App 专用密码」而不是 Apple ID 密码。", authFailure: true);
        if ((int)resp.StatusCode != 207 && !resp.IsSuccessStatusCode)
            throw new CalDavException($"iCloud 返回错误（{(int)resp.StatusCode}）");
        return (XDocument.Parse(text), resp.RequestMessage?.RequestUri ?? url);
    }

    /// <summary>手动跟随重定向：iCloud 会把请求转到分区服务器，自动重定向会丢掉认证头和请求方法</summary>
    private async Task<HttpResponseMessage> SendWithRedirectsAsync(HttpRequestMessage original, string user, string password, CancellationToken ct)
    {
        var auth = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}")));
        byte[]? content = original.Content != null ? await original.Content.ReadAsByteArrayAsync(ct) : null;
        var contentType = original.Content?.Headers.ContentType;
        var url = original.RequestUri!;

        for (int i = 0; i < 6; i++)
        {
            using var req = new HttpRequestMessage(original.Method, url);
            foreach (var h in original.Headers) req.Headers.TryAddWithoutValidation(h.Key, h.Value);
            req.Headers.Authorization = auth;
            if (content != null)
            {
                req.Content = new ByteArrayContent(content);
                req.Content.Headers.ContentType = contentType;
            }
            var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct);
            int code = (int)resp.StatusCode;
            if (code is 301 or 302 or 307 or 308 && resp.Headers.Location != null)
            {
                url = resp.Headers.Location.IsAbsoluteUri ? resp.Headers.Location : new Uri(url, resp.Headers.Location);
                resp.Dispose();
                continue;
            }
            resp.RequestMessage = new HttpRequestMessage(original.Method, url);
            return resp;
        }
        throw new CalDavException("iCloud 重定向次数过多");
    }
}
