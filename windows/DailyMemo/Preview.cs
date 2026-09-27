using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DailyMemo.Core;
using DailyMemo.Services;
using DailyMemo.ViewModels;
using DailyMemo.Views;

namespace DailyMemo;

/// <summary>
/// 开发用：用示例数据渲染各个界面并保存为 PNG（DailyMemo.exe --preview 输出目录）。
/// 不联网、不写设置（请同时设置 DAILYMEMO_DATA_DIR 指向临时目录）。
/// </summary>
internal static class Preview
{
    public static async void Run(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            var host = new AppHost(preview: true);
            var (items, containers) = DemoData.Create(DateTime.Now);
            host.Agenda.LoadDemo(items, containers);

            foreach (var theme in new[] { "light", "dark" })
            {
                ThemeService.Apply(theme);
                ThemeService.UpdateWidgetBackground(0.9);

                var main = new MainWindow(host.MainVm)
                {
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = 40,
                    Top = 40,
                    ShowActivated = false,
                };
                main.Show();
                foreach (var page in new[] { "today", "upcoming", "lists", "settings" })
                {
                    host.MainVm.Navigate(page);
                    await Render(main, Path.Combine(dir, $"main-{page}-{theme}.png"), null);
                }
                main.Close();

                var widget = new WidgetWindow(host.WidgetVm) { ShowActivated = false };
                widget.ApplySettings(host.Settings.Current);
                widget.Show();
                host.WidgetVm.Rebuild();
                await Render(widget, Path.Combine(dir, $"widget-{theme}.png"), Wallpaper());
                widget.Close();

                var editor = new EditorWindow(new EditorViewModel(host, null, ItemKind.Task, DateTime.Today)) { ShowActivated = false };
                editor.Show();
                await Render(editor, Path.Combine(dir, $"editor-task-{theme}.png"), null);
                editor.Close();

                var evEditor = new EditorWindow(new EditorViewModel(host, items.Find(i => i.IsEvent && !i.AllDay && i.Source == ItemSource.Google), ItemKind.Event, null)) { ShowActivated = false };
                evEditor.Show();
                await Render(evEditor, Path.Combine(dir, $"editor-event-{theme}.png"), null);
                evEditor.Close();
            }
            Console.WriteLine("preview done: " + dir);
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(dir, "preview-error.txt"), ex.ToString());
        }
        finally
        {
            Application.Current.Shutdown();
        }
    }

    private static Brush Wallpaper()
    {
        var b = new LinearGradientBrush(Color.FromRgb(0x2B, 0x5D, 0x8C), Color.FromRgb(0xC9, 0x7B, 0x63), new Point(0, 0), new Point(1, 1));
        b.Freeze();
        return b;
    }

    private static async Task Render(Window w, string path, Brush? background)
    {
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        await Task.Delay(400);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);

        // 透明的小组件窗口直接渲染整个窗口；Mica 窗口渲染内容区并补一个背景色
        Visual visual = w.AllowsTransparency ? w : (Visual)w.Content;
        var fe = (FrameworkElement)visual;
        double width = fe.ActualWidth, height = fe.ActualHeight;
        const double scale = 1.25;
        var bg = background ?? (Brush)Application.Current.Resources["ApplicationBackgroundBrush"];

        var area = new Rect(0, 0, width, height);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(bg, null, area);
            dc.DrawRectangle(new VisualBrush(visual)
            {
                Stretch = Stretch.None,
                ViewboxUnits = BrushMappingMode.Absolute,
                Viewbox = area,
                ViewportUnits = BrushMappingMode.Absolute,
                Viewport = area,
            }, null, area);
        }
        var bmp = new RenderTargetBitmap((int)Math.Ceiling(width * scale), (int)Math.Ceiling(height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bmp.Render(dv);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        enc.Save(fs);
    }
}

/// <summary>界面预览用的示例数据</summary>
internal static class DemoData
{
    public static (List<AgendaItem> items, List<SourceContainer> containers) Create(DateTime now)
    {
        var today = now.Date;
        var local = LocalTaskStore.Container;
        var myTasks = new SourceContainer { Key = "gt:list1", Kind = ItemKind.Task, Source = ItemSource.Google, Id = "list1", Name = "我的任务", Color = GoogleApi.TasksColor, IsPrimary = true };
        var phone = new SourceContainer { Key = "gt:list2", Kind = ItemKind.Task, Source = ItemSource.Google, Id = "list2", Name = "提醒事项", Color = GoogleApi.TasksColor };
        var work = new SourceContainer { Key = "gc:work", Kind = ItemKind.Event, Source = ItemSource.Google, Id = "work", Name = "工作", Color = "#039BE5", IsPrimary = true };
        var holiday = new SourceContainer { Key = "gc:holiday", Kind = ItemKind.Event, Source = ItemSource.Google, Id = "holiday", Name = "中国节假日", Color = "#0B8043", Writable = false };
        var family = new SourceContainer { Key = "ic:family", Kind = ItemKind.Event, Source = ItemSource.ICloud, Id = "family", Name = "家庭", Color = "#FF9500" };
        var containers = new List<SourceContainer> { local, myTasks, phone, work, holiday, family };

        int n = 0;
        AgendaItem Task(SourceContainer c, string title, DateTime? due, bool hasTime = false, bool done = false) => new()
        {
            Key = "demo|" + n++,
            Kind = ItemKind.Task,
            Source = c.Source,
            Id = "t" + n,
            ContainerId = c.Id,
            ContainerName = c.Name,
            Color = c.Color,
            Title = title,
            Start = due,
            AllDay = !hasTime,
            Completed = done,
            CompletedAt = done ? now.AddHours(-1) : null,
        };
        AgendaItem Event(SourceContainer c, string title, DateTime start, DateTime? end, bool allDay = false, string? location = null, bool recurring = false) => new()
        {
            Key = "demo|" + n++,
            Kind = ItemKind.Event,
            Source = c.Source,
            Id = "e" + n,
            ContainerId = c.Id,
            ContainerName = c.Name,
            Color = c.Color,
            Title = title,
            Start = start,
            End = end ?? (allDay ? start.AddDays(1) : start.AddHours(1)),
            AllDay = allDay,
            Location = location,
            Recurring = recurring,
            ReadOnly = !c.Writable,
        };

        var items = new List<AgendaItem>
        {
            Task(myTasks, "交水电费", today.AddDays(-1)),
            Task(phone, "给车续保险", today.AddDays(-3)),
            Event(holiday, "国庆节前调休上班", today, null, allDay: true),
            Event(work, "部门站会", today.AddHours(9).AddMinutes(30), today.AddHours(10), recurring: true),
            Event(work, "产品评审会", today.AddHours(14), today.AddHours(15).AddMinutes(30), location: "3 楼大会议室"),
            Task(myTasks, "交周报", today.AddHours(16), hasTime: true),
            Event(family, "接孩子放学", today.AddHours(18).AddMinutes(30), today.AddHours(19)),
            Task(local, "买牛奶和鸡蛋", today),
            Task(myTasks, "回复王老师的邮件", today),
            Task(phone, "给妈妈打电话", today),
            Task(myTasks, "整理书架", null),
            Task(phone, "学 SwiftUI 第三章", null),
            Task(myTasks, "取快递", today, done: true),
            Event(family, "看牙医", today.AddDays(1).AddHours(10), today.AddDays(1).AddHours(11), location: "市口腔医院"),
            Task(myTasks, "周报初稿", today.AddDays(1)),
            Event(work, "项目周会", today.AddDays(2).AddHours(14), today.AddDays(2).AddHours(15), recurring: true),
            Event(family, "妈妈生日", today.AddDays(3), null, allDay: true),
            Task(phone, "订生日蛋糕", today.AddDays(2)),
            Event(holiday, "国庆节", today.AddDays(4), today.AddDays(11), allDay: true),
            Event(family, "健身", today.AddDays(5).AddHours(19), today.AddDays(5).AddHours(20)),
        };
        return (items, containers);
    }
}
