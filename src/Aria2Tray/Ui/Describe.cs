using Aria2Tray.Model;
using Microsoft.UI.Xaml.Media;

namespace Aria2Tray.Ui;

/// <summary>任务显示出来的名字、状态文字、进度条颜色（docs/design.md 第 4 节「任务卡片」）。卡片、详情页、通知、调试通道共用。</summary>
internal static class Describe
{
    public static string Name(Download d) => d.Name ?? (d.IsMetadata ? L.T("正在取种子信息", "Fetching torrent info") : d.Gid);

    public static string Status(Download d) => d.Status switch
    {
        "active" when d.IsSeeding => L.T("做种中", "Seeding"),
        "active" => L.T("下载中", "Downloading"),
        "waiting" => L.T("等待中", "Waiting"),
        "paused" => L.T("已暂停", "Paused"),
        "complete" => L.T("已完成", "Completed"),
        "error" => L.T("出错", "Error"),
        "removed" => L.T("已移除", "Removed"),
        _ => d.Status,
    };

    /// <summary>已下载 / 总大小 · 百分比；总大小不知道时只写已下载。</summary>
    public static string Amount(Download d) => d.TotalLength > 0
        ? $"{Format.Size(d.CompletedLength)} / {Format.Size(d.TotalLength)} · {Format.Percent(d.Progress ?? 0)}"
        : Format.Size(d.CompletedLength);

    /// <summary>卡片第三行的左右两段。</summary>
    public static (string Left, string Right) Lines(Download d) => d.Status switch
    {
        "error" => (d.ErrorMessage ?? L.T("出错", "Error"), ""),
        "complete" => (Format.Size(d.TotalLength), Status(d)),
        "removed" => (d.TotalLength > 0 ? Amount(d) : "", Status(d)),
        "active" when d.IsSeeding => (Format.Size(d.TotalLength), $"{Status(d)} · ↑ {Format.Speed(d.UploadSpeed)}"),
        "active" => (Amount(d), d.Eta is { } eta ? $"{Format.Speed(d.DownloadSpeed)} · {Format.Duration(eta)}" : Format.Speed(d.DownloadSpeed)),
        _ => (Amount(d), Status(d)),
    };

    /// <summary>
    /// 任务下载到的东西在 aria2 那台机器上的路径：BT 是「保存位置/种子名」（单文件种子就是那个文件，多文件是那个文件夹）；
    /// 其余是第一个文件。还不知道时是 null。
    /// </summary>
    public static string? RemotePath(Download d) =>
        d.IsBt && d.BtName is { Length: > 0 } name && d.Dir.Length > 0 ? d.Dir.TrimEnd('/', '\\') + "/" + name
        : d.Files is [{ Path: { Length: > 0 } p }, ..] && !p.StartsWith("[METADATA]", StringComparison.Ordinal) ? p
        : null;

    /// <summary>复制用的链接：BT 用信息哈希拼磁力链接（带名字），其余用第一个文件的第一个下载地址；都没有是 null。</summary>
    public static string? Link(Download d) =>
        d.IsBt && d.InfoHash is { Length: > 0 } hash
            ? "magnet:?xt=urn:btih:" + hash + (d.Name is { } n ? "&dn=" + Uri.EscapeDataString(n) : "")
            : d.Files is [{ Uri: { Length: > 0 } uri }, ..] ? uri : null;

    public static double? BarFraction(Download d) => d.Status == "error" ? 1 : d.Progress;

    public static Brush BarFill(Download d, Palette p) => d.Status switch
    {
        "active" when d.IsSeeding => p.Success,
        "active" => p.Accent,
        "complete" => p.Success,
        "error" => p.Error,
        _ => p.Idle,
    };
}
