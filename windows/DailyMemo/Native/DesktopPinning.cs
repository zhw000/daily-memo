using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using DailyMemo.Core;

namespace DailyMemo.Native;

/// <summary>
/// 让小组件像桌面挂件一样工作：
/// · 不出现在任务栏和 Alt+Tab；
/// ·「贴在桌面」模式下始终在其他窗口下面，按 Win+D 或点击桌面时浮到最上面；
/// · 边缘可拖动调整大小（未锁定时）。
/// </summary>
public sealed class DesktopPinning : IDisposable
{
    private readonly Window _window;
    private IntPtr _hwnd;
    private HwndSource? _source;
    private Win32.WinEventProc? _proc;
    private IntPtr _hook;
    private WidgetMode _mode = WidgetMode.Desktop;
    private bool _overDesktop;

    public DesktopPinning(Window window)
    {
        _window = window;
        _window.SourceInitialized += OnSourceInitialized;
    }

    public bool ResizeEnabled { get; set; } = true;

    public WidgetMode Mode
    {
        get => _mode;
        set
        {
            _mode = value;
            Apply();
        }
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(_window).Handle;
        long ex = Win32.GetWindowLongPtr(_hwnd, Win32.GWL_EXSTYLE).ToInt64();
        ex = (ex | Win32.WS_EX_TOOLWINDOW) & ~Win32.WS_EX_APPWINDOW;
        Win32.SetWindowLongPtr(_hwnd, Win32.GWL_EXSTYLE, new IntPtr(ex));

        _source = HwndSource.FromHwnd(_hwnd);
        _source?.AddHook(WndProc);

        _proc = OnForegroundChanged;
        _hook = Win32.SetWinEventHook(Win32.EVENT_SYSTEM_FOREGROUND, Win32.EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _proc, 0, 0, Win32.WINEVENT_OUTOFCONTEXT);
        Apply();
    }

    private void Apply()
    {
        if (_hwnd == IntPtr.Zero) return;
        _overDesktop = false;
        switch (_mode)
        {
            case WidgetMode.Topmost:
                _window.Topmost = true;
                break;
            case WidgetMode.Normal:
                _window.Topmost = false;
                break;
            default:
                _window.Topmost = false;
                SendToBottom();
                break;
        }
    }

    private void SendToBottom() =>
        Win32.SetWindowPos(_hwnd, Win32.HWND_BOTTOM, 0, 0, 0, 0, Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE | Win32.SWP_NOOWNERZORDER);

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Win32.WM_WINDOWPOSCHANGING && _mode == WidgetMode.Desktop && !_overDesktop)
        {
            var wp = Marshal.PtrToStructure<Win32.WINDOWPOS>(lParam);
            if ((wp.flags & Win32.SWP_NOZORDER) == 0)
            {
                wp.hwndInsertAfter = Win32.HWND_BOTTOM;
                Marshal.StructureToPtr(wp, lParam, false);
            }
        }
        else if (msg == Win32.WM_NCHITTEST && ResizeEnabled)
        {
            int hit = HitTestEdges(lParam);
            if (hit != 0)
            {
                handled = true;
                return new IntPtr(hit);
            }
        }
        return IntPtr.Zero;
    }

    private int HitTestEdges(IntPtr lParam)
    {
        long lp = lParam.ToInt64();
        var screen = new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF));
        Point p;
        try { p = _window.PointFromScreen(screen); }
        catch { return 0; }

        const double grip = 8;
        double w = _window.ActualWidth, h = _window.ActualHeight;
        bool left = p.X < grip, right = p.X > w - grip, top = p.Y < grip, bottom = p.Y > h - grip;
        if (top && left) return Win32.HTTOPLEFT;
        if (top && right) return Win32.HTTOPRIGHT;
        if (bottom && left) return Win32.HTBOTTOMLEFT;
        if (bottom && right) return Win32.HTBOTTOMRIGHT;
        if (left) return Win32.HTLEFT;
        if (right) return Win32.HTRIGHT;
        if (top) return Win32.HTTOP;
        if (bottom) return Win32.HTBOTTOM;
        return 0;
    }

    private void OnForegroundChanged(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (_mode != WidgetMode.Desktop || hwnd == _hwnd || hwnd == IntPtr.Zero) return;
        var cls = Win32.ClassName(hwnd);
        bool desktop = cls is "WorkerW" or "Progman";
        if (desktop && !_overDesktop)
        {
            // 显示桌面（Win+D）或点击桌面：把小组件浮上来
            _overDesktop = true;
            Win32.SetWindowPos(_hwnd, Win32.HWND_TOPMOST, 0, 0, 0, 0, Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
            if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        }
        else if (!desktop && _overDesktop && !IsWidgetDialog(hwnd))
        {
            _overDesktop = false;
            Win32.SetWindowPos(_hwnd, Win32.HWND_NOTOPMOST, 0, 0, 0, 0, Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
            SendToBottom();
        }
    }

    /// <summary>从小组件里打开的对话框（Owner 是小组件）不算切走</summary>
    private bool IsWidgetDialog(IntPtr hwnd)
    {
        foreach (Window w in Application.Current.Windows)
            if (w.Owner == _window && new WindowInteropHelper(w).Handle == hwnd) return true;
        return false;
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero)
        {
            Win32.UnhookWinEvent(_hook);
            _hook = IntPtr.Zero;
        }
        _source?.RemoveHook(WndProc);
    }
}
