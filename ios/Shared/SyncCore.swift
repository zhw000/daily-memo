import Foundation

/// 同步比较用的内容（提醒事项和 Google 任务都转换成这个）
struct SyncTaskData: Equatable {
    var title: String
    var notes: String
    /// yyyy-MM-dd
    var dueDay: String?
    /// 一天中的第几分钟；只有有日期时才有意义
    var minute: Int?
    var completed: Bool

    var fingerprint: String {
        [title, notes, dueDay ?? "", minute.map { String($0) } ?? "", completed ? "1" : "0"].joined(separator: "\u{1F}")
    }

    /// 首次配对时用来匹配两边「同一件事」
    var matchKey: String { title.lowercased() + "\u{1F}" + (dueDay ?? "") }

    init(title: String, notes: String, dueDay: String?, minute: Int?, completed: Bool) {
        self.title = title.trimmingCharacters(in: .whitespacesAndNewlines)
        self.notes = notes.trimmingCharacters(in: .whitespacesAndNewlines)
        self.dueDay = dueDay
        self.minute = dueDay == nil ? nil : minute
        self.completed = completed
    }

    /// Google 任务 → 同步内容（标题开头的「15:00 」表示时间）
    init(task t: GoogleAPI.TaskItem) {
        let raw = (t.title ?? "").trimmingCharacters(in: .whitespacesAndNewlines)
        let day = t.due.map { String($0.prefix(10)) }
        var title = raw
        var minute: Int? = nil
        if day != nil {
            let split = GoogleAPI.splitTaskTitle(raw)
            title = split.title
            minute = split.minute
        }
        self.init(title: title, notes: t.notes ?? "", dueDay: day, minute: minute, completed: t.status == "completed")
    }

    /// 写给 Google 的字段
    var googleFields: [String: Any] {
        let dueValue: Any = dueDay.map { ($0 + "T00:00:00.000Z") as Any } ?? NSNull()
        var fields: [String: Any] = [
            "title": GoogleAPI.composeTaskTitle(title, minute: dueDay == nil ? nil : minute),
            "notes": notes,
            "due": dueValue,
            "status": completed ? "completed" : "needsAction",
        ]
        if !completed { fields["completed"] = NSNull() }
        return fields
    }

    /// 截止日期（本地时间）
    var dueDate: Date? {
        guard let day = GoogleAPI.parseDay(dueDay) else { return nil }
        return day.addingTimeInterval(TimeInterval((minute ?? 0) * 60))
    }
}

struct SyncSideItem {
    var id: String
    var altID: String?
    var data: SyncTaskData
    var modified: Date?
    var completedAt: Date?
}

enum SyncAction: Equatable {
    /// 两边一致，只记下对应关系
    case link(reminderID: String, taskID: String, data: SyncTaskData)
    case updateTask(reminderID: String, taskID: String, data: SyncTaskData)
    case updateReminder(reminderID: String, taskID: String, data: SyncTaskData)
    case createTask(reminderID: String, data: SyncTaskData)
    case createReminder(taskID: String, data: SyncTaskData)
    case deleteTask(taskID: String)
    case deleteReminder(reminderID: String)
}

struct SyncPlan {
    var actions: [SyncAction] = []
    /// 因为「一次删除太多」被暂停的记录，原样保留
    var heldRecords: [SyncRecord] = []
    var deletionsPaused = false

    var changeCount: Int {
        actions.filter { action -> Bool in
            if case .link = action { return false }
            return true
        }.count
    }
}

/// 双向同步的决策：对比上次同步时的内容（fingerprint），哪边变了就以哪边为准；两边都变了以较新的为准。
enum SyncPlanner {
    static func plan(records: [SyncRecord], reminders: [SyncSideItem], tasks: [SyncSideItem], now: Date) -> SyncPlan {
        var remByID: [String: SyncSideItem] = [:]
        var remByAlt: [String: SyncSideItem] = [:]
        for r in reminders {
            remByID[r.id] = r
            if let alt = r.altID, !alt.isEmpty { remByAlt[alt] = r }
        }
        var taskByID: [String: SyncSideItem] = [:]
        for t in tasks { taskByID[t.id] = t }

        var usedRem = Set<String>()
        var usedTask = Set<String>()
        var plan = SyncPlan()
        var deletions: [(SyncAction, SyncRecord)] = []

        for rec in records {
            var r = remByID[rec.reminderID]
            if r == nil, let alt = rec.reminderAltID { r = remByAlt[alt] }
            if let found = r, usedRem.contains(found.id) { r = nil }
            var t = taskByID[rec.taskID]
            if let found = t, usedTask.contains(found.id) { t = nil }
            if let r = r { usedRem.insert(r.id) }
            if let t = t { usedTask.insert(t.id) }

            switch (r, t) {
            case (nil, nil):
                continue
            case (let r?, nil):
                // Google 那边删掉了：提醒事项没改过就一起删；改过就重新传上去
                if r.data.fingerprint == rec.fingerprint {
                    deletions.append((.deleteReminder(reminderID: r.id), rec))
                } else {
                    plan.actions.append(.createTask(reminderID: r.id, data: r.data))
                }
            case (nil, let t?):
                if t.data.fingerprint == rec.fingerprint {
                    deletions.append((.deleteTask(taskID: t.id), rec))
                } else {
                    plan.actions.append(.createReminder(taskID: t.id, data: t.data))
                }
            case (let r?, let t?):
                plan.actions.append(resolve(reminder: r, task: t, last: rec.fingerprint))
            }
        }

        // 防止误删：一次要删的太多（例如列表被整个删掉、账号切换）时先不删
        if deletions.count >= 5 && deletions.count * 2 > records.count {
            plan.deletionsPaused = true
            plan.heldRecords = deletions.map { $0.1 }
        } else {
            plan.actions.append(contentsOf: deletions.map { $0.0 })
        }

        // 新出现的提醒事项：先按「标题 + 日期」找 Google 那边有没有同样的，没有再新建
        var freeTasks: [String: [SyncSideItem]] = [:]
        for t in tasks where !usedTask.contains(t.id) {
            freeTasks[t.data.matchKey, default: []].append(t)
        }
        let cutoff = now.addingTimeInterval(-7 * 86400)
        for r in reminders where !usedRem.contains(r.id) {
            if r.data.completed && (r.completedAt ?? .distantPast) < cutoff { continue }
            if var candidates = freeTasks[r.data.matchKey], !candidates.isEmpty {
                let t = candidates.removeFirst()
                freeTasks[r.data.matchKey] = candidates
                usedTask.insert(t.id)
                plan.actions.append(resolve(reminder: r, task: t, last: nil))
            } else {
                plan.actions.append(.createTask(reminderID: r.id, data: r.data))
            }
        }

        // 新出现的 Google 任务（已完成的不再搬过来）
        for t in tasks where !usedTask.contains(t.id) && !t.data.completed {
            plan.actions.append(.createReminder(taskID: t.id, data: t.data))
        }
        return plan
    }

    private static func resolve(reminder r: SyncSideItem, task t: SyncSideItem, last: String?) -> SyncAction {
        let fr = r.data.fingerprint
        let ft = t.data.fingerprint
        if fr == ft { return .link(reminderID: r.id, taskID: t.id, data: r.data) }
        if let last = last {
            if fr != last && ft == last { return .updateTask(reminderID: r.id, taskID: t.id, data: r.data) }
            if ft != last && fr == last { return .updateReminder(reminderID: r.id, taskID: t.id, data: t.data) }
        }
        // 两边都改了（或第一次配对）：以较新的为准
        let reminderNewer = (r.modified ?? .distantPast) >= (t.modified ?? .distantPast)
        return reminderNewer
            ? .updateTask(reminderID: r.id, taskID: t.id, data: r.data)
            : .updateReminder(reminderID: r.id, taskID: t.id, data: t.data)
    }
}
