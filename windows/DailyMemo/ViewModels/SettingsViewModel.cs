using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Input;
using DailyMemo.Core;
using DailyMemo.Services;
using Microsoft.Win32;
using Wpf.Ui.Controls;

namespace DailyMemo.ViewModels;

public sealed record TargetOption(string Key, string Name);

public sealed class ContainerToggleViewModel : ObservableObject
{
    private bool _isVisible;

    public ContainerToggleViewModel(SourceContainer container, bool visible)
    {
        Container = container;
        _isVisible = visible;
    }

    public SourceContainer Container { get; }
    public string Name => Container.Name;
    public string SourceLabel => Container.SourceLabel;
    public System.Windows.Media.Brush ColorBrush => ItemViewModel.BrushFor(Container.Color);

    public bool IsVisible
    {
        get => _isVisible;
        set
        {
            if (Set(ref _isVisible, value)) AppHost.Current.SetContainerVisible(Container, value);
        }
    }
}

public sealed class SettingsViewModel : PageViewModel
{
    private readonly AppHost _host;
    private string _googleStatus = "";
    private string _icloudStatus = "";
    private string _syncDetails = "";
    private bool _busy;

    public SettingsViewModel(AppHost host) : base("settings", "设置", SymbolRegular.Settings24)
    {
        _host = host;
        SignInCommand = new AsyncCommand(SignInAsync, () => !_busy);
        SignOutCommand = new AsyncCommand(SignOutAsync);
        ImportClientJsonCommand = new RelayCommand(ImportClientJson);
        OpenGoogleConsoleCommand = new RelayCommand(() => Open("https://console.cloud.google.com/apis/credentials"));
        ConnectICloudCommand = new AsyncCommand(ConnectICloudAsync);
        DisconnectICloudCommand = new RelayCommand(DisconnectICloud);
        OpenAppleIdCommand = new RelayCommand(() => Open("https://account.apple.com/account/manage"));
        ResetWidgetCommand = new RelayCommand(() => _host.ResetWidgetPosition());
        TestNotificationCommand = new RelayCommand(() => _host.Notifications.ShowTest());
        ShowSummaryNowCommand = new RelayCommand(() => _host.Notifications.ShowDailySummary());
        OpenDataFolderCommand = new RelayCommand(() => Open(AppPaths.DataDir));
        RefreshCommand = new AsyncCommand(() => _host.Agenda.RefreshAsync());
        MigrateLocalCommand = new AsyncCommand(MigrateLocalAsync, () => _host.Agenda.GoogleConnected && _host.Agenda.LocalOpenCount > 0);
    }

    private AppSettings S => _host.Settings.Current;

    private void Update(Action<AppSettings> change, Action? after = null, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        change(S);
        _host.Settings.Save();
        OnPropertyChanged(name);
        after?.Invoke();
    }

    // ───── Google ─────

    public string GoogleClientId { get => S.GoogleClientId; set => Update(s => s.GoogleClientId = value.Trim()); }
    public string GoogleClientSecret { get => S.GoogleClientSecret; set => Update(s => s.GoogleClientSecret = value.Trim()); }
    public bool IsGoogleSignedIn => _host.GoogleAuth.IsSignedIn;
    public bool IsGoogleSignedOut => !_host.GoogleAuth.IsSignedIn;
    public string GoogleStatus { get => _googleStatus; private set => Set(ref _googleStatus, value); }
    public bool Busy { get => _busy; private set => Set(ref _busy, value); }

    public bool GoogleCalendarEnabled
    {
        get => S.GoogleCalendarEnabled;
        set => Update(s => s.GoogleCalendarEnabled = value, () => _ = _host.Agenda.RefreshAsync());
    }

    public bool GoogleTasksEnabled
    {
        get => S.GoogleTasksEnabled;
        set => Update(s => s.GoogleTasksEnabled = value, () => _ = _host.Agenda.RefreshAsync());
    }

    public string LocalTasksHint => _host.Agenda.LocalOpenCount > 0
        ? $"本机还有 {_host.Agenda.LocalOpenCount} 项未同步的待办，可以一键搬到 Google Tasks。"
        : "";

    public ICommand SignInCommand { get; }
    public ICommand SignOutCommand { get; }
    public ICommand ImportClientJsonCommand { get; }
    public ICommand OpenGoogleConsoleCommand { get; }
    public ICommand MigrateLocalCommand { get; }

    private async Task SignInAsync()
    {
        Busy = true;
        GoogleStatus = "已打开浏览器，请在浏览器里登录并允许访问…";
        try
        {
            await _host.GoogleAuth.SignInAsync(default);
            _host.ShowMainWindow("settings");
            await _host.Agenda.RefreshAsync();
            // 登录后新建的待办默认存到 Google，这样手机上也能看到
            if (S.DefaultTaskTarget == ContainerKeys.Local)
            {
                var first = _host.Agenda.TaskContainers.FirstOrDefault(c => c.Source == ItemSource.Google);
                if (first != null) Update(s => s.DefaultTaskTarget = first.Key, null, nameof(SelectedTaskTarget));
            }
            if (_host.Agenda.LocalOpenCount > 0 &&
                await _host.ConfirmAsync($"本机有 {_host.Agenda.LocalOpenCount} 项待办还没同步，要搬到 Google Tasks 吗？\n搬过去之后手机上也能看到。", "搬到 Google", null))
                await MigrateLocalAsync();
        }
        finally
        {
            Busy = false;
            Rebuild();
        }
    }

    private async Task SignOutAsync()
    {
        if (!await _host.ConfirmAsync("退出 Google 账号后，这台电脑将不再显示 Google 日历和待办（云端数据不受影响）。", "退出登录", null)) return;
        await _host.GoogleAuth.SignOutAsync();
        Rebuild();
    }

    private async Task MigrateLocalAsync()
    {
        var target = _host.Agenda.ResolveTaskTarget();
        if (target == ContainerKeys.Local)
            target = _host.Agenda.TaskContainers.First(c => c.Source == ItemSource.Google).Key;
        int n = await _host.Agenda.MigrateLocalTasksAsync(target);
        _host.Toast($"已把 {n} 项待办搬到 Google Tasks");
        Rebuild();
    }

    private void ImportClientJson()
    {
        var dlg = new OpenFileDialog { Filter = "Google OAuth 客户端 (*.json)|*.json", Title = "选择从 Google Cloud 下载的 client_secret_xxx.json" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(dlg.FileName));
            var root = doc.RootElement;
            var node = root.TryGetProperty("installed", out var inst) ? inst : root.TryGetProperty("web", out var web) ? web : root;
            var id = node.GetProperty("client_id").GetString() ?? "";
            var secret = node.TryGetProperty("client_secret", out var sec) ? sec.GetString() ?? "" : "";
            GoogleClientId = id;
            GoogleClientSecret = secret;
            if (!root.TryGetProperty("installed", out _))
                _host.ShowError("导入成功，但这不是「桌面应用」类型的客户端，登录可能失败。请按教程创建「桌面应用」类型。");
        }
        catch (Exception ex)
        {
            _host.ShowError("读取 JSON 失败：" + ex.Message);
        }
    }

    // ───── iCloud ─────

    public string ICloudAppleId { get => S.ICloudAppleId; set => Update(s => s.ICloudAppleId = value.Trim()); }
    public bool IsICloudConnected => _host.Agenda.ICloudConnected;
    public bool IsICloudDisconnected => !_host.Agenda.ICloudConnected;
    public string ICloudStatus { get => _icloudStatus; private set => Set(ref _icloudStatus, value); }

    public ICommand ConnectICloudCommand { get; }
    public ICommand DisconnectICloudCommand { get; }
    public ICommand OpenAppleIdCommand { get; }

    private async Task ConnectICloudAsync(object? passwordBox)
    {
        var pwd = passwordBox switch
        {
            Wpf.Ui.Controls.PasswordBox ui => ui.Password,
            System.Windows.Controls.PasswordBox box => box.Password,
            _ => "",
        } ?? "";
        if (string.IsNullOrWhiteSpace(S.ICloudAppleId) || string.IsNullOrWhiteSpace(pwd))
        {
            _host.ShowError("请填写 Apple ID 和 App 专用密码。");
            return;
        }
        ICloudStatus = "正在连接 iCloud…";
        try
        {
            int n = await _host.Agenda.ConnectICloudAsync(S.ICloudAppleId, pwd);
            if (passwordBox is Wpf.Ui.Controls.PasswordBox ui2) ui2.Password = "";
            else if (passwordBox is System.Windows.Controls.PasswordBox box2) box2.Password = "";
            _host.Toast($"已连接 iCloud，找到 {n} 个日历");
        }
        finally
        {
            Rebuild();
        }
    }

    private void DisconnectICloud()
    {
        _host.Agenda.DisconnectICloud();
        Rebuild();
    }

    // ───── 显示内容 & 默认位置 ─────

    public ObservableCollection<ContainerToggleViewModel> Containers { get; } = new();
    public ObservableCollection<TargetOption> TaskTargets { get; } = new();
    public ObservableCollection<TargetOption> EventTargets { get; } = new();

    public TargetOption? SelectedTaskTarget
    {
        get => TaskTargets.FirstOrDefault(t => t.Key == _host.Agenda.ResolveTaskTarget());
        set { if (value != null) Update(s => s.DefaultTaskTarget = value.Key); }
    }

    public TargetOption? SelectedEventTarget
    {
        get
        {
            var key = _host.Agenda.ResolveEventTarget();
            return EventTargets.FirstOrDefault(t => t.Key == key);
        }
        set { if (value != null) Update(s => s.DefaultEventTarget = value.Key); }
    }

    public bool ShowUndatedInToday { get => S.ShowUndatedInToday; set => Update(s => s.ShowUndatedInToday = value, _host.RebuildAll); }

    // ───── 桌面小组件 ─────

    public bool WidgetVisible { get => S.WidgetVisible; set { _host.SetWidgetVisible(value); OnPropertyChanged(); } }

    public int WidgetModeIndex
    {
        get => (int)S.WidgetMode;
        set => Update(s => s.WidgetMode = (WidgetMode)value, _host.ApplyWidgetMode);
    }

    public double WidgetOpacity
    {
        get => S.WidgetOpacity;
        set => Update(s => s.WidgetOpacity = Math.Round(value, 2), () => ThemeService.UpdateWidgetBackground(S.WidgetOpacity));
    }

    public bool WidgetShowCompleted { get => S.WidgetShowCompleted; set => Update(s => s.WidgetShowCompleted = value, _host.RebuildAll); }
    public bool WidgetShowUndated { get => S.WidgetShowUndated; set => Update(s => s.WidgetShowUndated = value, _host.RebuildAll); }
    public bool WidgetLocked { get => S.WidgetLocked; set => Update(s => s.WidgetLocked = value, _host.ApplyWidgetMode); }

    public ICommand ResetWidgetCommand { get; }

    // ───── 提醒 ─────

    public bool DailySummaryEnabled { get => S.DailySummaryEnabled; set => Update(s => s.DailySummaryEnabled = value); }

    public string DailySummaryTime
    {
        get => S.DailySummaryTime;
        set
        {
            if (TimeSpan.TryParse(value, out var t) && t >= TimeSpan.Zero && t < TimeSpan.FromDays(1))
                Update(s => s.DailySummaryTime = $"{t.Hours:00}:{t.Minutes:00}");
            else OnPropertyChanged();
        }
    }

    public IReadOnlyList<string> SummaryTimeOptions { get; } =
        Enumerable.Range(12, 12).Select(i => $"{i / 2:00}:{(i % 2) * 30:00}").ToList();

    public bool EventReminderEnabled { get => S.EventReminderEnabled; set => Update(s => s.EventReminderEnabled = value); }

    public IReadOnlyList<int> ReminderOptions { get; } = new[] { 0, 5, 10, 15, 30, 60 };
    public int EventReminderMinutes { get => S.EventReminderMinutes; set => Update(s => s.EventReminderMinutes = value); }

    public ICommand TestNotificationCommand { get; }
    public ICommand ShowSummaryNowCommand { get; }

    // ───── 常规 ─────

    public bool LaunchAtStartup { get => S.LaunchAtStartup; set { _host.SetLaunchAtStartup(value); OnPropertyChanged(); } }

    public int ThemeIndex
    {
        get => S.Theme switch { "light" => 1, "dark" => 2, _ => 0 };
        set => Update(s => s.Theme = value switch { 1 => "light", 2 => "dark", _ => "system" }, () => ThemeService.Apply(S.Theme));
    }

    public IReadOnlyList<int> RefreshOptions { get; } = new[] { 2, 5, 10, 15, 30 };
    public int RefreshMinutes { get => S.RefreshMinutes; set => Update(s => s.RefreshMinutes = value, _host.RestartRefreshTimer); }

    public string ProxyAddress
    {
        get => S.ProxyAddress;
        set => Update(s => s.ProxyAddress = value.Trim(), () => { _host.ResetHttp(); _ = _host.Agenda.RefreshAsync(); });
    }

    public string SyncDetails { get => _syncDetails; private set => Set(ref _syncDetails, value); }
    public string DataFolder => AppPaths.DataDir;
    public string Version => "今日事 " + (typeof(SettingsViewModel).Assembly.GetName().Version?.ToString(3) ?? "1.0.0");

    public ICommand OpenDataFolderCommand { get; }
    public ICommand RefreshCommand { get; }

    public override void Rebuild()
    {
        var agenda = _host.Agenda;
        GoogleStatus = _host.GoogleAuth.IsSignedIn
            ? $"已连接：{_host.GoogleAuth.Email ?? "Google 账号"}"
            : string.IsNullOrWhiteSpace(S.GoogleClientId) ? "未配置：先填写客户端 ID / 密钥，或导入 JSON" : "未登录";
        ICloudStatus = agenda.ICloudConnected ? $"已连接：{S.ICloudAppleId}" : "未连接";

        Containers.Clear();
        foreach (var c in agenda.Containers)
            Containers.Add(new ContainerToggleViewModel(c, agenda.IsVisible(c)));

        TaskTargets.Clear();
        foreach (var c in agenda.TaskContainers) TaskTargets.Add(new TargetOption(c.Key, c.DisplayName));
        EventTargets.Clear();
        foreach (var c in agenda.EventContainers) EventTargets.Add(new TargetOption(c.Key, c.DisplayName));

        var lines = agenda.SourceStatuses.Select(s =>
            s.Error != null ? $"⚠ {s.Name}：{s.Error}" : $"✓ {s.Name}：{(s.LastSuccess.HasValue ? $"{s.LastSuccess:HH:mm} 已同步" : "等待同步")}").ToList();
        SyncDetails = lines.Count == 0 ? "还没有连接任何账号，目前只使用本机待办。" : string.Join("\n", lines);

        foreach (var p in new[]
                 {
                     nameof(IsGoogleSignedIn), nameof(IsGoogleSignedOut), nameof(IsICloudConnected), nameof(IsICloudDisconnected),
                     nameof(SelectedTaskTarget), nameof(SelectedEventTarget), nameof(LocalTasksHint), nameof(WidgetVisible),
                     nameof(LaunchAtStartup), nameof(GoogleClientId), nameof(GoogleClientSecret),
                 })
            OnPropertyChanged(p);
    }

    private static void Open(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); } catch { }
    }
}
