using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using DailyMemo.Core;
using DailyMemo.Native;
using DailyMemo.ViewModels;

namespace DailyMemo.Views;

public partial class WidgetWindow
{
    private readonly WidgetViewModel _vm;
    private readonly DesktopPinning _pinning;
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private bool _applying;

    public WidgetWindow(WidgetViewModel vm)
    {
        _vm = vm;
        DataContext = vm;
        InitializeComponent();
        _pinning = new DesktopPinning(this);

        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            SavePlacement();
        };
        LocationChanged += (_, _) => QueueSave();
        SizeChanged += (_, _) =>
        {
            QueueSave();
            UpdateProgress();
        };
        vm.PropertyChanged += OnVmChanged;
        Closed += (_, _) =>
        {
            vm.PropertyChanged -= OnVmChanged;
            _pinning.Dispose();
        };
        StateChanged += (_, _) =>
        {
            // 小组件不应该被最小化（例如按了 Win+M）
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        };
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(WidgetViewModel.Progress) or nameof(WidgetViewModel.Summary)) UpdateProgress();
    }

    private void UpdateProgress()
    {
        bool show = _vm.Progress > 0;
        ProgressTrack.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        ProgressBar.Width = Math.Max(0, (ActualWidth - 20 - 32) * _vm.Progress);
    }

    public void ApplySettings(AppSettings s)
    {
        _applying = true;
        try
        {
            double w = s.WidgetWidth is >= 220 and <= 2000 ? s.WidgetWidth : 340;
            double h = s.WidgetHeight is >= 180 and <= 2000 ? s.WidgetHeight : 460;
            Width = w;
            Height = h;

            var area = SystemParameters.WorkArea;
            double left = s.WidgetLeft, top = s.WidgetTop;
            bool valid = !double.IsNaN(left) && !double.IsNaN(top) && IsOnScreen(left, top, w, h);
            if (!valid)
            {
                left = area.Right - w - 16;
                top = area.Top + 16;
            }
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = left;
            Top = top;

            _pinning.Mode = s.WidgetMode;
            _pinning.ResizeEnabled = !s.WidgetLocked;
            Header.Cursor = s.WidgetLocked ? Cursors.Arrow : Cursors.SizeAll;
        }
        finally
        {
            _applying = false;
        }
    }

    private static bool IsOnScreen(double left, double top, double w, double h)
    {
        var vs = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        var r = new Rect(left, top, w, h);
        r.Intersect(vs);
        return !r.IsEmpty && r.Width > 80 && r.Height > 60;
    }

    private void QueueSave()
    {
        if (_applying || !IsLoaded) return;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    public void SavePlacement()
    {
        if (!IsLoaded || WindowState != WindowState.Normal) return;
        var s = AppHost.Current.Settings;
        s.Current.WidgetLeft = Math.Round(Left);
        s.Current.WidgetTop = Math.Round(Top);
        s.Current.WidgetWidth = Math.Round(ActualWidth);
        s.Current.WidgetHeight = Math.Round(ActualHeight);
        s.Save();
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            AppHost.Current.ShowMainWindow();
            return;
        }
        if (AppHost.Current.Settings.Current.WidgetLocked) return;
        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
        }
    }

    private void MenuButton_Click(object sender, RoutedEventArgs e)
    {
        if (Card.ContextMenu is not ContextMenu menu) return;
        menu.DataContext = DataContext;
        menu.PlacementTarget = MenuButton;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }
}
