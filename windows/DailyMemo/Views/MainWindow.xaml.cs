using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using DailyMemo.ViewModels;

namespace DailyMemo.Views;

public partial class MainWindow
{
    private readonly MainViewModel _vm;
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(3) };

    public MainWindow(MainViewModel vm)
    {
        _vm = vm;
        DataContext = vm;
        InitializeComponent();
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            ToastHost.Visibility = Visibility.Collapsed;
        };
        vm.PropertyChanged += OnVmChanged;
        UpdateSettingsNav();
        Closing += OnClosing;
        Closed += (_, _) => vm.PropertyChanged -= OnVmChanged;
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.Selected) or nameof(MainViewModel.IsSettingsSelected)) UpdateSettingsNav();
        if (e.PropertyName == nameof(MainViewModel.HasErrors))
            StatusText.SetResourceReference(ForegroundProperty, _vm.HasErrors ? "OverdueBrush" : "TextFillColorSecondaryBrush");
    }

    private void UpdateSettingsNav()
    {
        SettingsNav.Tag = _vm.IsSettingsSelected ? "selected" : null;
        // 「设置」不在导航列表里，选中它时清掉列表的选中状态
        if (_vm.IsSettingsSelected) Nav.UnselectAll();
        else if (Nav.SelectedItem != _vm.Selected) Nav.SelectedItem = _vm.Selected;
    }

    public void ShowToast(string message)
    {
        ToastText.Text = message;
        ToastHost.Visibility = Visibility.Visible;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        var host = AppHost.Current;
        if (host.IsPreview || host.Settings.Current.ClosedToTrayHintShown) return;
        host.Settings.Current.ClosedToTrayHintShown = true;
        host.Settings.Save();
        try
        {
            new Microsoft.Toolkit.Uwp.Notifications.ToastContentBuilder()
                .AddText("今日事还在后台运行")
                .AddText("桌面小组件和提醒会继续工作。点任务栏右下角的图标可以重新打开。")
                .Show();
        }
        catch
        {
        }
    }
}
