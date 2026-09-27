import Foundation

enum ItemKind: String, Codable, Hashable {
    case event
    case task
}

/// 数据来源：iPhone 日历、iPhone 提醒事项、Google 日历、Google Tasks
enum ItemSource: String, Codable, Hashable {
    case ekEvent
    case ekReminder
    case googleEvent
    case googleTask
}

/// 一条日程或待办。
/// 日程：start/end；全天日程的 end 是「不含」的结束日（次日 0 点）。
/// 待办：start 是截止日期，isAllDay = true 表示没有具体时间。
struct AgendaItem: Codable, Identifiable, Hashable {
    var id: String
    var kind: ItemKind
    var source: ItemSource
    var nativeID: String
    var title: String
    var notes: String? = nil
    var location: String? = nil
    var start: Date? = nil
    var end: Date? = nil
    var isAllDay: Bool = true
    var isCompleted: Bool = false
    var completedAt: Date? = nil
    var containerID: String
    var containerName: String
    var colorHex: String
    var externalID: String? = nil
    var url: String? = nil
    var isRecurring: Bool = false
    var isReadOnly: Bool = false

    var isTask: Bool { kind == .task }
    var isEvent: Bool { kind == .event }
    var hasTime: Bool { start != nil && !isAllDay }
    var containerKey: String { ContainerKey.make(source, containerID) }

    var sourceLabel: String {
        switch source {
        case .ekEvent: return "iPhone 日历"
        case .ekReminder: return "提醒事项"
        case .googleEvent: return "Google 日历"
        case .googleTask: return "Google Tasks"
        }
    }

    /// 日程的结束时间（没有结束时间时，全天日程算到次日 0 点）
    var effectiveEnd: Date {
        guard let s = start else { return .distantPast }
        if let e = end { return e }
        if isAllDay {
            let cal = DateText.calendar
            return cal.date(byAdding: .day, value: 1, to: cal.startOfDay(for: s)) ?? s
        }
        return s
    }
}

enum ContainerKey {
    static func make(_ source: ItemSource, _ id: String) -> String {
        switch source {
        case .ekEvent: return "ekc:" + id
        case .ekReminder: return "ekr:" + id
        case .googleEvent: return "gc:" + id
        case .googleTask: return "gt:" + id
        }
    }

    static func parse(_ key: String) -> (source: ItemSource, id: String)? {
        let prefixes: [(String, ItemSource)] = [("ekc:", .ekEvent), ("ekr:", .ekReminder), ("gc:", .googleEvent), ("gt:", .googleTask)]
        for (prefix, source) in prefixes where key.hasPrefix(prefix) {
            return (source, String(key.dropFirst(prefix.count)))
        }
        return nil
    }
}

/// 一个日历或待办清单
struct SourceContainer: Codable, Identifiable, Hashable {
    var key: String
    var kind: ItemKind
    var source: ItemSource
    var nativeID: String
    var name: String
    var colorHex: String
    var writable: Bool = true
    var defaultVisible: Bool = true
    var isDefault: Bool = false
    var accountName: String? = nil

    var id: String { key }

    var sourceLabel: String {
        switch source {
        case .ekEvent: return "iPhone 日历"
        case .ekReminder: return "提醒事项"
        case .googleEvent: return "Google 日历"
        case .googleTask: return "Google Tasks"
        }
    }

    /// 选择保存位置时显示的名字，例如「工作（Google 日历）」；默认的「提醒事项」列表直接叫「iPhone 提醒事项」
    var displayName: String {
        let long: String
        switch source {
        case .ekEvent: long = "iPhone 日历"
        case .ekReminder: long = "iPhone 提醒事项"
        case .googleEvent: long = "Google 日历"
        case .googleTask: long = "Google Tasks"
        }
        if name == sourceLabel || name == long || (source == .ekReminder && ["提醒事项", "提醒", "Reminders"].contains(name)) {
            return long
        }
        return "\(name)（\(long)）"
    }
}

/// 写入共享目录给小组件用的数据
struct AgendaSnapshot: Codable {
    var generatedAt: Date
    var items: [AgendaItem]
    var containers: [SourceContainer]
    var errors: [String] = []
}

/// 某一天要显示的内容
struct DayAgenda {
    var date: Date
    var overdue: [AgendaItem] = []
    var allDay: [AgendaItem] = []
    var scheduled: [AgendaItem] = []
    var dayTasks: [AgendaItem] = []
    var undated: [AgendaItem] = []
    var completed: [AgendaItem] = []

    var eventCount: Int { allDay.count + scheduled.filter { $0.isEvent }.count }
    var openTaskCount: Int { dayTasks.count + scheduled.filter { $0.isTask }.count }
    var isEmpty: Bool { overdue.isEmpty && allDay.isEmpty && scheduled.isEmpty && dayTasks.isEmpty && undated.isEmpty && completed.isEmpty }

    /// 今天的完成进度（已完成 / 今天全部待办）
    var progress: Double {
        let total = openTaskCount + overdue.count + completed.count
        return total == 0 ? 0 : Double(completed.count) / Double(total)
    }

    var summary: String {
        var parts: [String] = []
        if eventCount > 0 { parts.append("\(eventCount) 个日程") }
        let open = openTaskCount + overdue.count
        if open > 0 { parts.append("\(open) 项待办") }
        return parts.isEmpty ? "今天没有安排" : parts.joined(separator: " · ")
    }

    /// 下一件要做的事（还没开始的日程或带时间的待办；没有的话取正在进行的日程）
    func nextUp(now: Date) -> AgendaItem? {
        if let next = scheduled.first(where: { !$0.isCompleted && ($0.start ?? .distantPast) >= now.addingTimeInterval(-60) }) {
            return next
        }
        return scheduled.first(where: { $0.isEvent && ($0.start ?? .distantFuture) <= now && $0.effectiveEnd > now })
    }
}

enum AgendaBuilder {
    static func build(_ items: [AgendaItem], day: Date, carryOver: Bool) -> DayAgenda {
        let cal = DateText.calendar
        let d0 = cal.startOfDay(for: day)
        let d1 = cal.date(byAdding: .day, value: 1, to: d0) ?? d0.addingTimeInterval(86400)
        var a = DayAgenda(date: d0)

        for it in items {
            if it.isEvent {
                guard let s = it.start else { continue }
                if it.isAllDay {
                    let sd = cal.startOfDay(for: s)
                    var e = it.end ?? cal.date(byAdding: .day, value: 1, to: sd) ?? sd
                    if e <= sd { e = cal.date(byAdding: .day, value: 1, to: sd) ?? sd }
                    if sd < d1 && e > d0 { a.allDay.append(it) }
                } else {
                    let e = it.end ?? s
                    let overlaps = s < d1 && (e > d0 || (e == s && s >= d0))
                    if overlaps { a.scheduled.append(it) }
                }
                continue
            }

            if it.isCompleted {
                if let c = it.completedAt ?? it.start, cal.isDate(c, inSameDayAs: d0) { a.completed.append(it) }
                continue
            }

            guard let due = it.start else {
                if carryOver { a.undated.append(it) }
                continue
            }
            let dueDay = cal.startOfDay(for: due)
            if dueDay == d0 {
                if it.hasTime { a.scheduled.append(it) } else { a.dayTasks.append(it) }
            } else if dueDay < d0 && carryOver {
                a.overdue.append(it)
            }
        }

        a.allDay.sort { $0.title < $1.title }
        a.scheduled.sort { x, y in
            let sx = max(x.start ?? d0, d0), sy = max(y.start ?? d0, d0)
            if sx != sy { return sx < sy }
            if x.kind != y.kind { return x.kind == .event }
            return x.title < y.title
        }
        a.dayTasks.sort { x, y in
            if x.containerName != y.containerName { return x.containerName < y.containerName }
            return x.title < y.title
        }
        a.overdue.sort { ($0.start ?? .distantPast) < ($1.start ?? .distantPast) }
        a.undated.sort { $0.title < $1.title }
        a.completed.sort { ($0.completedAt ?? .distantPast) > ($1.completedAt ?? .distantPast) }
        return a
    }
}
