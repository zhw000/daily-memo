using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using DailyMemo.Core;
using DailyMemo.Services;

namespace DailyMemo;

public partial class App : Application
{
    private Mutex? _mutex;
    private EventWaitHandle? _showSignal;
    private AppHost? _host;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var args = e.Args;

        int previewIdx = Array.IndexOf(args, "--preview");
        if (previewIdx >= 0)
        {
            var dir = args.Length > previewIdx + 1 ? args[previewIdx + 1] : "preview";
            Preview.Run(dir);
            return;
        }

        // 只允许运行一个实例；再次打开时让已运行的实例显示主界面
        string suffix = StartupManager.IsDevMode ? ".dev" : "";
        _mutex = new Mutex(true, "DailyMemo.SingleInstance" + suffix, out bool first);
        if (!first)
        {
            try
            {
                using var ev = EventWaitHandle.OpenExisting("DailyMemo.Show" + suffix);
                ev.Set();
            }
            catch
            {
            }
            Shutdown();
            return;
        }
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, "DailyMemo.Show" + suffix);
        ThreadPool.RegisterWaitForSingleObject(_showSignal, (_, _) => Dispatcher.BeginInvoke(() => _host?.ShowMainWindow()), null, -1, false);

        DispatcherUnhandledException += (_, ex) =>
        {
            Log.Error("未处理的异常", ex.Exception);
            ex.Handled = true;
            _host?.ShowError(ex.Exception);
        };
        TaskScheduler.UnobservedTaskException += (_, ex) =>
        {
            Log.Error("后台任务异常", ex.Exception);
            ex.SetObserved();
        };

        bool minimized = args.Contains("--minimized");
        Log.Info("启动 " + string.Join(' ', args));
        _host = new AppHost(preview: false);
        _host.Start(minimized);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _mutex?.Dispose();
        _showSignal?.Dispose();
        base.OnExit(e);
    }
}
