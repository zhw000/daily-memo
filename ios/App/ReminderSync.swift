import Foundation
import EventKit

struct SyncReport {
    var changes = 0
    var error: String?
    var paused = false
}

/// iPhone「提醒事项」↔ Google Tasks 双向同步（这样 Windows 版也能看到并勾选提醒事项）。
/// 每个选中的提醒事项列表对应一个同名的 Google Tasks 清单。
final class ReminderSync {
    static let shared = ReminderSync()
    private var running = false

    func run(settings: AppSettings) async -> SyncReport {
        var report = SyncReport()
        guard !running else { return report }
        running = true
        defer { running = false }

        guard EventKitSource.isAuthorized(.reminder) else {
            report.error = "没有访问提醒事项的权限"
            return report
        }
        let ek = EventKitSource.shared
        var state = SharedStore.syncState
        // 不再同步的列表：只丢掉对应关系，两边的数据都不动
        state.pairs = state.pairs.filter { settings.syncedReminderLists.contains($0.key) }

        var googleLists: [SourceContainer]
        do {
            googleLists = try await GoogleAPI.taskLists()
        } catch {
            report.error = AgendaLoader.describe(error)
            state.lastError = report.error
            SharedStore.syncState = state
            return report
        }

        for listID in settings.syncedReminderLists {
            guard let calendar = ek.store.calendar(withIdentifier: listID) else { continue }
            var pair = state.pairs[listID] ?? ListPair(reminderListID: listID, googleListID: "")
            do {
                if pair.googleListID.isEmpty || !googleLists.contains(where: { $0.nativeID == pair.googleListID }) {
                    let takenByOthers = Set(state.pairs.filter { $0.key != listID }.values.map { $0.googleListID })
                    if let same = googleLists.first(where: { $0.name == calendar.title && !takenByOthers.contains($0.nativeID) }) {
                        pair.googleListID = same.nativeID
                    } else {
                        let created = try await GoogleAPI.insertTaskList(title: calendar.title)
                        googleLists.append(created)
                        pair.googleListID = created.nativeID
                    }
                    pair.records = []
                }

                let reminders = await ek.allReminders(inList: listID)
                let tasks = try await GoogleAPI.allTasks(listID: pair.googleListID)
                let plan = SyncPlanner.plan(
                    records: pair.records,
                    reminders: reminders.map { Self.side($0) },
                    tasks: tasks.map { Self.side($0) },
                    now: Date())
                let result = await execute(plan, pair: pair, calendar: calendar, reminders: reminders)
                pair.records = result.records
                report.changes += result.changes
                if plan.deletionsPaused { report.paused = true }
            } catch {
                report.error = AgendaLoader.describe(error)
            }
            state.pairs[listID] = pair
            SharedStore.syncState = state
        }

        state.lastRun = Date()
        state.lastError = report.error
        state.lastChanges = report.changes
        SharedStore.syncState = state
        return report
    }

    private func execute(_ plan: SyncPlan, pair: ListPair, calendar: EKCalendar, reminders: [EKReminder]) async -> (records: [SyncRecord], changes: Int) {
        let store = EventKitSource.shared.store
        var byID: [String: EKReminder] = [:]
        for r in reminders { byID[r.calendarItemIdentifier] = r }
        var records = plan.heldRecords
        var changes = 0

        func previous(for action: SyncAction) -> SyncRecord? {
            switch action {
            case .link(let rid, let tid, _), .updateTask(let rid, let tid, _), .updateReminder(let rid, let tid, _):
                return pair.records.first { $0.reminderID == rid || $0.taskID == tid }
            case .deleteTask(let tid):
                return pair.records.first { $0.taskID == tid }
            case .deleteReminder(let rid):
                return pair.records.first { $0.reminderID == rid }
            case .createTask, .createReminder:
                return nil
            }
        }

        for action in plan.actions {
            do {
                switch action {
                case .link(let rid, let tid, let data):
                    records.append(SyncRecord(reminderID: rid, reminderAltID: byID[rid]?.calendarItemExternalIdentifier, taskID: tid, fingerprint: data.fingerprint))

                case .updateTask(let rid, let tid, let data):
                    try await GoogleAPI.patchTask(listID: pair.googleListID, taskID: tid, fields: data.googleFields)
                    records.append(SyncRecord(reminderID: rid, reminderAltID: byID[rid]?.calendarItemExternalIdentifier, taskID: tid, fingerprint: data.fingerprint))
                    changes += 1

                case .updateReminder(let rid, let tid, let data):
                    guard let r = byID[rid] else { continue }
                    Self.apply(data, to: r)
                    try store.save(r, commit: true)
                    records.append(SyncRecord(reminderID: rid, reminderAltID: r.calendarItemExternalIdentifier, taskID: tid, fingerprint: data.fingerprint))
                    changes += 1

                case .createTask(let rid, let data):
                    let t = try await GoogleAPI.insertTask(listID: pair.googleListID, fields: data.googleFields)
                    records.append(SyncRecord(reminderID: rid, reminderAltID: byID[rid]?.calendarItemExternalIdentifier, taskID: t.id, fingerprint: data.fingerprint))
                    changes += 1

                case .createReminder(let tid, let data):
                    let r = EKReminder(eventStore: store)
                    r.calendar = calendar
                    Self.apply(data, to: r)
                    try store.save(r, commit: true)
                    records.append(SyncRecord(reminderID: r.calendarItemIdentifier, reminderAltID: r.calendarItemExternalIdentifier, taskID: tid, fingerprint: data.fingerprint))
                    changes += 1

                case .deleteTask(let tid):
                    try await GoogleAPI.deleteTask(listID: pair.googleListID, taskID: tid)
                    changes += 1

                case .deleteReminder(let rid):
                    if let r = byID[rid] { try store.remove(r, commit: true) }
                    changes += 1
                }
            } catch {
                // 失败时保留原来的对应关系，下次再试
                if let old = previous(for: action) { records.append(old) }
            }
        }

        // 去重（同一条提醒或任务只保留一条对应关系）
        var seenRem = Set<String>()
        var seenTask = Set<String>()
        let unique = records.filter { seenRem.insert($0.reminderID).inserted && seenTask.insert($0.taskID).inserted }
        return (unique, changes)
    }

    static func side(_ r: EKReminder) -> SyncSideItem {
        SyncSideItem(id: r.calendarItemIdentifier,
                     altID: r.calendarItemExternalIdentifier,
                     data: data(of: r),
                     modified: r.lastModifiedDate,
                     completedAt: r.completionDate)
    }

    static func side(_ t: GoogleAPI.TaskItem) -> SyncSideItem {
        SyncSideItem(id: t.id,
                     altID: nil,
                     data: SyncTaskData(task: t),
                     modified: GoogleAPI.parseRFC3339(t.updated),
                     completedAt: GoogleAPI.parseRFC3339(t.completed))
    }

    static func data(of r: EKReminder) -> SyncTaskData {
        var day: String? = nil
        var minute: Int? = nil
        if let c = r.dueDateComponents, let y = c.year, let m = c.month, let d = c.day {
            day = String(format: "%04d-%02d-%02d", y, m, d)
            if let h = c.hour { minute = h * 60 + (c.minute ?? 0) }
        }
        return SyncTaskData(title: r.title ?? "", notes: r.notes ?? "", dueDay: day, minute: minute, completed: r.isCompleted)
    }

    static func apply(_ data: SyncTaskData, to r: EKReminder) {
        r.title = data.title
        r.notes = data.notes.isEmpty ? nil : data.notes
        EventKitSource.apply(due: data.dueDate, hasTime: data.minute != nil, to: r)
        r.isCompleted = data.completed
    }
}
