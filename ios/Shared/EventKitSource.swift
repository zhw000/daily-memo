import Foundation
import EventKit
import CoreGraphics

enum EventKitError: LocalizedError {
    case noAccess(String)
    case notFound
    case noDefaultCalendar

    var errorDescription: String? {
        switch self {
        case .noAccess(let what): return "没有访问\(what)的权限，请在「设置 → 隐私与安全性」里允许今日事访问"
        case .notFound: return "找不到这条内容，可能已在别处删除"
        case .noDefaultCalendar: return "没有可以保存的日历或列表"
        }
    }
}

/// iPhone 自带的「日历」和「提醒事项」
final class EventKitSource {
    static let shared = EventKitSource()
    let store = EKEventStore()

    static func isAuthorized(_ type: EKEntityType) -> Bool {
        let status = EKEventStore.authorizationStatus(for: type)
        return status == .fullAccess || status == .authorized
    }

    static func statusText(_ type: EKEntityType) -> String {
        switch EKEventStore.authorizationStatus(for: type) {
        case .fullAccess, .authorized: return "已允许"
        case .writeOnly: return "仅可添加"
        case .denied: return "已拒绝"
        case .restricted: return "受限制"
        case .notDetermined: return "未设置"
        @unknown default: return "未知"
        }
    }

    func requestAccess() async -> (events: Bool, reminders: Bool) {
        var events = Self.isAuthorized(.event)
        var reminders = Self.isAuthorized(.reminder)
        if !events { events = (try? await store.requestFullAccessToEvents()) ?? false }
        if !reminders { reminders = (try? await store.requestFullAccessToReminders()) ?? false }
        if events || reminders { store.reset() }
        return (events, reminders)
    }

    // MARK: - 日历和列表

    func eventCalendars() -> [SourceContainer] {
        guard Self.isAuthorized(.event) else { return [] }
        let def = store.defaultCalendarForNewEvents?.calendarIdentifier
        return store.calendars(for: .event).map { cal in
            SourceContainer(
                key: ContainerKey.make(.ekEvent, cal.calendarIdentifier),
                kind: .event,
                source: .ekEvent,
                nativeID: cal.calendarIdentifier,
                name: cal.title,
                colorHex: Self.hex(cal.cgColor),
                writable: cal.allowsContentModifications,
                defaultVisible: true,
                isDefault: cal.calendarIdentifier == def,
                accountName: cal.source?.title)
        }.sorted { $0.name < $1.name }
    }

    func reminderLists() -> [SourceContainer] {
        guard Self.isAuthorized(.reminder) else { return [] }
        let def = store.defaultCalendarForNewReminders()?.calendarIdentifier
        return store.calendars(for: .reminder).map { cal in
            SourceContainer(
                key: ContainerKey.make(.ekReminder, cal.calendarIdentifier),
                kind: .task,
                source: .ekReminder,
                nativeID: cal.calendarIdentifier,
                name: cal.title,
                colorHex: Self.hex(cal.cgColor),
                writable: cal.allowsContentModifications,
                defaultVisible: true,
                isDefault: cal.calendarIdentifier == def,
                accountName: cal.source?.title)
        }.sorted { a, b in
            if a.isDefault != b.isDefault { return a.isDefault }
            return a.name < b.name
        }
    }

    // MARK: - 读取

    func events(from: Date, to: Date, hiddenKeys: Set<String>) -> [AgendaItem] {
        guard Self.isAuthorized(.event) else { return [] }
        let cals = store.calendars(for: .event).filter { !hiddenKeys.contains(ContainerKey.make(.ekEvent, $0.calendarIdentifier)) }
        guard !cals.isEmpty else { return [] }
        let predicate = store.predicateForEvents(withStart: from, end: to, calendars: cals)
        return store.events(matching: predicate).compactMap { mapEvent($0) }
    }

    /// 未完成的提醒 + 指定日期之后完成的提醒
    func reminders(hiddenKeys: Set<String>, completedSince: Date) async -> [AgendaItem] {
        guard Self.isAuthorized(.reminder) else { return [] }
        let cals = store.calendars(for: .reminder).filter { !hiddenKeys.contains(ContainerKey.make(.ekReminder, $0.calendarIdentifier)) }
        guard !cals.isEmpty else { return [] }
        let open = await fetch(store.predicateForIncompleteReminders(withDueDateStarting: nil, ending: nil, calendars: cals))
        let done = await fetch(store.predicateForCompletedReminders(withCompletionDateStarting: completedSince, ending: nil, calendars: cals))
        var seen = Set<String>()
        return (open + done).filter { seen.insert($0.calendarItemIdentifier).inserted }.map { mapReminder($0) }
    }

    /// 同步用：某个列表里的全部提醒
    func allReminders(inList listID: String) async -> [EKReminder] {
        guard Self.isAuthorized(.reminder), let cal = store.calendar(withIdentifier: listID) else { return [] }
        return await fetch(store.predicateForReminders(in: [cal]))
    }

    private func fetch(_ predicate: NSPredicate) async -> [EKReminder] {
        await withCheckedContinuation { (cont: CheckedContinuation<[EKReminder], Never>) in
            _ = store.fetchReminders(matching: predicate) { reminders in
                cont.resume(returning: reminders ?? [])
            }
        }
    }

    func mapEvent(_ e: EKEvent) -> AgendaItem? {
        guard let start = e.startDate else { return nil }
        let cal = DateText.calendar
        var end: Date = e.endDate ?? start
        var s = start
        if e.isAllDay {
            // EventKit 全天日程的结束时间是最后一天的 23:59:59，这里统一成「次日 0 点」
            s = cal.startOfDay(for: start)
            end = cal.date(byAdding: .day, value: 1, to: cal.startOfDay(for: end)) ?? end
            if end <= s { end = cal.date(byAdding: .day, value: 1, to: s) ?? s }
        }
        let calendar = e.calendar
        return AgendaItem(
            id: "eke|\(e.calendarItemIdentifier)|\(Int(start.timeIntervalSince1970))",
            kind: .event,
            source: .ekEvent,
            nativeID: e.calendarItemIdentifier,
            title: nonEmpty(e.title) ?? "（无标题）",
            notes: nonEmpty(e.notes),
            location: nonEmpty(e.location),
            start: s,
            end: end,
            isAllDay: e.isAllDay,
            isCompleted: false,
            completedAt: nil,
            containerID: calendar?.calendarIdentifier ?? "",
            containerName: calendar?.title ?? "",
            colorHex: Self.hex(calendar?.cgColor),
            externalID: e.calendarItemExternalIdentifier,
            url: e.url?.absoluteString,
            isRecurring: e.hasRecurrenceRules,
            isReadOnly: !(calendar?.allowsContentModifications ?? false))
    }

    func mapReminder(_ r: EKReminder) -> AgendaItem {
        var due: Date? = nil
        var hasTime = false
        if let comps = r.dueDateComponents, comps.year != nil, comps.month != nil, comps.day != nil {
            due = DateText.calendar.date(from: comps)
            hasTime = comps.hour != nil
        }
        let list = r.calendar
        return AgendaItem(
            id: "ekr|\(r.calendarItemIdentifier)",
            kind: .task,
            source: .ekReminder,
            nativeID: r.calendarItemIdentifier,
            title: nonEmpty(r.title) ?? "（无标题）",
            notes: nonEmpty(r.notes),
            location: nil,
            start: due,
            end: nil,
            isAllDay: !hasTime,
            isCompleted: r.isCompleted,
            completedAt: r.completionDate,
            containerID: list?.calendarIdentifier ?? "",
            containerName: list?.title ?? "",
            colorHex: Self.hex(list?.cgColor),
            externalID: r.calendarItemExternalIdentifier,
            url: nil,
            isRecurring: r.hasRecurrenceRules,
            isReadOnly: !(list?.allowsContentModifications ?? true))
    }

    // MARK: - 修改提醒事项

    func setReminderCompleted(id: String, completed: Bool) throws {
        guard Self.isAuthorized(.reminder) else { throw EventKitError.noAccess("提醒事项") }
        guard let r = store.calendarItem(withIdentifier: id) as? EKReminder else { throw EventKitError.notFound }
        r.isCompleted = completed
        try store.save(r, commit: true)
    }

    func createReminder(title: String, notes: String?, due: Date?, hasTime: Bool, listID: String?) throws -> AgendaItem {
        guard Self.isAuthorized(.reminder) else { throw EventKitError.noAccess("提醒事项") }
        let r = EKReminder(eventStore: store)
        if let id = listID, let cal = store.calendar(withIdentifier: id) {
            r.calendar = cal
        } else if let def = store.defaultCalendarForNewReminders() {
            r.calendar = def
        } else {
            throw EventKitError.noDefaultCalendar
        }
        r.title = title
        r.notes = notes
        Self.apply(due: due, hasTime: hasTime, to: r)
        try store.save(r, commit: true)
        return mapReminder(r)
    }

    func updateReminder(id: String, title: String, notes: String?, due: Date?, hasTime: Bool) throws -> AgendaItem {
        guard Self.isAuthorized(.reminder) else { throw EventKitError.noAccess("提醒事项") }
        guard let r = store.calendarItem(withIdentifier: id) as? EKReminder else { throw EventKitError.notFound }
        r.title = title
        r.notes = notes
        Self.apply(due: due, hasTime: hasTime, to: r)
        try store.save(r, commit: true)
        return mapReminder(r)
    }

    func deleteReminder(id: String) throws {
        guard let r = store.calendarItem(withIdentifier: id) as? EKReminder else { return }
        try store.remove(r, commit: true)
    }

    /// 设置截止日期；有具体时间时顺便加一个提醒闹钟，到点 iPhone 会通知
    static func apply(due: Date?, hasTime: Bool, to r: EKReminder) {
        guard let due = due else {
            r.dueDateComponents = nil
            r.alarms = nil
            return
        }
        let cal = DateText.calendar
        var comps = hasTime
            ? cal.dateComponents([.year, .month, .day, .hour, .minute], from: due)
            : cal.dateComponents([.year, .month, .day], from: due)
        comps.calendar = Calendar(identifier: .gregorian)
        r.dueDateComponents = comps
        r.alarms = hasTime ? [EKAlarm(absoluteDate: due)] : nil
    }

    // MARK: - 修改日程

    func createEvent(title: String, start: Date, end: Date, allDay: Bool, notes: String?, location: String?, calendarID: String?) throws -> AgendaItem {
        guard Self.isAuthorized(.event) else { throw EventKitError.noAccess("日历") }
        let e = EKEvent(eventStore: store)
        if let id = calendarID, let cal = store.calendar(withIdentifier: id) {
            e.calendar = cal
        } else if let def = store.defaultCalendarForNewEvents {
            e.calendar = def
        } else {
            throw EventKitError.noDefaultCalendar
        }
        fill(e, title: title, start: start, end: end, allDay: allDay, notes: notes, location: location)
        try store.save(e, span: .thisEvent, commit: true)
        return mapEvent(e) ?? AgendaItem(id: "eke|new", kind: .event, source: .ekEvent, nativeID: "", title: title,
                                         containerID: "", containerName: "", colorHex: "#8E8E93")
    }

    func updateEvent(_ item: AgendaItem, title: String, start: Date, end: Date, allDay: Bool, notes: String?, location: String?) throws {
        guard let e = findEvent(item) else { throw EventKitError.notFound }
        fill(e, title: title, start: start, end: end, allDay: allDay, notes: notes, location: location)
        try store.save(e, span: .thisEvent, commit: true)
    }

    func deleteEvent(_ item: AgendaItem) throws {
        guard let e = findEvent(item) else { return }
        try store.remove(e, span: .thisEvent, commit: true)
    }

    private func fill(_ e: EKEvent, title: String, start: Date, end: Date, allDay: Bool, notes: String?, location: String?) {
        e.title = title
        e.notes = notes
        e.location = location
        e.isAllDay = allDay
        if allDay {
            let cal = DateText.calendar
            let s = cal.startOfDay(for: start)
            var endDay = cal.startOfDay(for: end)
            if endDay <= s { endDay = cal.date(byAdding: .day, value: 1, to: s) ?? s }
            e.startDate = s
            // EventKit 的全天日程结束于最后一天
            e.endDate = endDay.addingTimeInterval(-1)
        } else {
            e.startDate = start
            e.endDate = end > start ? end : start.addingTimeInterval(3600)
        }
    }

    /// 找到重复日程里具体的那一次
    private func findEvent(_ item: AgendaItem) -> EKEvent? {
        if let start = item.start {
            let predicate = store.predicateForEvents(withStart: start.addingTimeInterval(-86400), end: start.addingTimeInterval(86400), calendars: nil)
            let candidates = store.events(matching: predicate).filter { $0.calendarItemIdentifier == item.nativeID }
            if let exact = candidates.min(by: { abs($0.startDate.timeIntervalSince(start)) < abs($1.startDate.timeIntervalSince(start)) }) {
                return exact
            }
        }
        return store.calendarItem(withIdentifier: item.nativeID) as? EKEvent
    }

    static func hex(_ color: CGColor?) -> String {
        guard let color = color,
              let srgb = CGColorSpace(name: CGColorSpace.sRGB),
              let c = color.converted(to: srgb, intent: .defaultIntent, options: nil),
              let comps = c.components, comps.count >= 3 else { return "#8E8E93" }
        func byte(_ v: CGFloat) -> Int { Int((max(0, min(1, v)) * 255).rounded()) }
        return String(format: "#%02X%02X%02X", byte(comps[0]), byte(comps[1]), byte(comps[2]))
    }
}
