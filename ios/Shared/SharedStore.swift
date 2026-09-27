import Foundation

struct AppSettings: Codable, Equatable {
    /// iOS 类型的 Google OAuth 客户端 ID
    var googleClientID: String = ""
    var googleCalendarEnabled: Bool = true
    var googleTasksEnabled: Bool = true
    /// 容器键 → 是否显示；没有记录时用容器自己的默认值
    var visibility: [String: Bool] = [:]
    var defaultTaskTarget: String = ""
    var defaultEventTarget: String = ""
    /// 和 Google Tasks 双向同步的「提醒事项」列表（EKCalendar 标识）
    var syncedReminderLists: [String] = []
    var dailySummaryEnabled: Bool = true
    var dailySummaryHour: Int = 8
    var dailySummaryMinute: Int = 30
    /// 为 Google 来源的日程/带时间待办安排本地提醒
    var googleAlertsEnabled: Bool = true
    var googleAlertMinutes: Int = 10
    var showUndated: Bool = true
    var widgetShowCompleted: Bool = false
    var onboardingDone: Bool = false

    init() {}

    // 逐项读取，新版本增加字段时旧的设置文件也能正常读
    init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        let d = AppSettings()
        googleClientID = try c.decodeIfPresent(String.self, forKey: .googleClientID) ?? d.googleClientID
        googleCalendarEnabled = try c.decodeIfPresent(Bool.self, forKey: .googleCalendarEnabled) ?? d.googleCalendarEnabled
        googleTasksEnabled = try c.decodeIfPresent(Bool.self, forKey: .googleTasksEnabled) ?? d.googleTasksEnabled
        visibility = try c.decodeIfPresent([String: Bool].self, forKey: .visibility) ?? d.visibility
        defaultTaskTarget = try c.decodeIfPresent(String.self, forKey: .defaultTaskTarget) ?? d.defaultTaskTarget
        defaultEventTarget = try c.decodeIfPresent(String.self, forKey: .defaultEventTarget) ?? d.defaultEventTarget
        syncedReminderLists = try c.decodeIfPresent([String].self, forKey: .syncedReminderLists) ?? d.syncedReminderLists
        dailySummaryEnabled = try c.decodeIfPresent(Bool.self, forKey: .dailySummaryEnabled) ?? d.dailySummaryEnabled
        dailySummaryHour = try c.decodeIfPresent(Int.self, forKey: .dailySummaryHour) ?? d.dailySummaryHour
        dailySummaryMinute = try c.decodeIfPresent(Int.self, forKey: .dailySummaryMinute) ?? d.dailySummaryMinute
        googleAlertsEnabled = try c.decodeIfPresent(Bool.self, forKey: .googleAlertsEnabled) ?? d.googleAlertsEnabled
        googleAlertMinutes = try c.decodeIfPresent(Int.self, forKey: .googleAlertMinutes) ?? d.googleAlertMinutes
        showUndated = try c.decodeIfPresent(Bool.self, forKey: .showUndated) ?? d.showUndated
        widgetShowCompleted = try c.decodeIfPresent(Bool.self, forKey: .widgetShowCompleted) ?? d.widgetShowCompleted
        onboardingDone = try c.decodeIfPresent(Bool.self, forKey: .onboardingDone) ?? d.onboardingDone
    }

    func isVisible(_ c: SourceContainer) -> Bool {
        visibility[c.key] ?? c.defaultVisible
    }
}

/// 提醒事项 ↔ Google Tasks 的对应关系
struct SyncRecord: Codable, Equatable {
    var reminderID: String
    var reminderAltID: String?
    var taskID: String
    var fingerprint: String
}

struct ListPair: Codable, Equatable {
    var reminderListID: String
    var googleListID: String
    var records: [SyncRecord] = []
}

struct ReminderSyncState: Codable {
    var pairs: [String: ListPair] = [:]
    var lastRun: Date? = nil
    var lastError: String? = nil
    var lastChanges: Int = 0

    /// 已经和提醒事项配对的 Google 清单（这些清单在 iPhone 上直接显示为提醒事项，避免重复）
    var pairedGoogleListIDs: Set<String> { Set(pairs.values.map { $0.googleListID }) }
}

/// App 和小组件共享的文件存储（App Group 不可用时退回 App 自己的目录）
enum SharedStore {
    static func url(_ name: String) -> URL {
        AppGroup.containerURL.appendingPathComponent(name)
    }

    static func read<T: Decodable>(_ type: T.Type, from name: String) -> T? {
        guard let data = try? Data(contentsOf: url(name)) else { return nil }
        return try? JSONDecoder().decode(T.self, from: data)
    }

    static func write<T: Encodable>(_ value: T, to name: String) {
        guard let data = try? JSONEncoder().encode(value) else { return }
        try? data.write(to: url(name), options: [.atomic, .completeFileProtectionUntilFirstUserAuthentication])
    }

    static func delete(_ name: String) {
        try? FileManager.default.removeItem(at: url(name))
    }

    static var settings: AppSettings {
        get { read(AppSettings.self, from: "settings.json") ?? AppSettings() }
        set { write(newValue, to: "settings.json") }
    }

    static var snapshot: AgendaSnapshot? {
        get { read(AgendaSnapshot.self, from: "snapshot.json") }
        set {
            if let v = newValue { write(v, to: "snapshot.json") } else { delete("snapshot.json") }
        }
    }

    static var token: GoogleToken? {
        get { read(GoogleToken.self, from: "google_token.json") }
        set {
            if let v = newValue { write(v, to: "google_token.json") } else { delete("google_token.json") }
        }
    }

    static var syncState: ReminderSyncState {
        get { read(ReminderSyncState.self, from: "reminder_sync.json") ?? ReminderSyncState() }
        set { write(newValue, to: "reminder_sync.json") }
    }

    /// 小组件里打勾后，立即改掉快照里的状态，免得等下次同步才变
    static func markCompleted(itemID: String, completed: Bool) {
        guard var snap = snapshot, let idx = snap.items.firstIndex(where: { $0.id == itemID }) else { return }
        snap.items[idx].isCompleted = completed
        snap.items[idx].completedAt = completed ? Date() : nil
        snapshot = snap
    }
}
