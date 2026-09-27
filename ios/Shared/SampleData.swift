import Foundation

/// 小组件占位图、界面演示（截图）用的示例数据
enum SampleData {
    static func containers() -> [SourceContainer] {
        [
            SourceContainer(key: "ekr:reminders", kind: .task, source: .ekReminder, nativeID: "reminders", name: "提醒事项", colorHex: "#FF9500", isDefault: true, accountName: "iCloud"),
            SourceContainer(key: "gt:list1", kind: .task, source: .googleTask, nativeID: "list1", name: "我的任务", colorHex: GoogleAPI.tasksColor, isDefault: true, accountName: "Google"),
            SourceContainer(key: "ekc:family", kind: .event, source: .ekEvent, nativeID: "family", name: "家庭", colorHex: "#34C759", accountName: "iCloud"),
            SourceContainer(key: "gc:work", kind: .event, source: .googleEvent, nativeID: "work", name: "工作", colorHex: "#039BE5", isDefault: true, accountName: "Google"),
            SourceContainer(key: "gc:holiday", kind: .event, source: .googleEvent, nativeID: "holiday", name: "中国节假日", colorHex: "#0B8043", writable: false, accountName: "Google"),
        ]
    }

    static func items(now: Date) -> [AgendaItem] {
        let cal = DateText.calendar
        let today = cal.startOfDay(for: now)
        func day(_ n: Int) -> Date { cal.date(byAdding: .day, value: n, to: today) ?? today }
        func at(_ n: Int, _ h: Int, _ m: Int = 0) -> Date { day(n).addingTimeInterval(TimeInterval(h * 3600 + m * 60)) }
        var n = 0
        func task(_ c: String, _ title: String, _ due: Date?, time: Bool = false, done: Bool = false) -> AgendaItem {
            n += 1
            let container = containers().first { $0.key == c }!
            return AgendaItem(id: "demo|t\(n)", kind: .task, source: container.source, nativeID: "t\(n)", title: title,
                              notes: nil, location: nil, start: due, end: nil, isAllDay: !time, isCompleted: done,
                              completedAt: done ? now.addingTimeInterval(-3600) : nil,
                              containerID: container.nativeID, containerName: container.name, colorHex: container.colorHex)
        }
        func event(_ c: String, _ title: String, _ start: Date, _ end: Date, allDay: Bool = false, location: String? = nil) -> AgendaItem {
            n += 1
            let container = containers().first { $0.key == c }!
            return AgendaItem(id: "demo|e\(n)", kind: .event, source: container.source, nativeID: "e\(n)", title: title,
                              notes: nil, location: location, start: start, end: end, isAllDay: allDay,
                              containerID: container.nativeID, containerName: container.name, colorHex: container.colorHex,
                              isReadOnly: !container.writable)
        }
        return [
            task("gt:list1", "交水电费", day(-1)),
            event("gc:holiday", "国庆节前调休上班", day(0), day(1), allDay: true),
            event("gc:work", "部门站会", at(0, 9, 30), at(0, 10)),
            event("gc:work", "产品评审会", at(0, 14), at(0, 15, 30), location: "3 楼大会议室"),
            task("gt:list1", "交周报", at(0, 16), time: true),
            event("ekc:family", "接孩子放学", at(0, 18, 30), at(0, 19)),
            task("ekr:reminders", "买牛奶和鸡蛋", day(0)),
            task("ekr:reminders", "给妈妈打电话", day(0)),
            task("gt:list1", "整理书架", nil),
            task("ekr:reminders", "取快递", day(0), done: true),
            event("ekc:family", "看牙医", at(1, 10), at(1, 11), location: "市口腔医院"),
            task("gt:list1", "周报初稿", day(1)),
            event("gc:work", "项目周会", at(2, 14), at(2, 15)),
            event("ekc:family", "妈妈生日", day(3), day(4), allDay: true),
            task("ekr:reminders", "订生日蛋糕", day(2)),
            event("gc:holiday", "国庆节", day(4), day(11), allDay: true),
            event("ekc:family", "健身", at(5, 19), at(5, 20)),
        ]
    }
}
