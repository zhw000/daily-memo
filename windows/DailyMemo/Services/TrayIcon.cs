using System;
using System.Drawing;
using System.Windows;
using WinForms = System.Windows.Forms;

namespace DailyMemo.Services;

/// <summary>系统托盘图标：左键打开主界面，右键菜单</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly WinForms.NotifyIcon _icon;
    private readonly WinForms.ToolStripMenuItem _widgetItem;
    private readonly WinForms.ToolStripMenuItem _startupItem;

    public TrayIcon(AppHost host)
    {
        var menu = new WinForms.ContextMenuStrip
        {
            Font = new Font("Microsoft YaHei UI", 9f),
            ShowImageMargin = false,
        };
        menu.Items.Add("打开今日事", null, (_, _) => host.ShowMainWindow());
        menu.Items.Add("新建待办…", null, (_, _) => host.OpenEditor(null, Core.ItemKind.Task, null, null));
        _widgetItem = new WinForms.ToolStripMenuItem("显示桌面小组件", null, (_, _) => host.ToggleWidget());
        menu.Items.Add(_widgetItem);
        menu.Items.Add("立即同步", null, (_, _) => _ = host.Agenda.RefreshAsync());
        menu.Items.Add(new WinForms.ToolStripSeparator());
        _startupItem = new WinForms.ToolStripMenuItem("开机自动启动", null, (_, _) => host.SetLaunchAtStartup(!host.Settings.Current.LaunchAtStartup));
        menu.Items.Add(_startupItem);
        menu.Items.Add("设置", null, (_, _) => host.ShowMainWindow("settings"));
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => host.Exit());
        menu.Opening += (_, _) =>
        {
            _widgetItem.Checked = host.Settings.Current.WidgetVisible;
            _startupItem.Checked = host.Settings.Current.LaunchAtStartup;
        };

        _icon = new WinForms.NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "今日事",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == WinForms.MouseButtons.Left) host.ShowMainWindow();
        };
    }

    public void SetTooltip(string text)
    {
        // NotifyIcon.Text 最多 127 个字符
        _icon.Text = text.Length > 120 ? text[..120] + "…" : text;
    }

    private static Icon LoadIcon()
    {
        var info = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"));
        var size = WinForms.SystemInformation.SmallIconSize;
        using var stream = info!.Stream;
        return new Icon(stream, size.Width, size.Height);
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
