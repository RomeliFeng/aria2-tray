namespace Aria2Tray.Model;

/// <summary>大小、速度、时长、百分比的写法。大小按 1024 进位（和 aria2 一样），单位写 KB / MB / GB。</summary>
internal static class Format
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB", "PB"];

    public static string Size(long bytes)
    {
        if (bytes < 1024)
        {
            return bytes.ToString(L.Culture) + " B";
        }

        double v = bytes;
        var i = 0;
        while (v >= 1024 && i < Units.Length - 1)
        {
            v /= 1024;
            i++;
        }

        // 三位有效数字：1.23 GB、12.3 MB、123 KB。
        var text = v >= 100 ? v.ToString("0", L.Culture) : v >= 10 ? v.ToString("0.0", L.Culture) : v.ToString("0.00", L.Culture);
        return text + " " + Units[i];
    }

    public static string Speed(long bytesPerSecond) => Size(bytesPerSecond) + "/s";

    /// <summary>往下取整，没下完不会显示 100%；不到 10% 时带一位小数。</summary>
    public static string Percent(double fraction) =>
        fraction >= 1 ? "100%"
        : fraction < 0.1 ? (Math.Floor(fraction * 1000) / 10).ToString("0.0", L.Culture) + "%"
        : Math.Floor(fraction * 100).ToString("0", L.Culture) + "%";

    /// <summary>剩余时间：「3 分钟」「1 小时 5 分」「12 秒」；英文「3 min」「1 h 5 min」「12 s」。超过 99 天写「很久」。</summary>
    public static string Duration(TimeSpan t)
    {
        if (t.TotalDays >= 99)
        {
            return L.T("很久", "a long time");
        }

        if (t.TotalDays >= 1)
        {
            return L.T($"{(int)t.TotalDays} 天 {t.Hours} 小时", $"{(int)t.TotalDays} d {t.Hours} h");
        }

        if (t.TotalHours >= 1)
        {
            return L.T($"{t.Hours} 小时 {t.Minutes} 分", $"{t.Hours} h {t.Minutes} min");
        }

        if (t.TotalMinutes >= 1)
        {
            return L.T($"{t.Minutes} 分钟", $"{t.Minutes} min");
        }

        return L.T($"{Math.Max(1, t.Seconds)} 秒", $"{Math.Max(1, t.Seconds)} s");
    }
}
