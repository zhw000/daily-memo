import Foundation
import UserNotifications

/// 本地通知：每天早上推送当天安排；Google 来源的日程和带时间的待办开始前提醒
/// （iPhone 日历和提醒事项里的内容由系统自己提醒）
enum NotificationScheduler {
    static func requestAuthorization() async -> Bool {
        (try? await UNUserNotificationCenter.current().requestAuthorization(options: [.alert, .sound, .badge])) ?? false
    }

    static func isAuthorized() async -> Bool {
        let status = await UNUserNotificationCenter.current().notificationSettings().authorizationStatus
        return status == .authorized || status == .provisional || status == .ephemeral
    }

    static func reschedule(items: [AgendaItem], settings: AppSettings) async {
        let center = UNUserNotificationCenter.current()
        let pending = await center.pendingNotificationRequests()
        let ours = pending.map { $0.identifier }.filter { $0.hasPrefix("dm-") }
        center.removePendingNotificationRequests(withIdentifiers: ours)
        guard await isAuthorized() else { return }

        let cal = DateText.calendar
        let now = Date()
        let today = cal.startOfDay(for: now)

        if settings.dailySummaryEnabled {
            for offset in 0..<7 {
                guard let day = cal.date(byAdding: .day, value: offset, to: today) else { continue }
                var comps = cal.dateComponents([.year, .month, .day], from: day)
                comps.hour = settings.dailySummaryHour
                comps.minute = settings.dailySummaryMinute
                guard let fire = cal.date(from: comps), fire > now else { continue }

                let agenda = AgendaBuilder.build(items, day: day, carryOver: offset == 0)
                let content = UNMutableNotificationContent()
                content.title = (offset == 0 ? "早上好 · " : "") + "\(DateText.monthDay(day)) \(DateText.weekday(day))"
                content.body = summaryBody(agenda)
                content.sound = .default
                content.threadIdentifier = "daily"
                let trigger = UNCalendarNotificationTrigger(dateMatching: comps, repeats: false)
                try? await center.add(UNNotificationRequest(identifier: "dm-daily-\(offset)", content: content, trigger: trigger))
            }
        }

        if settings.googleAlertsEnabled {
            let lead = TimeInterval(max(0, settings.googleAlertMinutes) * 60)
            let horizon = now.addingTimeInterval(3 * 86400)
            let candidates = items.compactMap { item -> (AgendaItem, Date)? in
                let isGoogleTimed = (item.source == .googleEvent && !item.isAllDay) || (item.source == .googleTask && item.hasTime && !item.isCompleted)
                guard isGoogleTimed, let start = item.start else { return nil }
                let fire = start.addingTimeInterval(-lead)
                return fire > now && fire < horizon ? (item, fire) : nil
            }
            .sorted { $0.1 < $1.1 }
            .prefix(40)

            for (item, fire) in candidates {
                let content = UNMutableNotificationContent()
                content.title = item.title
                let start = item.start ?? fire
                var body = lead > 0 ? "\(Int(lead / 60)) 分钟后开始 · \(DateText.time(start))" : "现在开始 · \(DateText.time(start))"
                if item.isTask { body = "到时间了 · \(DateText.time(start))" }
                if let loc = item.location { body += " · \(loc)" }
                content.body = body
                content.subtitle = item.containerName
                content.sound = .default
                let comps = cal.dateComponents([.year, .month, .day, .hour, .minute], from: fire)
                let trigger = UNCalendarNotificationTrigger(dateMatching: comps, repeats: false)
                try? await center.add(UNNotificationRequest(identifier: "dm-alert-\(item.id)", content: content, trigger: trigger))
            }
        }
    }

    static func summaryBody(_ agenda: DayAgenda) -> String {
        var parts: [String] = []
        if agenda.eventCount > 0 { parts.append("\(agenda.eventCount) 个日程") }
        let open = agenda.openTaskCount + agenda.overdue.count
        if open > 0 { parts.append("\(open) 项待办") }
        if agenda.overdue.count > 0 { parts.append("\(agenda.overdue.count) 项已过期") }
        guard !parts.isEmpty else { return "今天没有安排，放松一下～" }
        let top = (agenda.allDay + agenda.scheduled + agenda.overdue + agenda.dayTasks)
            .filter { !$0.isCompleted }
            .prefix(4)
            .map { $0.hasTime && $0.start != nil ? "\(DateText.time($0.start!)) \($0.title)" : $0.title }
        return parts.joined(separator: " · ") + "\n" + top.joined(separator: "、")
    }
}
