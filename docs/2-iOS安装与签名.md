# iPhone 版：获取 IPA、签名安装、添加小组件

## 获取未签名 IPA

- **现成的**：`dist\ios\DailyMemo-unsigned.ipa`（最近一次编译的结果）。
- **重新编译**：代码在私有仓库 <https://github.com/zhw000/daily-memo> ，每次推送 `ios/` 目录的改动都会在 GitHub Actions 的 macOS 机器上自动编译；也可以打开仓库的 **Actions → iOS → Run workflow** 手动编译。完成后在运行记录底部的 **Artifacts** 下载 `DailyMemo-unsigned-ipa`（zip 里就是 IPA）。

同一次编译还会产出 `screenshots`（各界面的模拟器截图）和 `logs`（编译日志）。

## 签名安装

IPA 里有两部分：App 本体（`com.dailymemo.app`）和小组件扩展（`com.dailymemo.app.widget`），两者通过 App Group `group.com.dailymemo.app` 共享数据。**签名时尽量保留 App Group**，否则小组件只能显示 iPhone 日历和提醒事项，看不到 Google 的内容（App 本身不受影响）。App 在「设置 → 小组件 → 小组件数据共享」里会显示是否正常。

| 方式 | 说明 |
|---|---|
| **Sideloadly**（Windows） | 装好 Apple 官网版的 iTunes 和 iCloud → 数据线连接 iPhone → 把 IPA 拖进去 → 填 Apple ID → Start。免费 Apple ID 签名 7 天有效，需要每周重签（可开启 Wi-Fi 自动续签）；App 和小组件会占用 2 个 App ID 名额。 |
| **AltStore / SideStore** | 会自动注册并改写 App Group，小组件数据共享正常。同样 7 天续签。 |
| **付费开发者证书**（p12 + 描述文件，用 ESign / Feather / 爱思助手等） | 描述文件要用**明确的 App ID 并勾选 App Groups**；通配符（`*`）描述文件不支持 App Group。 |
| **TrollStore**（支持的系统版本） | 直接安装，永久有效，App Group 正常。 |

App 会在运行时自动识别签名工具改过的 App Group 名字（AltStore 写入的 `ALTAppGroups`、描述文件里的 App Group），一般不需要手动处理。

安装后第一次打开前：

1. iOS 16 及以上需要打开 **设置 → 隐私与安全性 → 开发者模式**（重启后确认）。
2. 用 Apple ID 签名的，需要在 **设置 → 通用 → VPN 与设备管理** 里信任对应的开发者。

> 签名工具如果修改了 Bundle ID，创建 Google「iOS 客户端」时填改后的 ID（App 的「设置 → Google 账号 → iOS 客户端 ID」页面会显示当前 Bundle ID）。

## 第一次使用

1. 打开今日事，点 **允许访问并开始**，依次允许 **日历**、**提醒事项**、**通知**（选「允许完全访问」）。
2. **设置 → Google 账号 → iOS 客户端 ID**：粘贴按 [Google 配置教程](1-Google配置教程.md) 第 5 步得到的客户端 ID。
3. 返回点 **登录 Google**，在弹出的页面登录并允许全部权限。
4. 登录后会自动把「提醒事项」的默认列表和 Google Tasks 同步。想同步更多列表：**设置 → 同步 → 提醒事项 ↔ Google Tasks**。

## 添加小组件

- **主屏幕**：长按空白处 → 左上角「编辑」→「添加小组件」→ 搜索「今日事」→ 选择小/中/大尺寸。中、大尺寸里待办前面的圆圈可以直接点，完成后划掉。
- **锁屏**：长按锁屏 →「自定」→ 锁定屏幕 → 点小组件区域 → 选「今日事」（圆形显示剩余待办数，长方形显示接下来两件事）。
- App 里 **设置 → 小组件 → 小组件预览** 可以先看看各个尺寸的样子。

小组件会在每个日程开始/结束时自动更新，并且至少每 30 分钟重新读取一次数据。

## 常见问题

| 现象 | 解决 |
|---|---|
| Google 登录页打不开 / 同步报网络错误 | iPhone 需要打开网络代理才能访问 Google |
| 小组件看不到 Google 的日程 | 「设置 → 小组件数据共享」显示不可用 → 换保留 App Group 的签名方式；或者把 Google 账号添加到 iPhone「设置 → 日历 → 账户」，日程会以 iPhone 日历的形式显示 |
| 同一个日程显示两次 | 已自动合并「iPhone 日历里的 Google 账号」和「今日事登录的 Google」重复的日程；如果仍重复，在「设置 → 显示内容」里关掉其中一边的这个日历 |
| 提醒事项同步提示「检测到大量删除，已暂停」 | 为防止误删的保护：一次要删除一半以上的内容时会暂停。确认没问题的话，在同步设置里把这个列表关掉再打开 |
