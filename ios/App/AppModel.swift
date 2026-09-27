import SwiftUI
import EventKit
import WidgetKit
import UIKit

/// 新建/编辑表单的内容
struct EditorDraft {
    var original: AgendaItem?
    var kind: ItemKind
    var title: String = ""
    var notes: String = ""
    var location: String = ""
    var hasDate: Bool = true
    var date: Date = Date()
    var hasTime: Bool = false
    var startTime: Date = Date()
    var endTime: Date = Date().addingTimeInterval(3600)
    var targetKey: String = ""

    var isNew: Bool { original == nil }
}

@MainActor
final class AppModel: ObservableObject {
    static let shared = AppModel()

    @Published var items: [AgendaItem] = []
    @Published var containers: [SourceContainer] = []
    @Published var settings: AppSettings {
        didSet {
            if settings != oldValue && !isDemo { SharedStore.settings = settings }
        }
    }
    @Published var isLoading = false
    @Published var errors: [String] = []
    @Published var lastRefresh: Date?
    @Published var googleSignedIn = false
    @Published var googleEmail: String?
    @Published var eventsAuthorized = false
    @Published var remindersAuthorized = false
    @Published var syncState = ReminderSyncState()
    @Published var toast: String?
    @Published var now = Date()
    @Published var selectedTab = "today"
    @Published var editorDraft: EditorDraft?

    let isDemo: Bool
    let demoScreen: String?
    private var pendingRefresh = false
    private var syncTask: Task<Void, Never>?

    init() {
        let args = ProcessInfo.processInfo.arguments
        isDemo = args.contains("-demo")
        if let idx = args.firstIndex(of: "-screen"), idx + 1 < args.count { demoScreen = args[idx + 1] } else { demoScreen = nil }
        settings = isDemo ? AppSettings() : SharedStore.settings

        if isDemo {
            items = SampleData.items(now: Date())
            containers = SampleData.containers()
            googleSignedIn = true
            googleEmail = "me@gmail.com"
            eventsAuthorized = true
            remindersAuthorized = true
            lastRefresh = Date()
            settings.googleClientID = "1234567890-demo.apps.googleusercontent.com"
            settings.syncedReminderLists = ["reminders"]
            var demoSync = ReminderSyncState()
            demoSync.lastRun = Date()
            demoSync.lastChanges = 3
            syncState = demoSync
            if let screen = demoScreen, ["today", "upcoming", "lists", "settings"].contains(screen) { selectedTab = screen }
            return
        }

        if let snap = SharedStore.snapshot {
            items = snap.items
            containers = snap.containers
            lastRefresh = snap.generatedAt
        }
        syncState = SharedStore.syncState
        refreshAuthState()
    }

    // MARK: - 状态

    func refreshAuthState() {
        guard !isDemo else { return }
        eventsAuthorized = EventKitSource.isAuthorized(.event)
        remindersAuthorized = EventKitSource.isAuthorized(.reminder)
        let token = SharedStore.token
        googleSignedIn = token != nil
        googleEmail = token?.email
    }

    var today: DayAgenda {
        AgendaBuilder.build(items, day: now, carryOver: true)
    }

    func agenda(for day: Date) -> DayAgenda {
        AgendaBuilder.build(items, day: day, carryOver: false)
    }

    var needsOnboarding: Bool {
        !isDemo && !settings.onboardingDone && (!eventsAuthorized || !remindersAuthorized)
    }

    // MARK: - 刷新

    func refresh() async {
        guard !isDemo else { return }
        if isLoading {
            pendingRefresh = true
            return
        }
        isLoading = true
        refreshAuthState()
        var errs: [String] = []

        // 先把提醒事项和 Google Tasks 对齐，再读取数据
        if googleSignedIn && remindersAuthorized && !settings.syncedReminderLists.isEmpty {
            let report = await ReminderSync.shared.run(settings: settings)
            if let e = report.error { errs.append("提醒事项同步：" + e) }
            if report.paused { errs.append("提醒事项同步：检测到大量删除，为防止误删已暂停删除，请检查后在设置里关闭再打开同步") }
            syncState = SharedStore.syncState
        }

        let time = Date()
        let local = await AgendaLoader.loadLocal(settings: settings, now: time)
        var all = local.items
        var cons = local.containers
        refreshAuthState()
        if googleSignedIn {
            let g = await AgendaLoader.loadGoogle(settings: settings, now: time)
            all += g.items
            cons += g.containers
            errs += g.errors
        }
        all = AgendaLoader.dedupe(all)

        items = all
        containers = cons
        errors = errs
        lastRefresh = Date()
        now = Date()
        isLoading = false
        publish()

        if pendingRefresh {
            pendingRefresh = false
            await refresh()
        }
    }

    /// 写快照给小组件、刷新小组件、重新安排通知
    private func publish() {
        guard !isDemo else { return }
        SharedStore.snapshot = AgendaSnapshot(generatedAt: Date(), items: items, containers: containers, errors: errors)
        WidgetCenter.shared.reloadAllTimelines()
        let snapshotItems = items
        let currentSettings = settings
        Task { await NotificationScheduler.reschedule(items: snapshotItems, settings: currentSettings) }
    }

    /// 本地改动后稍等一下再完整同步一次（把提醒事项的改动推到 Google）
    private func scheduleSync() {
        syncTask?.cancel()
        syncTask = Task { [weak self] in
            try? await Task.sleep(nanoseconds: 4_000_000_000)
            guard !Task.isCancelled else { return }
            await self?.refresh()
        }
    }

    func requestPermissions() async {
        guard !isDemo else { return }
        let r = await EventKitSource.shared.requestAccess()
        eventsAuthorized = r.events
        remindersAuthorized = r.reminders
        _ = await NotificationScheduler.requestAuthorization()
        await refresh()
    }

    // MARK: - 默认保存位置

    var taskTargets: [SourceContainer] { containers.filter { $0.kind == .task && $0.writable } }
    var eventTargets: [SourceContainer] { containers.filter { $0.kind == .event && $0.writable } }

    func resolveTaskTarget() -> String? {
        let targets = taskTargets
        if targets.contains(where: { $0.key == settings.defaultTaskTarget }) { return settings.defaultTaskTarget }
        // 已和 Google 同步的提醒事项列表最好：iPhone 会按时提醒，电脑上也能看到
        if let synced = targets.first(where: { $0.source == .ekReminder && settings.syncedReminderLists.contains($0.nativeID) }) {
            return synced.key
        }
        if googleSignedIn, let g = targets.first(where: { $0.source == .googleTask }) { return g.key }
        return (targets.first(where: { $0.isDefault && $0.source == .ekReminder }) ?? targets.first)?.key
    }

    func resolveEventTarget() -> String? {
        let targets = eventTargets
        if targets.contains(where: { $0.key == settings.defaultEventTarget }) { return settings.defaultEventTarget }
        if googleSignedIn, let g = targets.first(where: { $0.source == .googleEvent && $0.isDefault }) { return g.key }
        return (targets.first(where: { $0.isDefault && $0.source == .ekEvent }) ?? targets.first)?.key
    }

    func container(for key: String) -> SourceContainer? {
        containers.first { $0.key == key }
    }

    // MARK: - 修改

    func toggle(_ item: AgendaItem) async {
        guard item.isTask, !item.isReadOnly else { return }
        let newValue = !item.isCompleted
        updateLocal(item.id) {
            $0.isCompleted = newValue
            $0.completedAt = newValue ? Date() : nil
        }
        if newValue { UIImpactFeedbackGenerator(style: .light).impactOccurred() }
        guard !isDemo else { return }
        do {
            switch item.source {
            case .ekReminder:
                try EventKitSource.shared.setReminderCompleted(id: item.nativeID, completed: newValue)
            case .googleTask:
                try await GoogleAPI.setTaskCompleted(listID: item.containerID, taskID: item.nativeID, completed: newValue)
            default:
                break
            }
            publish()
            scheduleSync()
        } catch {
            updateLocal(item.id) {
                $0.isCompleted = item.isCompleted
                $0.completedAt = item.completedAt
            }
            show(error)
        }
    }

    /// 快速添加：识别日期时间，没写日期就放到今天
    func quickAdd(_ text: String) async {
        let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { return }
        let parsed = QuickParser.parse(trimmed, now: Date())
        var draft = EditorDraft(original: nil, kind: .task)
        draft.title = parsed.title
        draft.hasDate = true
        draft.date = parsed.date ?? DateText.calendar.startOfDay(for: Date())
        if let when = parsed.when, parsed.minuteOfDay != nil {
            draft.hasTime = true
            draft.startTime = when
        }
        draft.targetKey = resolveTaskTarget() ?? ""
        do {
            try await save(draft)
            showToast("已添加「\(parsed.title)」")
        } catch {
            show(error)
        }
    }

    func newDraft(kind: ItemKind, date: Date? = nil) -> EditorDraft {
        let cal = DateText.calendar
        var d = EditorDraft(original: nil, kind: kind)
        d.date = cal.startOfDay(for: date ?? Date())
        // 默认从下一个整点开始
        let comps = cal.dateComponents([.year, .month, .day, .hour], from: Date().addingTimeInterval(3600))
        let nextHour = cal.date(from: comps) ?? Date()
        d.startTime = nextHour
        d.endTime = nextHour.addingTimeInterval(3600)
        d.hasTime = kind == .event
        d.targetKey = (kind == .task ? resolveTaskTarget() : resolveEventTarget()) ?? ""
        return d
    }

    func draft(for item: AgendaItem) -> EditorDraft {
        var d = EditorDraft(original: item, kind: item.kind)
        d.title = item.title
        d.notes = item.notes ?? ""
        d.location = item.location ?? ""
        d.hasDate = item.start != nil
        d.date = DateText.calendar.startOfDay(for: item.start ?? Date())
        d.hasTime = item.hasTime
        d.startTime = item.start ?? Date()
        d.endTime = item.end ?? (item.start ?? Date()).addingTimeInterval(3600)
        d.targetKey = item.containerKey
        return d
    }

    func save(_ draft: EditorDraft) async throws {
        let title = draft.title.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !title.isEmpty else { throw EditorError.emptyTitle }
        guard let target = ContainerKey.parse(draft.targetKey) else { throw EditorError.noTarget }
        let cal = DateText.calendar
        let notes = nonEmpty(draft.notes)
        let location = nonEmpty(draft.location)

        func combine(_ day: Date, _ time: Date) -> Date {
            let t = cal.dateComponents([.hour, .minute], from: time)
            return cal.date(bySettingHour: t.hour ?? 0, minute: t.minute ?? 0, second: 0, of: day) ?? day
        }

        // 算出时间
        var due: Date? = nil
        var start = Date()
        var end = Date()
        let allDay = !draft.hasTime
        if draft.kind == .task {
            if draft.hasDate { due = draft.hasTime ? combine(draft.date, draft.startTime) : cal.startOfDay(for: draft.date) }
        } else if allDay {
            start = cal.startOfDay(for: draft.date)
            var span = 1
            if let o = draft.original, o.isAllDay, let os = o.start {
                span = max(1, DateText.daysBetween(os, o.effectiveEnd))
            }
            end = cal.date(byAdding: .day, value: span, to: start) ?? start
        } else {
            start = combine(draft.date, draft.startTime)
            end = combine(draft.date, draft.endTime)
            if end <= start { end = cal.date(byAdding: .day, value: 1, to: end) ?? end }
        }

        if isDemo {
            editorDraft = nil
            return
        }

        if let original = draft.original, original.containerKey == draft.targetKey {
            try await update(original, title: title, notes: notes, location: location, due: due, hasTime: draft.hasTime,
                             start: start, end: end, allDay: allDay)
        } else {
            try await create(kind: draft.kind, target: target, title: title, notes: notes, location: location, due: due,
                             hasTime: draft.hasTime, start: start, end: end, allDay: allDay)
            if let original = draft.original {
                try await deleteRemote(original)
            }
        }
        await refresh()
        scheduleSync()
    }

    private func create(kind: ItemKind, target: (source: ItemSource, id: String), title: String, notes: String?, location: String?,
                        due: Date?, hasTime: Bool, start: Date, end: Date, allDay: Bool) async throws {
        switch target.source {
        case .ekReminder:
            _ = try EventKitSource.shared.createReminder(title: title, notes: notes, due: due, hasTime: hasTime, listID: target.id)
        case .googleTask:
            try await GoogleAPI.insertTask(listID: target.id, fields: GoogleAPI.taskFields(title: title, notes: notes, due: due, hasTime: hasTime))
        case .ekEvent:
            _ = try EventKitSource.shared.createEvent(title: title, start: start, end: end, allDay: allDay, notes: notes, location: location, calendarID: target.id)
        case .googleEvent:
            guard let cal = container(for: ContainerKey.make(.googleEvent, target.id)) else { throw EditorError.noTarget }
            _ = try await GoogleAPI.insertEvent(calendar: cal, title: title, start: start, end: end, allDay: allDay, notes: notes, location: location)
        }
    }

    private func update(_ item: AgendaItem, title: String, notes: String?, location: String?, due: Date?, hasTime: Bool,
                        start: Date, end: Date, allDay: Bool) async throws {
        switch item.source {
        case .ekReminder:
            _ = try EventKitSource.shared.updateReminder(id: item.nativeID, title: title, notes: notes, due: due, hasTime: hasTime)
        case .googleTask:
            try await GoogleAPI.patchTask(listID: item.containerID, taskID: item.nativeID,
                                          fields: GoogleAPI.taskFields(title: title, notes: notes, due: due, hasTime: hasTime))
        case .ekEvent:
            try EventKitSource.shared.updateEvent(item, title: title, start: start, end: end, allDay: allDay, notes: notes, location: location)
        case .googleEvent:
            guard let cal = container(for: item.containerKey) else { throw EditorError.noTarget }
            _ = try await GoogleAPI.updateEvent(calendar: cal, eventID: item.nativeID, title: title, start: start, end: end,
                                                allDay: allDay, notes: notes, location: location)
        }
    }

    func delete(_ item: AgendaItem) async {
        items.removeAll { $0.id == item.id }
        guard !isDemo else { return }
        do {
            try await deleteRemote(item)
            publish()
            scheduleSync()
        } catch {
            show(error)
            await refresh()
        }
    }

    private func deleteRemote(_ item: AgendaItem) async throws {
        switch item.source {
        case .ekReminder: try EventKitSource.shared.deleteReminder(id: item.nativeID)
        case .googleTask: try await GoogleAPI.deleteTask(listID: item.containerID, taskID: item.nativeID)
        case .ekEvent: try EventKitSource.shared.deleteEvent(item)
        case .googleEvent: try await GoogleAPI.deleteEvent(calendarID: item.containerID, eventID: item.nativeID)
        }
    }

    private func updateLocal(_ id: String, _ change: (inout AgendaItem) -> Void) {
        guard let idx = items.firstIndex(where: { $0.id == id }) else { return }
        var copy = items[idx]
        change(&copy)
        items[idx] = copy
    }

    // MARK: - Google 账号

    func finishGoogleSignIn(token: GoogleToken) async {
        await GoogleSession.shared.save(token)
        refreshAuthState()
        // 登录后默认把「提醒事项」的默认列表和 Google Tasks 同步，这样电脑上也能看到
        if settings.syncedReminderLists.isEmpty, remindersAuthorized,
           let def = EventKitSource.shared.store.defaultCalendarForNewReminders() {
            settings.syncedReminderLists = [def.calendarIdentifier]
        }
        showToast("已连接 Google 账号")
        await refresh()
    }

    func signOutGoogle() async {
        await GoogleSession.shared.signOut()
        refreshAuthState()
        await refresh()
    }

    func setReminderSync(listID: String, enabled: Bool) {
        var lists = settings.syncedReminderLists
        if enabled {
            if !lists.contains(listID) { lists.append(listID) }
        } else {
            lists.removeAll { $0 == listID }
        }
        settings.syncedReminderLists = lists
        Task { await refresh() }
    }

    func setVisible(_ container: SourceContainer, _ visible: Bool) {
        settings.visibility[container.key] = visible
        Task { await refresh() }
    }

    // MARK: - 提示

    func show(_ error: Error) {
        showToast(AgendaLoader.describe(error))
    }

    func showToast(_ text: String) {
        toast = text
        let current = text
        Task { [weak self] in
            try? await Task.sleep(nanoseconds: 2_500_000_000)
            if self?.toast == current { self?.toast = nil }
        }
    }

    func handle(url: URL) {
        if url.host == "today" { selectedTab = "today" }
    }
}

enum EditorError: LocalizedError {
    case emptyTitle
    case noTarget

    var errorDescription: String? {
        switch self {
        case .emptyTitle: return "请输入标题"
        case .noTarget: return "请选择保存位置（需要先允许访问日历/提醒事项，或登录 Google）"
        }
    }
}
