# 在电脑上显示 iPhone「日历」里的 iCloud 日历（可选）

如果你在 iPhone 自带的「日历」里建日程，这些日程默认存在 iCloud。电脑版今日事可以通过 iCloud 的日历接口（CalDAV）直接读取，也能在这些日历里新建日程。

> 不需要这一步的情况：你的日程都建在 Google 日历里（或者 iPhone「日历」的默认日历就是 Google 账号）。

## 1. 生成 App 专用密码

1. 打开 <https://account.apple.com>（国区账号也可以用 <https://appleid.apple.com>），登录 Apple ID。
2. **登录与安全 → App 专用密码 → 生成**，名称填 `今日事`。
3. 得到一串 `xxxx-xxxx-xxxx-xxxx` 的密码，复制下来。

> 这个密码只能用来访问日历/通讯录等数据，不能登录你的 Apple ID，随时可以在同一页面吊销。

## 2. 在电脑版里连接

**设置 → iCloud 日历**：Apple ID 填你的账号（邮箱或手机号），密码处粘贴刚才的 App 专用密码，点 **连接**。连接成功后会提示找到几个日历，「设置 → 显示哪些日历和清单」里可以选择显示哪些。

- 国区（云上贵州）的 Apple ID 会自动连接到 `caldav.icloud.com.cn`。
- 密码用 Windows DPAPI 加密保存在本机。

## 限制

- **提醒事项不能用这种方式读取**：iOS 13 以后的新版提醒事项不再提供 CalDAV 接口。请在 iPhone 版开启「提醒事项 ↔ Google Tasks」同步，电脑上就能看到提醒事项了。
- iCloud 日程在电脑上可以新建、删除（非重复日程），修改请在 iPhone 上进行。
