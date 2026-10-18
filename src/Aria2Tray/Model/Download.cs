using System.Globalization;
using System.Text.Json;

namespace Aria2Tray.Model;

internal enum Group
{
    Active,
    Waiting,
    Done,
}

/// <summary>一个文件（tellStatus 的 files）。Path 是 aria2 那台机器上的完整路径。</summary>
internal sealed record DownloadFile(int Index, string Path, long Length, long Completed, bool Selected, string? Uri);

/// <summary>全局速度和任务数（getGlobalStat）。</summary>
internal sealed record GlobalStat(long DownloadSpeed, long UploadSpeed, int NumActive, int NumWaiting, int NumStopped)
{
    public static readonly GlobalStat Empty = new(0, 0, 0, 0, 0);

    public static GlobalStat Parse(JsonElement e) => new(
        Download.Long(e, "downloadSpeed"), Download.Long(e, "uploadSpeed"),
        (int)Download.Long(e, "numActive"), (int)Download.Long(e, "numWaiting"), (int)Download.Long(e, "numStopped"));
}

/// <summary>
/// 一个任务：aria2 原样给的字段（数字在 aria2 里都是字符串），加上派生的名字、进度、剩余时间。
/// 状态：active、waiting、paused、error、complete、removed。
/// </summary>
internal sealed record Download
{
    /// <summary>列表里取的字段（不取 files：大种子有几千个文件，每秒取一次太重；名字另外取，见 Aria2Session）。</summary>
    public static readonly string[] ListKeys =
    [
        "gid", "status", "totalLength", "completedLength", "uploadLength", "downloadSpeed", "uploadSpeed", "connections",
        "numSeeders", "seeder", "errorCode", "errorMessage", "dir", "bittorrent", "followedBy", "infoHash",
    ];

    public required string Gid { get; init; }

    public required string Status { get; init; }

    public long TotalLength { get; init; }

    public long CompletedLength { get; init; }

    public long UploadLength { get; init; }

    public long DownloadSpeed { get; init; }

    public long UploadSpeed { get; init; }

    public int Connections { get; init; }

    public int? NumSeeders { get; init; }

    public bool Seeder { get; init; }

    public string? ErrorCode { get; init; }

    public string? ErrorMessage { get; init; }

    public string Dir { get; init; } = "";

    public string? InfoHash { get; init; }

    /// <summary>有 bittorrent 字段就是 BT 任务（包括还在取种子信息的磁力链接）。</summary>
    public bool IsBt { get; init; }

    /// <summary>种子里的名字（bittorrent.info.name）；磁力链接还没取到种子信息时是 null。</summary>
    public string? BtName { get; init; }

    /// <summary>这个任务完成后接着生成的任务（磁力链接取完种子信息后的真正下载）。</summary>
    public IReadOnlyList<string> FollowedBy { get; init; } = [];

    /// <summary>文件列表：只有详情页取，或者为了名字取过的第一个文件；列表里没取时是空的。</summary>
    public IReadOnlyList<DownloadFile> Files { get; init; } = [];

    // ---------------- 派生 ----------------

    public Group Group => Status switch
    {
        "active" => Group.Active,
        "waiting" or "paused" => Group.Waiting,
        _ => Group.Done,
    };

    /// <summary>磁力链接先下种子信息：文件路径以 [METADATA] 开头，或者 BT 任务还没有种子名。</summary>
    public bool IsMetadata => (IsBt && BtName is null) || Files is [{ Path: var p }, ..] && p.StartsWith("[METADATA]", StringComparison.Ordinal);

    /// <summary>BT 下完、正在做种（aria2 里状态还是 active）。</summary>
    public bool IsSeeding => Status == "active" && IsBt && (Seeder || (TotalLength > 0 && CompletedLength >= TotalLength));

    /// <summary>真在下载：活动中、不是做种。</summary>
    public bool IsDownloading => Status == "active" && !IsSeeding;

    public bool CanPause => Status is "active" or "waiting";

    public bool CanResume => Status == "paused";

    /// <summary>0 到 1；总大小还不知道时是 null。</summary>
    public double? Progress => TotalLength > 0 ? Math.Clamp((double)CompletedLength / TotalLength, 0, 1) : (Status == "complete" ? 1 : null);

    /// <summary>剩余时间；没在下或速度是 0 时是 null。</summary>
    public TimeSpan? Eta => IsDownloading && DownloadSpeed > 0 && TotalLength > CompletedLength
        ? TimeSpan.FromSeconds((double)(TotalLength - CompletedLength) / DownloadSpeed)
        : null;

    /// <summary>分享率 = 已上传 ÷ 已下载。</summary>
    public double? Ratio => IsBt && CompletedLength > 0 ? (double)UploadLength / CompletedLength : null;

    /// <summary>
    /// 显示的名字：BT 用种子里的名字；否则用第一个文件的文件名；还没有文件名时用链接的最后一段。
    /// 取种子信息的阶段是 null（界面写「正在取种子信息」）。
    /// </summary>
    public string? Name
    {
        get
        {
            if (BtName is { Length: > 0 } bt)
            {
                return bt;
            }

            if (IsMetadata)
            {
                return null;
            }

            if (Files is [var f, ..])
            {
                if (f.Path.Length > 0)
                {
                    return System.IO.Path.GetFileName(f.Path.Replace('\\', '/').TrimEnd('/')) is { Length: > 0 } n ? n : f.Path;
                }

                if (f.Uri is { } u && Uri.TryCreate(u, UriKind.Absolute, out var uri))
                {
                    var last = uri.Segments.LastOrDefault()?.Trim('/');
                    return last is { Length: > 0 } ? Uri.UnescapeDataString(last) : uri.Host;
                }
            }

            return null;
        }
    }

    public static Download Parse(JsonElement e, IReadOnlyList<DownloadFile>? files = null)
    {
        string? btName = null;
        var isBt = e.TryGetProperty("bittorrent", out var bt) && bt.ValueKind == JsonValueKind.Object;
        if (isBt && bt.TryGetProperty("info", out var info) && info.TryGetProperty("name", out var name))
        {
            btName = name.GetString();
        }

        return new Download
        {
            Gid = Str(e, "gid") ?? "",
            Status = Str(e, "status") ?? "",
            TotalLength = Long(e, "totalLength"),
            CompletedLength = Long(e, "completedLength"),
            UploadLength = Long(e, "uploadLength"),
            DownloadSpeed = Long(e, "downloadSpeed"),
            UploadSpeed = Long(e, "uploadSpeed"),
            Connections = (int)Long(e, "connections"),
            NumSeeders = e.TryGetProperty("numSeeders", out _) ? (int)Long(e, "numSeeders") : null,
            Seeder = Str(e, "seeder") == "true",
            ErrorCode = Str(e, "errorCode") is { } code && code != "0" ? code : null,
            ErrorMessage = Str(e, "errorMessage") is { Length: > 0 } msg ? msg : null,
            Dir = Str(e, "dir") ?? "",
            InfoHash = Str(e, "infoHash"),
            IsBt = isBt,
            BtName = btName,
            FollowedBy = e.TryGetProperty("followedBy", out var fb) && fb.ValueKind == JsonValueKind.Array
                ? [.. fb.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0)]
                : [],
            Files = files ?? (e.TryGetProperty("files", out var fs) ? ParseFiles(fs) : []),
        };
    }

    public static IReadOnlyList<DownloadFile> ParseFiles(JsonElement files)
    {
        if (files.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return [.. files.EnumerateArray().Select(f => new DownloadFile(
            (int)Long(f, "index"),
            Str(f, "path") ?? "",
            Long(f, "length"),
            Long(f, "completedLength"),
            Str(f, "selected") != "false",
            f.TryGetProperty("uris", out var uris) && uris.ValueKind == JsonValueKind.Array && uris.GetArrayLength() > 0 ? Str(uris[0], "uri") : null))];
    }

    public static string? Str(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public static long Long(JsonElement e, string key) =>
        Str(e, key) is { } s && long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;
}
