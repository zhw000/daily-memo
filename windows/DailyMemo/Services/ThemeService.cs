using System;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace DailyMemo.Services;

/// <summary>浅色 / 深色 / 跟随系统，以及小组件的半透明背景</summary>
public static class ThemeService
{
    public static readonly Color BrandLight = Color.FromRgb(0x4C, 0x6E, 0xF5);
    public static readonly Color BrandDark = Color.FromRgb(0x7B, 0x93, 0xFF);

    private static string _mode = "system";
    private static double _widgetOpacity = 0.9;
    private static bool _watching;

    public static bool IsDark { get; private set; }

    public static event EventHandler? Changed;

    public static void Apply(string mode)
    {
        _mode = mode;
        bool dark = mode == "dark" || (mode != "light" && SystemIsDark());
        IsDark = dark;
        var theme = dark ? ApplicationTheme.Dark : ApplicationTheme.Light;

        ApplicationThemeManager.Apply(theme, WindowBackdropType.Mica, false);
        ApplicationAccentColorManager.Apply(dark ? BrandDark : BrandLight, theme);

        var res = Application.Current.Resources;
        res["BrandBrush"] = Frozen(dark ? BrandDark : BrandLight);
        res["OverdueBrush"] = Frozen(dark ? Color.FromRgb(0xFF, 0x7B, 0x7B) : Color.FromRgb(0xD9, 0x36, 0x36));
        res["SuccessBrush"] = Frozen(dark ? Color.FromRgb(0x5F, 0xD0, 0x8B) : Color.FromRgb(0x1E, 0x9E, 0x5A));
        res["WidgetBorderBrush"] = Frozen(dark ? Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x22, 0x00, 0x00, 0x00));
        res["WidgetHoverBrush"] = Frozen(dark ? Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x10, 0x00, 0x00, 0x00));
        res["PillBrush"] = Frozen(dark ? Color.FromArgb(0x33, 0x7B, 0x93, 0xFF) : Color.FromArgb(0x1F, 0x4C, 0x6E, 0xF5));
        UpdateWidgetBackground(_widgetOpacity);

        foreach (Window w in Application.Current.Windows)
        {
            if (w is FluentWindow fw)
                WindowBackgroundManager.UpdateBackground(fw, theme, fw.WindowBackdropType);
        }

        if (!_watching)
        {
            _watching = true;
            SystemEvents.UserPreferenceChanged += (_, e) =>
            {
                if (e.Category == UserPreferenceCategory.General && _mode == "system")
                    Application.Current.Dispatcher.BeginInvoke(() => Apply(_mode));
            };
        }
        Changed?.Invoke(null, EventArgs.Empty);
    }

    public static void UpdateWidgetBackground(double opacity)
    {
        _widgetOpacity = Math.Clamp(opacity, 0.3, 1.0);
        byte a = (byte)Math.Round(_widgetOpacity * 255);
        var c = IsDark ? Color.FromArgb(a, 0x1F, 0x21, 0x27) : Color.FromArgb(a, 0xF8, 0xF9, 0xFC);
        Application.Current.Resources["WidgetBackgroundBrush"] = Frozen(c);
    }

    private static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private static bool SystemIsDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch
        {
            return false;
        }
    }
}
