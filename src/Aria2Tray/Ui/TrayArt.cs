using System.Runtime.InteropServices;
using static Aria2Tray.Native.Win32;

namespace Aria2Tray.Ui;

/// <summary>
/// 托盘图标，按当前进度现画（见 docs/design.md 第 3 节）。在 24 × 24 的格子里描图形，每个像素取 4 × 4 个点算覆盖率做抗锯齿，
/// 画成 32 位带透明的位图再转 HICON。单色：任务栏浅色时近黑，深色时白。
/// - 没有下载中的任务：向下的箭头落到一条托盘上。
/// - 有：外圈进度环（底槽淡色，已完成的实色，从 12 点钟顺时针），箭头和托盘缩小放在环里。
/// </summary>
internal static class TrayArt
{
    private const int Samples = 4;
    private const double TrackAlpha = 0.32;
    private const double RingOuter = 11.6, RingInner = 9.2;

    /// <summary>progress 为 null 是空闲图标；0 到 1 是进度环。</summary>
    public static nint Render(bool lightTaskbar, int size, double? progress)
    {
        var (r, g, b) = lightTaskbar ? (28, 28, 28) : (255, 255, 255);
        var pixels = new int[size * size];
        var k = 24.0 / size;
        var shown = progress is { } p ? (p > 0 ? Math.Max(p, 0.03) : 0) : 0;
        for (var py = 0; py < size; py++)
        {
            for (var px = 0; px < size; px++)
            {
                double alpha = 0;
                for (var sy = 0; sy < Samples; sy++)
                {
                    for (var sx = 0; sx < Samples; sx++)
                    {
                        var x = (px + ((sx + 0.5) / Samples)) * k;
                        var y = (py + ((sy + 0.5) / Samples)) * k;
                        alpha += progress is null ? Idle(x, y) : Busy(x, y, shown);
                    }
                }

                var a = (int)Math.Round(Math.Clamp(alpha / (Samples * Samples), 0, 1) * 255);
                pixels[(py * size) + px] = (a << 24) | (r << 16) | (g << 8) | b;
            }
        }

        return ToIcon(pixels, size);
    }

    /// <summary>空闲：箭头（杆 + 三角）和下面的托盘。</summary>
    private static double Idle(double x, double y) =>
        InRect(x, y, 10.5, 2.4, 13.5, 11) || InTriangle(x, y, 5.2, 10.2, 18.8, 10.2, 12, 17.2) || InRoundRect(x, y, 2.8, 19.3, 21.2, 22.1, 1.4) ? 1 : 0;

    /// <summary>下载中：进度环，环里是缩小的箭头和托盘。</summary>
    private static double Busy(double x, double y, double progress)
    {
        var dx = x - 12;
        var dy = y - 12;
        var d = Math.Sqrt((dx * dx) + (dy * dy));
        if (d >= RingInner && d <= RingOuter)
        {
            var angle = Math.Atan2(dx, -dy);
            if (angle < 0)
            {
                angle += 2 * Math.PI;
            }

            return angle / (2 * Math.PI) <= progress ? 1 : TrackAlpha;
        }

        return InRect(x, y, 11, 5.6, 13, 10.6) || InTriangle(x, y, 7.6, 10, 16.4, 10, 12, 14.8) || InRoundRect(x, y, 7.4, 16, 16.6, 17.8, 0.9) ? 1 : 0;
    }

    private static bool InRect(double x, double y, double l, double t, double r, double b) => x >= l && x <= r && y >= t && y <= b;

    private static bool InRoundRect(double x, double y, double l, double t, double r, double b, double radius)
    {
        if (!InRect(x, y, l, t, r, b))
        {
            return false;
        }

        var cx = Math.Clamp(x, l + radius, r - radius);
        var cy = Math.Clamp(y, t + radius, b - radius);
        return ((x - cx) * (x - cx)) + ((y - cy) * (y - cy)) <= radius * radius;
    }

    private static bool InTriangle(double x, double y, double x1, double y1, double x2, double y2, double x3, double y3)
    {
        static double Side(double px, double py, double ax, double ay, double bx, double by) => ((px - bx) * (ay - by)) - ((ax - bx) * (py - by));
        var d1 = Side(x, y, x1, y1, x2, y2);
        var d2 = Side(x, y, x2, y2, x3, y3);
        var d3 = Side(x, y, x3, y3, x1, y1);
        var neg = d1 < 0 || d2 < 0 || d3 < 0;
        var pos = d1 > 0 || d2 > 0 || d3 > 0;
        return !(neg && pos);
    }

    /// <summary>自上而下的 32 位位图（不预乘的透明度，和 .ico 里一样）加一张全零的掩码，拼成图标。</summary>
    private static nint ToIcon(int[] pixels, int size)
    {
        var header = new BITMAPINFOHEADER
        {
            biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = size,
            biHeight = -size,
            biPlanes = 1,
            biBitCount = 32,
        };
        var color = CreateDIBSection(0, ref header, 0, out var bits, 0, 0);
        if (color == 0)
        {
            return 0;
        }

        Marshal.Copy(pixels, 0, bits, pixels.Length);
        var mask = CreateBitmap(size, size, 1, 1, 0);
        var info = new ICONINFO { fIcon = true, hbmColor = color, hbmMask = mask };
        var icon = CreateIconIndirect(ref info);
        DeleteObject(color);
        DeleteObject(mask);
        return icon;
    }
}
