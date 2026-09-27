import AppIntents
import Foundation

/// 小组件里直接打勾（iOS 17 交互式小组件）
struct ToggleItemIntent: AppIntent {
    static var title: LocalizedStringResource = "完成待办"
    static var isDiscoverable: Bool = false

    @Parameter(title: "条目")
    var itemID: String

    @Parameter(title: "完成")
    var completed: Bool

    init() {}

    init(itemID: String, completed: Bool) {
        self.itemID = itemID
        self.completed = completed
    }

    func perform() async throws -> some IntentResult {
        try await ItemActions.setCompleted(itemID: itemID, completed: completed)
        return .result()
    }
}

enum ItemActions {
    /// itemID 形如 ekr|提醒ID 或 gt|清单ID|任务ID
    static func setCompleted(itemID: String, completed: Bool) async throws {
        let parts = itemID.components(separatedBy: "|")
        guard parts.count >= 2 else { return }
        switch parts[0] {
        case "ekr":
            try EventKitSource.shared.setReminderCompleted(id: parts[1], completed: completed)
        case "gt":
            guard parts.count >= 3 else { return }
            try await GoogleAPI.setTaskCompleted(listID: parts[1], taskID: parts[2], completed: completed)
        default:
            return
        }
        SharedStore.markCompleted(itemID: itemID, completed: completed)
    }
}
