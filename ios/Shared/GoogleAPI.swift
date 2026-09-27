import Foundation

enum GoogleAPIError: LocalizedError {
    case http(Int, String)
    case badURL

    var errorDescription: String? {
        switch self {
        case .badURL:
            return "请求地址无效"
        case .http(let code, let msg):
            if code == 403 && (msg.contains("has not been used") || msg.contains("disabled")) {
                return "Google 项目里还没有启用 Calendar API 或 Tasks API，请按教程启用"
            }
            if code == 403 { return "没有权限：\(msg)" }
            if code == 404 { return "找不到这条数据，可能已在别处删除" }
            return "Google 返回错误（\(code)）：\(msg)"
        }
    }
}

/// Google 日历 v3 和 Google Tasks v1（与 Windows 版使用相同的数据约定）
enum GoogleAPI {
    static let tasksColor = "#1A73E8"
    /// 小组件里调小一点，避免超时
    static var requestTimeout: TimeInterval = 25

    private static let calendarBase = "https://www.googleapis.com/calendar/v3"
    private static let tasksBase = "https://tasks.googleapis.com/tasks/v1"

    // MARK: - 数据结构

    struct CalendarList: Decodable {
        var items: [CalendarEntry]?
        var nextPageToken: String?
    }

    struct CalendarEntry: Decodable {
        var id: String
        var summary: String?
        var summaryOverride: String?
        var backgroundColor: String?
        var accessRole: String?
        var primary: Bool?
        var selected: Bool?
        var deleted: Bool?
    }

    struct Events: Decodable {
        var items: [Event]?
        var nextPageToken: String?
    }

    struct Event: Decodable {
        var id: String?
        var status: String?
        var summary: String?
        var description: String?
        var location: String?
        var htmlLink: String?
        var colorId: String?
        var recurringEventId: String?
        var iCalUID: String?
        var start: EventTime?
        var end: EventTime?
    }

    struct EventTime: Decodable {
        var date: String?
        var dateTime: String?
    }

    struct TaskLists: Decodable {
        var items: [TaskList]?
        var nextPageToken: String?
    }

    struct TaskList: Decodable {
        var id: String
        var title: String?
    }

    struct Tasks: Decodable {
        var items: [TaskItem]?
        var nextPageToken: String?
    }

    struct TaskItem: Decodable {
        var id: String
        var title: String?
        var notes: String?
        var status: String?
        var due: String?
        var completed: String?
        var updated: String?
        var deleted: Bool?
        var hidden: Bool?
        var webViewLink: String?
    }

    // MARK: - 日历

    static func calendars() async throws -> [SourceContainer] {
        var result: [SourceContainer] = []
        var page: String? = nil
        repeat {
            var url = "\(calendarBase)/users/me/calendarList?maxResults=250"
            if let p = page { url += "&pageToken=\(esc(p))" }
            let list = try decode(CalendarList.self, try await request("GET", url))
            for c in list.items ?? [] where c.deleted != true {
                let name = nonEmpty(c.summaryOverride) ?? nonEmpty(c.summary) ?? c.id
                result.append(SourceContainer(
                    key: ContainerKey.make(.googleEvent, c.id),
                    kind: .event,
                    source: .googleEvent,
                    nativeID: c.id,
                    name: name,
                    colorHex: normalizeColor(c.backgroundColor, fallback: "#4285F4"),
                    writable: c.accessRole == "owner" || c.accessRole == "writer",
                    defaultVisible: c.primary == true || c.selected == true,
                    isDefault: c.primary == true,
                    accountName: "Google"))
            }
            page = list.nextPageToken
        } while page != nil
        return result.sorted { a, b in
            if a.isDefault != b.isDefault { return a.isDefault }
            return a.name < b.name
        }
    }

    static func events(calendar: SourceContainer, from: Date, to: Date) async throws -> [AgendaItem] {
        var items: [AgendaItem] = []
        var page: String? = nil
        repeat {
            var url = "\(calendarBase)/calendars/\(esc(calendar.nativeID))/events?singleEvents=true&orderBy=startTime&maxResults=250"
            url += "&timeMin=\(esc(rfc3339(from)))&timeMax=\(esc(rfc3339(to)))"
            if let p = page { url += "&pageToken=\(esc(p))" }
            let list = try decode(Events.self, try await request("GET", url))
            for e in list.items ?? [] where e.status != "cancelled" {
                if let item = mapEvent(e, calendar) { items.append(item) }
            }
            page = list.nextPageToken
        } while page != nil
        return items
    }

    static func insertEvent(calendar: SourceContainer, title: String, start: Date, end: Date, allDay: Bool,
                            notes: String?, location: String?) async throws -> AgendaItem {
        let body = eventBody(title: title, start: start, end: end, allDay: allDay, notes: notes, location: location, patch: false)
        let data = try await request("POST", "\(calendarBase)/calendars/\(esc(calendar.nativeID))/events", body: body)
        guard let item = mapEvent(try decode(Event.self, data), calendar) else { throw GoogleAPIError.http(200, "返回的数据不完整") }
        return item
    }

    static func updateEvent(calendar: SourceContainer, eventID: String, title: String, start: Date, end: Date, allDay: Bool,
                            notes: String?, location: String?) async throws -> AgendaItem {
        let body = eventBody(title: title, start: start, end: end, allDay: allDay, notes: notes, location: location, patch: true)
        let data = try await request("PATCH", "\(calendarBase)/calendars/\(esc(calendar.nativeID))/events/\(esc(eventID))", body: body)
        guard let item = mapEvent(try decode(Event.self, data), calendar) else { throw GoogleAPIError.http(200, "返回的数据不完整") }
        return item
    }

    static func deleteEvent(calendarID: String, eventID: String) async throws {
        try await request("DELETE", "\(calendarBase)/calendars/\(esc(calendarID))/events/\(esc(eventID))")
    }

    static func eventBody(title: String, start: Date, end: Date, allDay: Bool, notes: String?, location: String?, patch: Bool) -> [String: Any] {
        var body: [String: Any] = ["summary": title, "description": notes ?? "", "location": location ?? ""]
        let cal = DateText.calendar
        if allDay {
            let sd = cal.startOfDay(for: start)
            var ed = cal.startOfDay(for: end)
            if ed <= sd { ed = cal.date(byAdding: .day, value: 1, to: sd) ?? sd }
            var s: [String: Any] = ["date": dayString(sd)]
            var e: [String: Any] = ["date": dayString(ed)]
            if patch {
                s["dateTime"] = NSNull()
                e["dateTime"] = NSNull()
            }
            body["start"] = s
            body["end"] = e
        } else {
            let realEnd = end > start ? end : start.addingTimeInterval(3600)
            let tz = TimeZone.current.identifier
            var s: [String: Any] = ["dateTime": rfc3339(start), "timeZone": tz]
            var e: [String: Any] = ["dateTime": rfc3339(realEnd), "timeZone": tz]
            if patch {
                s["date"] = NSNull()
                e["date"] = NSNull()
            }
            body["start"] = s
            body["end"] = e
        }
        return body
    }

    static func mapEvent(_ e: Event, _ cal: SourceContainer) -> AgendaItem? {
        guard let id = e.id, let s = e.start else { return nil }
        let start: Date
        let end: Date
        let allDay: Bool
        if let dayText = s.date, let sd = parseDay(dayText) {
            allDay = true
            start = sd
            end = parseDay(e.end?.date) ?? DateText.calendar.date(byAdding: .day, value: 1, to: sd) ?? sd
        } else if let st = parseRFC3339(s.dateTime) {
            allDay = false
            start = st
            end = parseRFC3339(e.end?.dateTime) ?? st
        } else {
            return nil
        }
        return AgendaItem(
            id: "gce|\(cal.nativeID)|\(id)",
            kind: .event,
            source: .googleEvent,
            nativeID: id,
            title: nonEmpty(e.summary) ?? "（无标题）",
            notes: nonEmpty(e.description),
            location: nonEmpty(e.location),
            start: start,
            end: end,
            isAllDay: allDay,
            isCompleted: false,
            completedAt: nil,
            containerID: cal.nativeID,
            containerName: cal.name,
            colorHex: eventColor(e.colorId) ?? cal.colorHex,
            externalID: e.iCalUID,
            url: e.htmlLink,
            isRecurring: e.recurringEventId != nil,
            isReadOnly: !cal.writable)
    }

    // MARK: - 待办

    static func taskLists() async throws -> [SourceContainer] {
        var result: [SourceContainer] = []
        var page: String? = nil
        repeat {
            var url = "\(tasksBase)/users/@me/lists?maxResults=100"
            if let p = page { url += "&pageToken=\(esc(p))" }
            let list = try decode(TaskLists.self, try await request("GET", url))
            for l in list.items ?? [] {
                result.append(taskListContainer(id: l.id, title: l.title, isDefault: result.isEmpty))
            }
            page = list.nextPageToken
        } while page != nil
        return result
    }

    static func insertTaskList(title: String) async throws -> SourceContainer {
        let data = try await request("POST", "\(tasksBase)/users/@me/lists", body: ["title": title])
        let l = try decode(TaskList.self, data)
        return taskListContainer(id: l.id, title: l.title, isDefault: false)
    }

    private static func taskListContainer(id: String, title: String?, isDefault: Bool) -> SourceContainer {
        SourceContainer(
            key: ContainerKey.make(.googleTask, id),
            kind: .task,
            source: .googleTask,
            nativeID: id,
            name: nonEmpty(title) ?? "我的任务",
            colorHex: tasksColor,
            writable: true,
            defaultVisible: true,
            isDefault: isDefault,
            accountName: "Google")
    }

    /// 显示用：未完成的任务 + 最近 7 天完成的任务
    static func tasksForDisplay(list: SourceContainer) async throws -> [AgendaItem] {
        let completedMin = rfc3339(Date().addingTimeInterval(-7 * 86400))
        var items: [AgendaItem] = []
        for completed in [false, true] {
            var page: String? = nil
            repeat {
                var url = "\(tasksBase)/lists/\(esc(list.nativeID))/tasks?maxResults=100&showHidden=true"
                url += completed ? "&showCompleted=true&completedMin=\(esc(completedMin))" : "&showCompleted=false"
                if let p = page { url += "&pageToken=\(esc(p))" }
                let res = try decode(Tasks.self, try await request("GET", url))
                for t in res.items ?? [] where t.deleted != true && (t.status == "completed") == completed {
                    if nonEmpty(t.title) == nil && nonEmpty(t.notes) == nil { continue }
                    items.append(mapTask(t, list))
                }
                page = res.nextPageToken
            } while page != nil
        }
        return items
    }

    /// 同步用：清单里的全部任务（含已完成、已清除）
    static func allTasks(listID: String) async throws -> [TaskItem] {
        var items: [TaskItem] = []
        var page: String? = nil
        repeat {
            var url = "\(tasksBase)/lists/\(esc(listID))/tasks?maxResults=100&showCompleted=true&showHidden=true"
            if let p = page { url += "&pageToken=\(esc(p))" }
            let res = try decode(Tasks.self, try await request("GET", url))
            items.append(contentsOf: (res.items ?? []).filter { $0.deleted != true })
            page = res.nextPageToken
        } while page != nil
        return items
    }

    @discardableResult
    static func insertTask(listID: String, fields: [String: Any]) async throws -> TaskItem {
        var body = fields
        // 新建时不需要显式的空值
        for (k, v) in fields where v is NSNull { body.removeValue(forKey: k) }
        let data = try await request("POST", "\(tasksBase)/lists/\(esc(listID))/tasks", body: body)
        return try decode(TaskItem.self, data)
    }

    @discardableResult
    static func patchTask(listID: String, taskID: String, fields: [String: Any]) async throws -> TaskItem {
        let data = try await request("PATCH", "\(tasksBase)/lists/\(esc(listID))/tasks/\(esc(taskID))", body: fields)
        return try decode(TaskItem.self, data)
    }

    static func deleteTask(listID: String, taskID: String) async throws {
        try await request("DELETE", "\(tasksBase)/lists/\(esc(listID))/tasks/\(esc(taskID))")
    }

    static func setTaskCompleted(listID: String, taskID: String, completed: Bool) async throws {
        let fields: [String: Any] = completed ? ["status": "completed"] : ["status": "needsAction", "completed": NSNull()]
        try await patchTask(listID: listID, taskID: taskID, fields: fields)
    }

    /// 任务字段。Google Tasks 只保存日期，具体时间写在标题开头：「15:00 交报告」（Windows 版同样识别）
    static func taskFields(title: String, notes: String?, due: Date?, hasTime: Bool, completed: Bool? = nil) -> [String: Any] {
        var minute: Int? = nil
        if let due, hasTime {
            let c = DateText.calendar.dateComponents([.hour, .minute], from: due)
            minute = (c.hour ?? 0) * 60 + (c.minute ?? 0)
        }
        let dueValue: Any = due.map { taskDue($0) as Any } ?? NSNull()
        var fields: [String: Any] = [
            "title": composeTaskTitle(title, minute: minute),
            "notes": notes ?? "",
            "due": dueValue,
        ]
        if let completed {
            fields["status"] = completed ? "completed" : "needsAction"
            if !completed { fields["completed"] = NSNull() }
        }
        return fields
    }

    static func mapTask(_ t: TaskItem, _ list: SourceContainer) -> AgendaItem {
        var due: Date? = nil
        var allDay = true
        var title = (t.title ?? "").trimmingCharacters(in: .whitespacesAndNewlines)
        if let day = parseDay(t.due) {
            due = day
            let split = splitTaskTitle(title)
            if let m = split.minute {
                title = split.title
                due = day.addingTimeInterval(TimeInterval(m * 60))
                allDay = false
            }
        }
        return AgendaItem(
            id: "gt|\(list.nativeID)|\(t.id)",
            kind: .task,
            source: .googleTask,
            nativeID: t.id,
            title: title.isEmpty ? "（无标题）" : title,
            notes: nonEmpty(t.notes),
            location: nil,
            start: due,
            end: nil,
            isAllDay: allDay,
            isCompleted: t.status == "completed",
            completedAt: parseRFC3339(t.completed),
            containerID: list.nativeID,
            containerName: list.name,
            colorHex: tasksColor,
            externalID: nil,
            url: t.webViewLink,
            isRecurring: false,
            isReadOnly: false)
    }

    /// 「15:00 交报告」→（交报告, 900）
    static func splitTaskTitle(_ raw: String) -> (title: String, minute: Int?) {
        let text = raw.trimmingCharacters(in: .whitespacesAndNewlines)
        guard let re = try? NSRegularExpression(pattern: "^([01]?\\d|2[0-3])[:：]([0-5]\\d)\\s+(.+)$", options: [.dotMatchesLineSeparators]) else {
            return (text, nil)
        }
        let ns = text as NSString
        guard let m = re.firstMatch(in: text, options: [], range: NSRange(location: 0, length: ns.length)),
              let h = Int(ns.substring(with: m.range(at: 1))), let mi = Int(ns.substring(with: m.range(at: 2))) else {
            return (text, nil)
        }
        return (ns.substring(with: m.range(at: 3)).trimmingCharacters(in: .whitespacesAndNewlines), h * 60 + mi)
    }

    static func composeTaskTitle(_ title: String, minute: Int?) -> String {
        let t = title.trimmingCharacters(in: .whitespacesAndNewlines)
        guard let m = minute else { return t }
        return String(format: "%02d:%02d ", m / 60, m % 60) + splitTaskTitle(t).title
    }

    // MARK: - 基础

    @discardableResult
    static func request(_ method: String, _ urlString: String, body: [String: Any]? = nil) async throws -> Data {
        guard let url = URL(string: urlString) else { throw GoogleAPIError.badURL }
        var lastStatus = 0
        for attempt in 0..<3 {
            let token = try await GoogleSession.shared.accessToken(forceRefresh: attempt > 0 && lastStatus == 401)
            var req = URLRequest(url: url)
            req.httpMethod = method
            req.timeoutInterval = requestTimeout
            req.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization")
            if let body = body {
                req.setValue("application/json; charset=utf-8", forHTTPHeaderField: "Content-Type")
                req.httpBody = try JSONSerialization.data(withJSONObject: body, options: [])
            }
            let (data, response) = try await URLSession.shared.data(for: req)
            let status = (response as? HTTPURLResponse)?.statusCode ?? 0
            lastStatus = status
            if (200..<300).contains(status) { return data }
            if status == 401 && attempt == 0 { continue }
            if status == 429 && attempt < 2 {
                try await Task.sleep(nanoseconds: 1_500_000_000)
                continue
            }
            if method == "DELETE" && (status == 404 || status == 410) { return Data() }
            throw GoogleAPIError.http(status, message(from: data))
        }
        throw GoogleAPIError.http(lastStatus, "请求失败")
    }

    private static func decode<T: Decodable>(_ type: T.Type, _ data: Data) throws -> T {
        try JSONDecoder().decode(type, from: data)
    }

    private static func message(from data: Data) -> String {
        if let json = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any],
           let err = json["error"] as? [String: Any],
           let msg = err["message"] as? String {
            return msg
        }
        return String(String(data: data, encoding: .utf8)?.prefix(200) ?? "")
    }

    static func esc(_ s: String) -> String {
        var allowed = CharacterSet.alphanumerics
        allowed.insert(charactersIn: "-._~")
        return s.addingPercentEncoding(withAllowedCharacters: allowed) ?? s
    }

    private static let isoOut: ISO8601DateFormatter = {
        let f = ISO8601DateFormatter()
        f.formatOptions = [.withInternetDateTime]
        f.timeZone = TimeZone.current
        return f
    }()

    private static let isoFrac: ISO8601DateFormatter = {
        let f = ISO8601DateFormatter()
        f.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        return f
    }()

    private static let isoPlain: ISO8601DateFormatter = {
        let f = ISO8601DateFormatter()
        f.formatOptions = [.withInternetDateTime]
        return f
    }()

    static func rfc3339(_ d: Date) -> String { isoOut.string(from: d) }

    static func parseRFC3339(_ s: String?) -> Date? {
        guard let s = s, !s.isEmpty else { return nil }
        return isoFrac.date(from: s) ?? isoPlain.date(from: s)
    }

    /// 「2026-09-27」或「2026-09-27T00:00:00.000Z」的日期部分 → 本地当天 0 点
    static func parseDay(_ s: String?) -> Date? {
        guard let s = s, s.count >= 10 else { return nil }
        let parts = s.prefix(10).split(separator: "-")
        guard parts.count == 3, let y = Int(parts[0]), let m = Int(parts[1]), let d = Int(parts[2]) else { return nil }
        return DateText.calendar.date(from: DateComponents(year: y, month: m, day: d))
    }

    static func dayString(_ d: Date) -> String {
        let c = DateText.calendar.dateComponents([.year, .month, .day], from: d)
        return String(format: "%04d-%02d-%02d", c.year ?? 1970, c.month ?? 1, c.day ?? 1)
    }

    static func taskDue(_ d: Date) -> String { dayString(d) + "T00:00:00.000Z" }

    static func normalizeColor(_ color: String?, fallback: String) -> String {
        guard let c = color, c.hasPrefix("#"), c.count >= 7 else { return fallback }
        return String(c.prefix(7)).uppercased()
    }

    private static func eventColor(_ id: String?) -> String? {
        switch id {
        case "1": return "#7986CB"
        case "2": return "#33B679"
        case "3": return "#8E24AA"
        case "4": return "#E67C73"
        case "5": return "#F6BF26"
        case "6": return "#F4511E"
        case "7": return "#039BE5"
        case "8": return "#616161"
        case "9": return "#3F51B5"
        case "10": return "#0B8043"
        case "11": return "#D50000"
        default: return nil
        }
    }
}

func nonEmpty(_ s: String?) -> String? {
    guard let t = s?.trimmingCharacters(in: .whitespacesAndNewlines), !t.isEmpty else { return nil }
    return t
}
