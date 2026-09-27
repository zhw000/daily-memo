# 今日事 · 每天要做的事，一眼看清

一个给「总忘事」的人用的备忘录：**Windows 电脑**和 **iPhone** 各有完整的主界面和桌面小组件，两边通过你的 **Google 账号** 同步，并且能读写 **iPhone 自带的「日历」和「提醒事项」**。

| | Windows | iPhone |
|---|---|---|
| 主界面 | 今天 / 未来两周 / 待办清单 / 设置 | 今天 / 日程 / 清单 / 设置 |
| 小组件 | 桌面悬浮小组件（贴在桌面、可置顶、可调透明度） | 主屏幕小/中/大 + 锁屏 3 种，待办可直接打勾 |
| 提醒 | 每天第一次开机推送当天安排；日程开始前弹通知 | 每天早上推送当天安排；Google 日程开始前通知 |
| 数据来源 | Google 日历、Google Tasks、iCloud 日历、本机待办 | iPhone 日历、iPhone 提醒事项、Google 日历、Google Tasks |
| 快速记录 | 「明天下午3点 交报告」「周五 买菜」「半小时后 关火」自动识别时间 | 同左 |

## 电脑和手机怎么同步

```
 iPhone「提醒事项」 ◀──双向同步──▶ Google Tasks ◀────▶ Windows 今日事
 iPhone「日历」(iCloud) ─────────────────────────────▶ Windows 今日事（可选，App 专用密码）
 Google 日历 ◀──────────────────────────────────────▶ 两边都直接读写
```

- 两边登录**同一个 Google 账号**，Google 日历和 Google Tasks 就是共同的数据。
- iPhone 版把你选中的「提醒事项」列表和 Google Tasks 里的同名清单**双向同步**：在 iPhone 或电脑上新建、改名、打勾、删除，另一边都会跟着变。
- Google Tasks 的接口只能存日期，所以带时间的待办会把时间写在标题开头（`15:00 交报告`），两边都会自动识别；同步到 iPhone 时还原成带闹钟的提醒事项，到点 iPhone 会响。
- iPhone「日历」里的 iCloud 日历，电脑版可以在设置里用 Apple ID + App 专用密码直接连接（只读 + 可新建）。

> 在中国大陆访问 Google 需要网络代理。电脑版会自动使用系统代理，也可以在设置里填代理地址（如 `127.0.0.1:7890`）。

## 快速开始

### 1. 配置 Google（一次性，约 5 分钟）
按 [docs/1-Google配置教程.md](docs/1-Google配置教程.md) 创建一个免费的 Google Cloud 项目，拿到两个客户端 ID（电脑用「桌面应用」，手机用「iOS」）。

### 2. Windows
1. 运行 `dist\windows\DailyMemo.exe`（免安装的单文件程序，也可以放到任意文件夹）。
2. 第一次运行会：在右下角托盘出现图标、桌面右上角出现小组件、并设为开机自启（可在设置里关闭）。
3. 打开「设置 → Google 账号」，点「导入 JSON 文件…」选择下载的客户端 JSON，再点「登录 Google」。
4. （可选）「设置 → iCloud 日历」填 Apple ID 和 App 专用密码，见 [docs/3-iCloud日历.md](docs/3-iCloud日历.md)。

小组件用法：拖动顶部移动位置，拖动边缘调整大小，右键（或 `…`）切换「贴在桌面 / 总在最前 / 普通窗口」、锁定位置。关闭主窗口后程序在托盘继续运行。

### 3. iPhone
1. 按 [docs/2-iOS安装与签名.md](docs/2-iOS安装与签名.md) 下载未签名 IPA，用 Sideloadly / AltStore / 自己的证书签名安装。
2. 打开 App，允许访问日历、提醒事项和通知。
3. 「设置 → Google 账号 → iOS 客户端 ID」粘贴 iOS 客户端 ID，点「登录 Google」。登录后默认会把「提醒事项」的默认列表和 Google Tasks 同步（在「设置 → 同步」里可以选更多列表）。
4. 添加小组件：长按主屏幕空白处 → 编辑 → 添加小组件 → 搜索「今日事」；锁屏同理。

## 项目结构

```
windows/DailyMemo/        Windows 版（.NET 8 + WPF + WPF-UI）
  Services/               Google OAuth、Google 日历/Tasks、iCloud CalDAV、通知、托盘
  Views/ ViewModels/      主界面、桌面小组件、编辑窗口
windows/DailyMemo.Tests/  单元测试（日期识别、Google/iCloud 数据解析、OAuth 回调）
ios/                      iPhone 版（SwiftUI + WidgetKit + EventKit），用 XcodeGen 生成工程
  App/                    主界面、提醒事项同步、通知
  Widget/                 小组件扩展
  Shared/                 App 和小组件共用：数据模型、Google、EventKit、同步算法、小组件界面
  Tests/                  单元测试（日期识别、同步决策、数据映射）
  ci/                     GitHub Actions 用的编译/截图脚本
.github/workflows/ios.yml 在 macOS 上编译未签名 IPA
tools/IconGen/            生成图标的小工具
```

## 自己编译

**Windows**（需要 .NET 8 SDK）：

```bash
dotnet publish windows/DailyMemo -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o dist/windows
```

运行测试：`dotnet test windows/DailyMemo.Tests`

**iPhone**：推送到 GitHub 后 Actions 自动编译（也可以在 Actions 页面点「Run workflow」），编译好的 IPA 发布在仓库 Releases 的「ios-latest」里：`gh release download ios-latest --repo zhw000/daily-memo`。有 Mac 的话：`brew install xcodegen && cd ios && xcodegen generate && open DailyMemo.xcodeproj`。

## 数据和隐私

- 所有数据只在你的设备、你的 Google 账号和 iCloud 之间传输，没有任何第三方服务器。
- Windows：设置和缓存在 `%APPDATA%\DailyMemo`，Google 令牌和 iCloud 密码用 Windows DPAPI 加密保存。
- iPhone：令牌保存在 App 的共享容器里（受系统文件保护）。
