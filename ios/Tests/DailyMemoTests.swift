import XCTest
@testable import DailyMemo

final class QuickParserTests: XCTestCase {
    /// 2026-09-27 10:00（星期日）
    private var now: Date {
        DateText.calendar.date(from: DateComponents(year: 2026, month: 9, day: 27, hour: 10, minute: 0))!
    }

    private func day(_ y: Int, _ m: Int, _ d: Int) -> Date {
        DateText.calendar.date(from: DateComponents(year: y, month: m, day: d))!
    }

    private func check(_ input: String, _ title: String, _ date: Date?, _ minute: Int?, file: StaticString = #filePath, line: UInt = #line) {
        let r = QuickParser.parse(input, now: now)
        XCTAssertEqual(r.title, title, "标题：\(input)", file: file, line: line)
        XCTAssertEqual(r.date, date, "日期：\(input)", file: file, line: line)
        XCTAssertEqual(r.minuteOfDay, minute, "时间：\(input)", file: file, line: line)
    }

    func testDates() {
        check("明天下午3点 交报告", "交报告", day(2026, 9, 28), 15 * 60)
        check("明天下午3点交报告", "交报告", day(2026, 9, 28), 15 * 60)
        check("后天 买菜", "买菜", day(2026, 9, 29), nil)
        check("大后天上午9点半 体检", "体检", day(2026, 9, 30), 9 * 60 + 30)
        check("周五 交房租", "交房租", day(2026, 10, 2), nil)
        check("下周一 开会", "开会", day(2026, 9, 28), nil)
        check("下周三 交作业", "交作业", day(2026, 9, 30), nil)
        check("这周日 休息", "休息", day(2026, 9, 27), nil)
        check("10月1日 回家", "回家", day(2026, 10, 1), nil)
        check("十月一号 回家", "回家", day(2026, 10, 1), nil)
        check("3月5日 交税", "交税", day(2027, 3, 5), nil)
        check("2026-12-24 平安夜", "平安夜", day(2026, 12, 24), nil)
        check("12/31 跨年", "跨年", day(2026, 12, 31), nil)
        check("30号 交电费", "交电费", day(2026, 9, 30), nil)
        check("3天后 还书", "还书", day(2026, 9, 30), nil)
    }

    func testTimes() {
        check("今晚8点 看电影", "看电影", day(2026, 9, 27), 20 * 60)
        check("明早7:30 跑步", "跑步", day(2026, 9, 28), 7 * 60 + 30)
        check("晚上九点 吃药", "吃药", day(2026, 9, 27), 21 * 60)
        check("中午12点 吃饭", "吃饭", day(2026, 9, 27), 12 * 60)
        check("中午1点 午休", "午休", day(2026, 9, 27), 13 * 60)
        check("15:30 开会", "开会", day(2026, 9, 27), 15 * 60 + 30)
        check("8点 开会", "开会", day(2026, 9, 28), 8 * 60)
        check("半小时后 关火", "关火", day(2026, 9, 27), 10 * 60 + 30)
        check("20分钟后 出门", "出门", day(2026, 9, 27), 10 * 60 + 20)
        check("提醒我明天 买牛奶", "买牛奶", day(2026, 9, 28), nil)
    }

    func testPlainText() {
        for text in ["买一点东西", "去10号楼开会", "买2斤苹果", "整理书架"] {
            check(text, text, nil, nil)
        }
    }

    func testChineseNumbers() {
        XCTAssertEqual(QuickParser.cnNumber("十"), 10)
        XCTAssertEqual(QuickParser.cnNumber("十二"), 12)
        XCTAssertEqual(QuickParser.cnNumber("二十三"), 23)
        XCTAssertEqual(QuickParser.cnNumber("两"), 2)
        XCTAssertEqual(QuickParser.cnNumber("31"), 31)
    }
}

final class GoogleMappingTests: XCTestCase {
    private let list = SourceContainer(key: "gt:L1", kind: .task, source: .googleTask, nativeID: "L1", name: "我的任务", colorHex: "#1A73E8")

    func testTimePrefix() {
        XCTAssertEqual(GoogleAPI.splitTaskTitle("15:00 交报告").title, "交报告")
        XCTAssertEqual(GoogleAPI.splitTaskTitle("15:00 交报告").minute, 900)
        XCTAssertNil(GoogleAPI.splitTaskTitle("交报告").minute)
        XCTAssertEqual(GoogleAPI.composeTaskTitle("交报告", minute: 9 * 60 + 5), "09:05 交报告")
        XCTAssertEqual(GoogleAPI.composeTaskTitle("交报告", minute: nil), "交报告")
    }

    func testMapTask() throws {
        let json = #"{"id":"t1","title":"15:00 交报告","status":"needsAction","due":"2026-09-28T00:00:00.000Z"}"#
        let t = try JSONDecoder().decode(GoogleAPI.TaskItem.self, from: Data(json.utf8))
        let item = GoogleAPI.mapTask(t, list)
        XCTAssertEqual(item.title, "交报告")
        XCTAssertFalse(item.isAllDay)
        let expected = DateText.calendar.date(from: DateComponents(year: 2026, month: 9, day: 28, hour: 15))
        XCTAssertEqual(item.start, expected)
        XCTAssertEqual(item.id, "gt|L1|t1")
    }

    func testMapAllDayEvent() throws {
        let cal = SourceContainer(key: "gc:work", kind: .event, source: .googleEvent, nativeID: "work", name: "工作", colorHex: "#039BE5")
        let json = #"{"id":"e1","summary":"国庆节","start":{"date":"2026-10-01"},"end":{"date":"2026-10-08"}}"#
        let e = try JSONDecoder().decode(GoogleAPI.Event.self, from: Data(json.utf8))
        let item = try XCTUnwrap(GoogleAPI.mapEvent(e, cal))
        XCTAssertTrue(item.isAllDay)
        let oct7 = DateText.calendar.date(from: DateComponents(year: 2026, month: 10, day: 7))!
        let oct8 = DateText.calendar.date(from: DateComponents(year: 2026, month: 10, day: 8))!
        XCTAssertEqual(AgendaBuilder.build([item], day: oct7, carryOver: false).allDay.count, 1)
        XCTAssertEqual(AgendaBuilder.build([item], day: oct8, carryOver: false).allDay.count, 0)
    }

    func testTimedEventParsesOffset() throws {
        let cal = SourceContainer(key: "gc:work", kind: .event, source: .googleEvent, nativeID: "work", name: "工作", colorHex: "#039BE5")
        let json = #"{"id":"e2","summary":"组会","start":{"dateTime":"2026-09-27T10:00:00+08:00"},"end":{"dateTime":"2026-09-27T11:30:00.000+08:00"}}"#
        let e = try JSONDecoder().decode(GoogleAPI.Event.self, from: Data(json.utf8))
        let item = try XCTUnwrap(GoogleAPI.mapEvent(e, cal))
        XCTAssertEqual(item.end!.timeIntervalSince(item.start!), 90 * 60)
    }

    func testCallbackScheme() {
        XCTAssertEqual(GoogleOAuth.callbackScheme(for: "123-abc.apps.googleusercontent.com"), "com.googleusercontent.apps.123-abc")
        XCTAssertNil(GoogleOAuth.callbackScheme(for: "abc"))
        let url = GoogleOAuth.authorizationURL(clientID: "123-abc.apps.googleusercontent.com", verifier: "v", state: "s")
        XCTAssertNotNil(url)
        XCTAssertTrue(url!.absoluteString.contains("redirect_uri=com.googleusercontent.apps.123-abc:/oauth2redirect"))
    }
}

final class SyncPlannerTests: XCTestCase {
    private let now = Date()

    private func data(_ title: String, day: String? = "2026-09-28", minute: Int? = nil, done: Bool = false) -> SyncTaskData {
        SyncTaskData(title: title, notes: "", dueDay: day, minute: minute, completed: done)
    }

    private func item(_ id: String, _ d: SyncTaskData, modified: Date? = nil) -> SyncSideItem {
        SyncSideItem(id: id, altID: nil, data: d, modified: modified, completedAt: d.completed ? now : nil)
    }

    func testNewItemsOnBothSides() {
        let plan = SyncPlanner.plan(records: [], reminders: [item("r1", data("买菜"))], tasks: [item("t1", data("交报告"))], now: now)
        XCTAssertTrue(plan.actions.contains(.createTask(reminderID: "r1", data: data("买菜"))))
        XCTAssertTrue(plan.actions.contains(.createReminder(taskID: "t1", data: data("交报告"))))
    }

    func testFirstPairingMatchesSameTitle() {
        let plan = SyncPlanner.plan(records: [], reminders: [item("r1", data("买菜"))], tasks: [item("t1", data("买菜"))], now: now)
        XCTAssertEqual(plan.actions, [.link(reminderID: "r1", taskID: "t1", data: data("买菜"))])
    }

    func testChangeOnOneSide() {
        let old = data("买菜")
        let rec = SyncRecord(reminderID: "r1", reminderAltID: nil, taskID: "t1", fingerprint: old.fingerprint)
        let doneOnPhone = SyncPlanner.plan(records: [rec], reminders: [item("r1", data("买菜", done: true))], tasks: [item("t1", old)], now: now)
        XCTAssertEqual(doneOnPhone.actions, [.updateTask(reminderID: "r1", taskID: "t1", data: data("买菜", done: true))])

        let renamedOnPC = SyncPlanner.plan(records: [rec], reminders: [item("r1", old)], tasks: [item("t1", data("买菜和水果"))], now: now)
        XCTAssertEqual(renamedOnPC.actions, [.updateReminder(reminderID: "r1", taskID: "t1", data: data("买菜和水果"))])
    }

    func testDeletions() {
        let old = data("买菜")
        let rec = SyncRecord(reminderID: "r1", reminderAltID: nil, taskID: "t1", fingerprint: old.fingerprint)
        let deletedOnPC = SyncPlanner.plan(records: [rec], reminders: [item("r1", old)], tasks: [], now: now)
        XCTAssertEqual(deletedOnPC.actions, [.deleteReminder(reminderID: "r1")])
        let deletedOnPhone = SyncPlanner.plan(records: [rec], reminders: [], tasks: [item("t1", old)], now: now)
        XCTAssertEqual(deletedOnPhone.actions, [.deleteTask(taskID: "t1")])
    }

    func testMassDeletionIsPaused() {
        var records: [SyncRecord] = []
        var tasks: [SyncSideItem] = []
        for i in 0..<8 {
            let d = data("事情\(i)")
            records.append(SyncRecord(reminderID: "r\(i)", reminderAltID: nil, taskID: "t\(i)", fingerprint: d.fingerprint))
            tasks.append(item("t\(i)", d))
        }
        let plan = SyncPlanner.plan(records: records, reminders: [], tasks: tasks, now: now)
        XCTAssertTrue(plan.deletionsPaused)
        XCTAssertEqual(plan.heldRecords.count, 8)
        XCTAssertFalse(plan.actions.contains { if case .deleteTask = $0 { return true } else { return false } })
    }

    func testBothChangedNewerWins() {
        let old = data("买菜")
        let rec = SyncRecord(reminderID: "r1", reminderAltID: nil, taskID: "t1", fingerprint: old.fingerprint)
        let plan = SyncPlanner.plan(records: [rec],
                                    reminders: [item("r1", data("买菜A"), modified: now)],
                                    tasks: [item("t1", data("买菜B"), modified: now.addingTimeInterval(-60))],
                                    now: now)
        XCTAssertEqual(plan.actions, [.updateTask(reminderID: "r1", taskID: "t1", data: data("买菜A"))])
    }

    func testTimeRoundTripThroughGoogle() throws {
        let original = data("交报告", day: "2026-09-28", minute: 15 * 60)
        let fields = original.googleFields
        XCTAssertEqual(fields["title"] as? String, "15:00 交报告")
        XCTAssertEqual(fields["due"] as? String, "2026-09-28T00:00:00.000Z")
        let json = #"{"id":"t1","title":"15:00 交报告","status":"needsAction","due":"2026-09-28T00:00:00.000Z"}"#
        let task = try JSONDecoder().decode(GoogleAPI.TaskItem.self, from: Data(json.utf8))
        XCTAssertEqual(SyncTaskData(task: task).fingerprint, original.fingerprint)
    }

    func testOldCompletedRemindersAreNotUploaded() {
        var r = item("r1", data("很久以前的事", done: true))
        r.completedAt = now.addingTimeInterval(-30 * 86400)
        let plan = SyncPlanner.plan(records: [], reminders: [r], tasks: [], now: now)
        XCTAssertTrue(plan.actions.isEmpty)
    }
}

final class AgendaTests: XCTestCase {
    func testGrouping() {
        let cal = DateText.calendar
        let today = cal.date(from: DateComponents(year: 2026, month: 9, day: 27))!
        func task(_ id: String, _ due: Date?, time: Bool = false, done: Bool = false) -> AgendaItem {
            AgendaItem(id: id, kind: .task, source: .ekReminder, nativeID: id, title: id, start: due, isAllDay: !time,
                       isCompleted: done, completedAt: done ? today.addingTimeInterval(3600) : nil,
                       containerID: "c", containerName: "c", colorHex: "#000000")
        }
        let items = [
            task("过期", cal.date(byAdding: .day, value: -2, to: today)),
            task("今天", today),
            task("带时间", today.addingTimeInterval(15 * 3600), time: true),
            task("无日期", nil),
            task("完成了", today, done: true),
        ]
        let a = AgendaBuilder.build(items, day: today, carryOver: true)
        XCTAssertEqual(a.overdue.map { $0.title }, ["过期"])
        XCTAssertEqual(a.dayTasks.map { $0.title }, ["今天"])
        XCTAssertEqual(a.scheduled.map { $0.title }, ["带时间"])
        XCTAssertEqual(a.undated.map { $0.title }, ["无日期"])
        XCTAssertEqual(a.completed.map { $0.title }, ["完成了"])
        XCTAssertEqual(a.openTaskCount, 2)
    }

    func testDateText() {
        let cal = DateText.calendar
        let today = cal.date(from: DateComponents(year: 2026, month: 9, day: 27))!
        XCTAssertEqual(DateText.weekday(today), "星期日")
        XCTAssertEqual(DateText.monthDay(today), "9月27日")
        XCTAssertEqual(DateText.relative(cal.date(byAdding: .day, value: 1, to: today)!, today: today), "明天")
        XCTAssertEqual(DateText.dayHeader(cal.date(byAdding: .day, value: 1, to: today)!, today: today), "明天 · 9月28日 周一")
    }
}
