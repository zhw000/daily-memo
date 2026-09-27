using System;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using DailyMemo.Core;
using DailyMemo.Services;
using DailyMemo.ViewModels;
using DailyMemo.Views;

namespace DailyMemo;

/// <summary>应用的「总控」：持有各个服务、窗口和托盘，界面上的操作都经过这里。</summary>
public sealed class AppHost
{
    private readonly HttpClient _caldavHttp;
    private readonly DispatcherTimer _refreshTimer = new();
    private readonly DispatcherTimer _clockTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private HttpClient _googleHttp;
    private MainWindow? _main;
    private WidgetWindow? _widget;
    private TrayIcon? _tray;
    private DateTime _today = DateTime.Today;
    private DateTime _lastRebuild = DateTime.Now;

    public AppHost(bool preview)
    {
        Current = this;
        IsPreview = preview;
        Settings = new SettingsService();
        _googleHttp = CreateGoogleHttp();
        _caldavHttp = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(40) };
        GoogleAuth = new GoogleAuth(() => _googleHttp, () => Settings.Current);
        Google = new GoogleApi(() => _googleHttp, GoogleAuth);
        CalDav = new CalDavClient(_caldavHttp);
        Agenda = new AgendaService(Settings, GoogleAuth, Google, CalDav, new LocalTaskStore(), demo: preview);
        Notifications = new NotificationService(Settings, Agenda);
        WidgetVm = new WidgetViewModel(this);
        MainVm = new MainViewModel(this);
        Agenda.Changed += (_, _) => RebuildAll();
    }

    public static AppHost Current { get; private set; } = null!;

    public bool IsPreview { get; }
    public SettingsService Settings { get; }
    public GoogleAuth GoogleAuth { get; }
    public GoogleApi Google { get; }
    public CalDavClient CalDav { get; }
    public AgendaService Agenda { get; }
    public NotificationService Notifications { get; }
    public MainViewModel MainVm { get; }
    public WidgetViewModel WidgetVm { get; }
    public Window? WidgetWindow => _widget;
    public MainWindow? MainWindow => _main;

    public void Start(bool minimized)
    {
        ThemeService.Apply(Settings.Current.Theme);
        ThemeService.UpdateWidgetBackground(Settings.Current.WidgetOpacity);
        _tray = new TrayIcon(this);

        if (Settings.Current.WidgetVisible) ShowWidget();
        if (!minimized) ShowMainWindow();

        // 每次启动都刷新一下开机启动项（程序被移动过位置也能继续自启）
        if (!Settings.Current.FirstRunDone)
        {
            Settings.Current.FirstRunDone = true;
            Settings.Save();
        }
        StartupManager.Apply(Settings.Current.LaunchAtStartup);

        Notifications.Activated += _ => ShowMainWindow();
        Notifications.Start();

        RestartRefreshTimer();
        _clockTimer.Tick += (_, _) => OnClock();
        _clockTimer.Start();

        RebuildAll();
        _ = Agenda.RefreshAsync();
    }

    // ───────────── 窗口 ─────────────

    public void ShowMainWindow(string? page = null)
    {
        if (_main == null)
        {
            _main = new MainWindow(MainVm);
            _main.Closed += (_, _) => _main = null;
        }
        if (page != null) MainVm.Navigate(page);
        _main.Show();
        if (_main.WindowState == WindowState.Minimized) _main.WindowState = WindowState.Normal;
        _main.Activate();
        if (Agenda.LastRefresh == null || DateTime.Now - Agenda.LastRefresh > TimeSpan.FromMinutes(1))
            _ = Agenda.RefreshAsync();
    }

    public void ShowWidget()
    {
        if (_widget == null)
        {
            _widget = new WidgetWindow(WidgetVm);
            _widget.Closed += (_, _) => _widget = null;
        }
        _widget.ApplySettings(Settings.Current);
        _widget.Show();
    }

    public void SetWidgetVisible(bool visible)
    {
        Settings.Current.WidgetVisible = visible;
        Settings.Save();
        if (visible) ShowWidget();
        else _widget?.Close();
        MainVm.Settings.Rebuild();
    }

    public void ToggleWidget() => SetWidgetVisible(!(Settings.Current.WidgetVisible && _widget != null));

    public void ApplyWidgetMode()
    {
        _widget?.ApplySettings(Settings.Current);
        WidgetVm.Rebuild();
    }

    public void ResetWidgetPosition()
    {
        Settings.Current.WidgetLeft = double.NaN;
        Settings.Current.WidgetTop = double.NaN;
        Settings.Current.WidgetWidth = 340;
        Settings.Current.WidgetHeight = 460;
        Settings.Save();
        if (_widget != null) _widget.ApplySettings(Settings.Current);
        else SetWidgetVisible(true);
    }

    public void OpenEditor(AgendaItem? item, ItemKind kind, DateTime? date, Window? owner)
    {
        var vm = new EditorViewModel(this, item, kind, date);
        var win = new EditorWindow(vm);
        owner ??= _main is { IsVisible: true } ? _main : null;
        if (owner != null && owner.IsVisible)
        {
            win.Owner = owner;
            win.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else
        {
            win.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
        win.Show();
        win.Activate();
    }

    public void RebuildAll()
    {
        _lastRebuild = DateTime.Now;
        MainVm.Rebuild();
        WidgetVm.Rebuild();
        if (_tray != null)
        {
            var day = Agenda.Day(DateTime.Today, true);
            int open = day.OpenTaskCount + day.Overdue.Count;
            _tray.SetTooltip($"今日事 · {day.EventCount} 个日程 · {open} 项待办");
        }
    }

    // ───────────── 条目操作 ─────────────

    public Task ToggleAsync(AgendaItem item) => Agenda.ToggleCompletedAsync(item);

    public async Task DeleteAsync(AgendaItem item, Window? owner)
    {
        if (!await ConfirmAsync($"确定删除「{item.Title}」吗？", "删除", owner)) return;
        await Agenda.DeleteAsync(item);
    }

    public void OpenLink(AgendaItem item)
    {
        if (string.IsNullOrEmpty(item.Link)) return;
        try { Process.Start(new ProcessStartInfo(item.Link) { UseShellExecute = true }); } catch { }
    }

    public void SetContainerVisible(SourceContainer c, bool visible)
    {
        Settings.Current.Visibility[c.Key] = visible;
        Settings.Save();
        RebuildAll();
        if (visible && c.Kind == ItemKind.Event) _ = Agenda.RefreshAsync();
    }

    // ───────────── 提示 ─────────────

    public void ShowError(Exception ex) => ShowError(AgendaService.FriendlyError(ex));

    public void ShowError(string message)
    {
        Log.Info("提示：" + message);
        if (IsPreview) return;
        _ = ShowMessageAsync(message);
    }

    private static async Task ShowMessageAsync(string message)
    {
        var mb = new Wpf.Ui.Controls.MessageBox
        {
            Title = "今日事",
            Content = message,
            CloseButtonText = "知道了",
            Owner = ActiveWindow(),
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        if (mb.Owner == null) mb.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        try { await mb.ShowDialogAsync(); } catch { }
    }

    public async Task<bool> ConfirmAsync(string message, string primary, Window? owner)
    {
        var mb = new Wpf.Ui.Controls.MessageBox
        {
            Title = "今日事",
            Content = message,
            PrimaryButtonText = primary,
            CloseButtonText = "取消",
            Owner = owner ?? ActiveWindow(),
        };
        mb.WindowStartupLocation = mb.Owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen;
        var result = await mb.ShowDialogAsync();
        return result == Wpf.Ui.Controls.MessageBoxResult.Primary;
    }

    /// <summary>主窗口右下角的轻提示</summary>
    public void Toast(string message) => _main?.ShowToast(message);

    private static Window? ActiveWindow() =>
        Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive && w.IsVisible) is { } w && w is not Views.WidgetWindow
            ? w
            : null;

    // ───────────── 设置相关 ─────────────

    public void SetLaunchAtStartup(bool enabled)
    {
        Settings.Current.LaunchAtStartup = enabled;
        Settings.Save();
        StartupManager.Apply(enabled);
    }

    public void RestartRefreshTimer()
    {
        _refreshTimer.Stop();
        _refreshTimer.Interval = TimeSpan.FromMinutes(Math.Max(2, Settings.Current.RefreshMinutes));
        _refreshTimer.Tick -= OnRefreshTick;
        _refreshTimer.Tick += OnRefreshTick;
        if (!IsPreview) _refreshTimer.Start();
    }

    private void OnRefreshTick(object? sender, EventArgs e) => _ = Agenda.RefreshAsync();

    public void ResetHttp() => _googleHttp = CreateGoogleHttp();

    private HttpClient CreateGoogleHttp()
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(15),
        };
        var proxy = Settings.Current.ProxyAddress;
        if (!string.IsNullOrWhiteSpace(proxy))
        {
            try
            {
                handler.Proxy = new WebProxy(proxy.Contains("://") ? proxy : "http://" + proxy);
                handler.UseProxy = true;
            }
            catch (Exception ex)
            {
                Log.Error("代理地址无效", ex);
            }
        }
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(40) };
    }

    private void OnClock()
    {
        if (DateTime.Today != _today)
        {
            // 过了零点：「今天」换成新的一天
            _today = DateTime.Today;
            RebuildAll();
            _ = Agenda.RefreshAsync();
        }
        else if (DateTime.Now - _lastRebuild > TimeSpan.FromMinutes(5))
        {
            // 定时刷新「x 分钟后」和已结束日程的显示
            RebuildAll();
        }
    }

    public void Exit()
    {
        _widget?.SavePlacement();
        _tray?.Dispose();
        _tray = null;
        Application.Current.Shutdown();
    }
}
