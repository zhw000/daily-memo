using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

// 生成「今日事」图标：蓝色渐变底 + 白色日历页 + 对勾 + 橙色日历头。
// Windows 版带圆角透明边；iOS 版是满版方形（系统自己加圆角遮罩）。
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var root = args.Length > 0 ? args[0] : FindRepoRoot();
        var icoPath = Path.Combine(root, "windows", "DailyMemo", "Assets", "app.ico");
        var iosPath = Path.Combine(root, "ios", "App", "Assets.xcassets", "AppIcon.appiconset", "icon-1024.png");
        var pngPath = Path.Combine(root, "windows", "DailyMemo", "Assets", "app-256.png");

        int[] sizes = { 16, 20, 24, 32, 40, 48, 64, 128, 256 };
        var frames = sizes.Select(s => (s, Render(s, rounded: true))).ToList();
        WriteIco(icoPath, frames);
        File.WriteAllBytes(pngPath, EncodePng(Render(256, rounded: true)));
        File.WriteAllBytes(iosPath, EncodePng(Render(1024, rounded: false), dropAlpha: true));

        Console.WriteLine($"wrote {icoPath}");
        Console.WriteLine($"wrote {pngPath}");
        Console.WriteLine($"wrote {iosPath}");
        return 0;
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "windows"))) dir = dir.Parent;
        return dir?.FullName ?? Directory.GetCurrentDirectory();
    }

    private static BitmapSource Render(int size, bool rounded)
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(size / 1024.0, size / 1024.0));

            // 背景
            var bg = new LinearGradientBrush(Color.FromRgb(0x6F, 0x8B, 0xFF), Color.FromRgb(0x3B, 0x57, 0xE8), new Point(0, 0), new Point(1, 1));
            var bgRect = rounded ? new Rect(40, 40, 944, 944) : new Rect(0, 0, 1024, 1024);
            double radius = rounded ? 210 : 0;
            if (rounded)
            {
                // 小尺寸图标去掉阴影，避免发糊
                if (size >= 48)
                    dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(40, 0, 0, 40)), null, new Rect(40, 56, 944, 944), radius, radius);
            }
            dc.DrawRoundedRectangle(bg, null, bgRect, radius, radius);

            // 日历页
            var page = new Rect(232, 250, 560, 540);
            var pageGeo = new RectangleGeometry(page, 88, 88);
            dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(50, 10, 20, 90)), null,
                new RectangleGeometry(new Rect(page.X, page.Y + 18, page.Width, page.Height), 88, 88));
            dc.DrawGeometry(Brushes.White, null, pageGeo);

            // 橙色日历头（裁剪到页的圆角内）
            dc.PushClip(pageGeo);
            dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(0xFF, 0x8A, 0x5B), Color.FromRgb(0xFF, 0x6B, 0x4A), 90), null,
                new Rect(page.X, page.Y, page.Width, 150));
            dc.Pop();

            // 装订环
            var ring = new SolidColorBrush(Color.FromRgb(0xF3, 0xF5, 0xFF));
            dc.DrawRoundedRectangle(ring, null, new Rect(362, 196, 64, 120), 32, 32);
            dc.DrawRoundedRectangle(ring, null, new Rect(598, 196, 64, 120), 32, 32);

            // 对勾
            var check = new StreamGeometry();
            using (var g = check.Open())
            {
                g.BeginFigure(new Point(378, 572), false, false);
                g.LineTo(new Point(474, 666), true, true);
                g.LineTo(new Point(654, 480), true, true);
            }
            var pen = new Pen(new SolidColorBrush(Color.FromRgb(0x3B, 0x57, 0xE8)), size <= 24 ? 96 : 76)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
                LineJoin = PenLineJoin.Round,
            };
            dc.DrawGeometry(null, pen, check);
            dc.Pop();
        }

        var bmp = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(dv);
        bmp.Freeze();
        return bmp;
    }

    private static byte[] EncodePng(BitmapSource src, bool dropAlpha = false)
    {
        BitmapSource s = src;
        if (dropAlpha)
        {
            // App Store 图标不允许透明通道
            s = new FormatConvertedBitmap(src, PixelFormats.Bgr24, null, 0);
        }
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(s));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    // ICO：≤64px 用 32 位 DIB（兼容 System.Drawing.Icon），更大的用 PNG 压缩帧
    private static void WriteIco(string path, List<(int size, BitmapSource bmp)> frames)
    {
        var images = frames.Select(f => f.size <= 64 ? EncodeDib(f.bmp) : EncodePng(f.bmp)).ToList();
        using var fs = File.Create(path);
        using var w = new BinaryWriter(fs);
        w.Write((short)0);
        w.Write((short)1);
        w.Write((short)frames.Count);
        int offset = 6 + 16 * frames.Count;
        for (int i = 0; i < frames.Count; i++)
        {
            int s = frames[i].size;
            w.Write((byte)(s >= 256 ? 0 : s));
            w.Write((byte)(s >= 256 ? 0 : s));
            w.Write((byte)0);
            w.Write((byte)0);
            w.Write((short)1);
            w.Write((short)32);
            w.Write(images[i].Length);
            w.Write(offset);
            offset += images[i].Length;
        }
        foreach (var img in images) w.Write(img);
    }

    private static byte[] EncodeDib(BitmapSource src)
    {
        int w = src.PixelWidth, h = src.PixelHeight;
        var conv = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[w * h * 4];
        conv.CopyPixels(pixels, w * 4, 0);
        int maskStride = ((w + 31) / 32) * 4;
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write(40);
        bw.Write(w);
        bw.Write(h * 2);
        bw.Write((short)1);
        bw.Write((short)32);
        bw.Write(0);
        bw.Write(w * h * 4 + maskStride * h);
        bw.Write(0);
        bw.Write(0);
        bw.Write(0);
        bw.Write(0);
        for (int y = h - 1; y >= 0; y--) bw.Write(pixels, y * w * 4, w * 4);
        bw.Write(new byte[maskStride * h]);
        return ms.ToArray();
    }
}
