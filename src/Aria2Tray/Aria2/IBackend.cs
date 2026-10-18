using Aria2Tray.Model;

namespace Aria2Tray.Aria2;

internal enum ConnectionStatus
{
    Connecting,
    Connected,
    Disconnected,
    AuthFailed,
}

internal enum NoticeKind
{
    Complete,
    Error,
}

/// <summary>一次取到的全部数据。面板收起时只取全局速度和下载中的任务，等待中、已完成保留上次的值。</summary>
internal sealed record Snapshot(GlobalStat Stat, IReadOnlyList<Download> Active, IReadOnlyList<Download> Waiting, IReadOnlyList<Download> Stopped)
{
    public static readonly Snapshot Empty = new(GlobalStat.Empty, [], [], []);

    public IEnumerable<Download> All => Active.Concat(Waiting).Concat(Stopped);

    /// <summary>已完成的列表里去掉取完种子信息的中间任务（followedBy 不空）：它接着变成了真正的下载，列出来只是重复。</summary>
    public static List<Download> WithoutFinishedMetadata(IEnumerable<Download> stopped) =>
        [.. stopped.Where(d => !(d.IsMetadata && d.FollowedBy.Count > 0))];

    /// <summary>托盘进度环：下载中（不算做种）且知道总大小的任务，已下载之和 ÷ 总大小之和；一个都没有时是 null。</summary>
    public double? TotalProgress
    {
        get
        {
            var list = Active.Where(d => d.IsDownloading && d.TotalLength > 0).ToList();
            return list.Count == 0 ? null : Math.Clamp((double)list.Sum(d => d.CompletedLength) / list.Sum(d => d.TotalLength), 0, 1);
        }
    }

    public int DownloadingCount => Active.Count(d => d.IsDownloading);
}

/// <summary>数据来源：真的 aria2（<see cref="Aria2Session"/>）或演示数据（<see cref="DemoBackend"/>）。事件都在 UI 线程上触发。</summary>
internal interface IBackend : IAsyncDisposable
{
    ConnectionStatus Status { get; }

    /// <summary>连不上时的原因，给用户看。</summary>
    string? Error { get; }

    /// <summary>取到过一次数据。</summary>
    bool Ready { get; }

    Snapshot Current { get; }

    SpeedHistory History { get; }

    /// <summary>面板开着：1 秒取一次全部；收起：2 秒取一次全局速度和下载中的任务。</summary>
    bool PanelOpen { get; set; }

    /// <summary>详情页开着的任务：每次连它的文件列表一起取。</summary>
    string? DetailGid { get; set; }

    /// <summary>详情页的任务（带文件列表）；没取到时是 null。</summary>
    Download? Detail { get; }

    /// <summary>数据变了（每次取完，或连接状态变了）。</summary>
    event Action? Changed;

    /// <summary>下载完成、出错（通知用）。</summary>
    event Action<Download, NoticeKind>? Notice;

    void Start();

    Task PauseAsync(string gid);

    Task ResumeAsync(string gid);

    /// <summary>下载中、等待中的用 remove，已完成、出错的用 removeDownloadResult。文件留在原处。</summary>
    Task RemoveAsync(Download d);

    Task PurgeAsync();

    /// <summary>等待中的任务挪到队首（top）或队尾（aria2.changePosition 的 POS_SET 0 / POS_END 0）。</summary>
    Task MoveAsync(string gid, bool top);

    Task PauseAllAsync();

    Task ResumeAllAsync();

    /// <summary>aria2 的全局下载目录（全局选项 dir）。</summary>
    Task<string?> GetDefaultDirAsync();

    /// <summary>连上后取到的 aria2 全局下载目录；还没取到是 null。「文件位置」的 aria2 那一边留空时用它。</summary>
    string? DefaultDir { get; }

    Task AddUriAsync(string uri, string? dir);

    Task AddTorrentAsync(byte[] torrent, string? dir);
}
