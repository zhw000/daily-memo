import Foundation

/// 把 iPhone 日历、提醒事项、Google 日历、Google Tasks 合在一起
enum AgendaLoader {
    struct Result {
        var items: [AgendaItem] = []
        var containers: [SourceContainer] = []
        var errors: [String] = []
    }

    /// 读取的时间范围：昨天 ~ 两周后
    static func range(now: Date) -> (from: Date, to: Date) {
        let cal = DateText.calendar
        let today = cal.startOfDay(for: now)
        let from = cal.date(byAdding: .day, value: -1, to: today) ?? today
        let to = cal.date(byAdding: .day, value: 15, to: today) ?? today
        return (from, to)
    }

    static func loadLocal(settings: AppSettings, now: Date) async -> Result {
        let ek = EventKitSource.shared
        let (from, to) = range(now: now)
        var result = Result()
        result.containers = ek.eventCalendars() + ek.reminderLists()
        let hidden = Set(result.containers.filter { !settings.isVisible($0) }.map { $0.key })
        result.items = ek.events(from: from, to: to, hiddenKeys: hidden)
        let completedSince = DateText.calendar.date(byAdding: .day, value: -7, to: now) ?? now
        result.items += await ek.reminders(hiddenKeys: hidden, completedSince: completedSince)
        return result
    }

    static func loadGoogle(settings: AppSettings, now: Date) async -> Result {
        var result = Result()
        guard await GoogleSession.shared.isSignedIn else { return result }
        let (from, to) = range(now: now)
        let paired = SharedStore.syncState.pairedGoogleListIDs

        if settings.googleCalendarEnabled {
            do {
                let cals = try await GoogleAPI.calendars()
                result.containers += cals
                var firstError: Error? = nil
                var okCount = 0
                for cal in cals where settings.isVisible(cal) {
                    do {
                        result.items += try await GoogleAPI.events(calendar: cal, from: from, to: to)
                        okCount += 1
                    } catch {
                        if firstError == nil { firstError = error }
                    }
                }
                if okCount == 0, let err = firstError { result.errors.append("Google 日历：" + describe(err)) }
            } catch {
                result.errors.append("Google 日历：" + describe(error))
            }
        }

        if settings.googleTasksEnabled {
            do {
                // 和「提醒事项」配对的清单在 iPhone 上直接显示为提醒事项，这里跳过免得重复
                let lists = try await GoogleAPI.taskLists().filter { !paired.contains($0.nativeID) }
                result.containers += lists
                for list in lists where settings.isVisible(list) {
                    result.items += try await GoogleAPI.tasksForDisplay(list: list)
                }
            } catch {
                result.errors.append("Google Tasks：" + describe(error))
            }
        }
        return result
    }

    /// 同一个 Google 账号也添加到了 iPhone「日历」时，同一个日程会出现两次，这里去掉 Google 那份
    static func dedupe(_ items: [AgendaItem]) -> [AgendaItem] {
        var keys = Set<String>()
        for it in items where it.source == .ekEvent {
            guard let s = it.start else { continue }
            let minute = Int(s.timeIntervalSince1970 / 60)
            if let ext = it.externalID, !ext.isEmpty { keys.insert("x|\(ext)|\(minute)") }
            keys.insert("t|\(it.title)|\(minute)|\(it.isAllDay)")
        }
        guard !keys.isEmpty else { return items }
        return items.filter { it in
            guard it.source == .googleEvent, let s = it.start else { return true }
            let minute = Int(s.timeIntervalSince1970 / 60)
            if let ext = it.externalID, keys.contains("x|\(ext)|\(minute)") { return false }
            return !keys.contains("t|\(it.title)|\(minute)|\(it.isAllDay)")
        }
    }

    static func describe(_ error: Error) -> String {
        if let e = error as? LocalizedError, let d = e.errorDescription { return d }
        let ns = error as NSError
        if ns.domain == NSURLErrorDomain {
            switch ns.code {
            case NSURLErrorTimedOut: return "连接超时（在国内访问 Google 需要网络代理）"
            case NSURLErrorNotConnectedToInternet: return "没有网络连接"
            default: return "网络错误（在国内访问 Google 需要网络代理）"
            }
        }
        return error.localizedDescription
    }
}

/// 小组件用的数据：iPhone 日历/提醒事项实时读取；Google 数据用 App 同步好的快照，太旧时再联网刷新
enum WidgetData {
    static func load(now: Date) async -> [AgendaItem] {
        let settings = SharedStore.settings
        let snapshot = SharedStore.snapshot
        let hasEventKit = EventKitSource.isAuthorized(.event) || EventKitSource.isAuthorized(.reminder)
        guard hasEventKit else { return snapshot?.items ?? [] }

        var items = await AgendaLoader.loadLocal(settings: settings, now: now).items
        var google = (snapshot?.items ?? []).filter { $0.source == .googleEvent || $0.source == .googleTask }
        let stale = snapshot.map { now.timeIntervalSince($0.generatedAt) > 30 * 60 } ?? true
        if stale, await GoogleSession.shared.isSignedIn {
            GoogleAPI.requestTimeout = 8
            let fresh = await AgendaLoader.loadGoogle(settings: settings, now: now)
            if fresh.errors.isEmpty { google = fresh.items }
        }
        items += google
        return AgendaLoader.dedupe(items)
    }
}
