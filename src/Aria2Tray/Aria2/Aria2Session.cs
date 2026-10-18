using System.Net.WebSockets;
using System.Text.Json;
using Aria2Tray.Model;
using Microsoft.UI.Dispatching;

namespace Aria2Tray.Aria2;

/// <summary>
/// 连着一台 aria2：定时取全局速度和任务列表（见 docs/design.md 第 2 节），收到事件马上补取一次，下载完成、出错时报 <see cref="Notice"/>。
/// 断线按 1、2、5、10 秒退避重连；密钥不对就停下，等用户改设置（设置一改会换一个新的 Session）。
/// 从 UI 线程启动，取数和改状态都在 UI 线程上（await 回到 UI 线程），只有连接的收包在后台线程。
/// </summary>
internal sealed class Aria2Session(DispatcherQueue ui, Uri uri, string secret) : IBackend
{
    private static readonly TimeSpan[] Backoff = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10)];
    private static readonly string[] NameKeys = ["gid", "files"];

    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _wake = new(0, 1);

    /// <summary>名字要用的第一个文件，按 gid 记着（列表里不取 files）。</summary>
    private readonly Dictionary<string, DownloadFile> _firstFile = [];
    private readonly HashSet<string> _notified = [];
    private Aria2Connection? _conn;
    private bool _closed;
    private bool _panelOpen;

    public ConnectionStatus Status { get; private set; } = ConnectionStatus.Connecting;

    public string? Error { get; private set; }

    public bool Ready { get; private set; }

    public Snapshot Current { get; private set; } = Snapshot.Empty;

    public SpeedHistory History { get; } = new();

    public bool PanelOpen
    {
        get => _panelOpen;
        set
        {
            var opened = value && !_panelOpen;
            _panelOpen = value;
            if (opened)
            {
                Wake();
            }
        }
    }

    public string? DetailGid { get; set; }

    public string? DefaultDir { get; private set; }

    public Download? Detail { get; private set; }

    public event Action? Changed;

    public event Action<Download, NoticeKind>? Notice;

    public void Start() => _ = RunAsync();

    public Task PauseAsync(string gid) => Command("aria2.pause", gid);

    public Task ResumeAsync(string gid) => Command("aria2.unpause", gid);

    public Task RemoveAsync(Download d) => Command(d.Group == Group.Done ? "aria2.removeDownloadResult" : "aria2.remove", d.Gid);

    public Task PurgeAsync() => Command("aria2.purgeDownloadResult");

    public Task MoveAsync(string gid, bool top) => Command("aria2.changePosition", gid, 0, top ? "POS_SET" : "POS_END");

    public Task PauseAllAsync() => Command("aria2.pauseAll");

    public Task ResumeAllAsync() => Command("aria2.unpauseAll");

    public async Task<string?> GetDefaultDirAsync()
    {
        var r = await Conn().CallAsync("aria2.getGlobalOption");
        DefaultDir = Download.Str(r, "dir") ?? DefaultDir;
        return DefaultDir;
    }

    public Task AddUriAsync(string uri, string? dir) => Command("aria2.addUri", new[] { uri }, Options(dir));

    public Task AddTorrentAsync(byte[] torrent, string? dir) => Command("aria2.addTorrent", Convert.ToBase64String(torrent), Array.Empty<string>(), Options(dir));

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        if (_conn is { } c)
        {
            _conn = null;
            await c.DisposeAsync();
        }

        _cts.Dispose();
    }

    private static Dictionary<string, string> Options(string? dir) =>
        dir is { Length: > 0 } ? new() { ["dir"] = dir } : [];

    private Aria2Connection Conn() => _conn ?? throw new InvalidOperationException(L.T("没连上 aria2", "Not connected to aria2"));

    private async Task Command(string method, params object?[] args)
    {
        await Conn().CallAsync(method, args);
        Wake();
    }

    private void Wake()
    {
        if (_wake.CurrentCount == 0)
        {
            try
            {
                _wake.Release();
            }
            catch (SemaphoreFullException)
            {
            }
        }
    }

    private void SetStatus(ConnectionStatus status, string? error)
    {
        Status = status;
        Error = error;
        Changed?.Invoke();
    }

    private async Task RunAsync()
    {
        var attempt = 0;
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                if (!Ready)
                {
                    SetStatus(ConnectionStatus.Connecting, Error);
                }

                _closed = false;
                var conn = await Aria2Connection.ConnectAsync(uri, secret, _cts.Token);
                conn.Notified += (method, gid) => ui.TryEnqueue(() => _ = OnNotified(method, gid));
                conn.Closed += _ => ui.TryEnqueue(() =>
                {
                    _closed = true;
                    Wake();
                });
                _conn = conn;

                // 连上后先取一次，取到了才算连上（密钥不对就在这一步报 Unauthorized）。
                await PollAsync();
                attempt = 0;

                // 下载目录：「文件位置」的 aria2 那一边留空时用它，新建下载页也拿它预填。
                await GetDefaultDirAsync();
                SetStatus(ConnectionStatus.Connected, null);
                while (!_cts.IsCancellationRequested && !_closed)
                {
                    await _wake.WaitAsync(PanelOpen ? TimeSpan.FromSeconds(1) : TimeSpan.FromSeconds(2), _cts.Token);
                    if (_closed)
                    {
                        break;
                    }

                    await PollAsync();
                }

                if (!_cts.IsCancellationRequested)
                {
                    SetStatus(ConnectionStatus.Disconnected, L.T("连接断了", "Connection lost"));
                }
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested)
            {
                return;
            }
            catch (Aria2Exception ex) when (ex.Unauthorized)
            {
                await DropConnection();
                SetStatus(ConnectionStatus.AuthFailed, null);
                return;
            }
            catch (Exception ex) when (ex is WebSocketException or IOException or TimeoutException or OperationCanceledException or Aria2Exception or System.Net.Http.HttpRequestException or JsonException)
            {
                SetStatus(ConnectionStatus.Disconnected, Aria2Connection.Describe(ex));
            }

            await DropConnection();
            try
            {
                await Task.Delay(Backoff[Math.Min(attempt++, Backoff.Length - 1)], _cts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task DropConnection()
    {
        if (_conn is { } c)
        {
            _conn = null;
            await c.DisposeAsync();
        }
    }

    /// <summary>一次 system.multicall 取全部；名字还不知道的任务再补取一次第一个文件。</summary>
    private async Task PollAsync()
    {
        var conn = Conn();
        var full = PanelOpen;
        var detail = full ? DetailGid : null;
        var keys = Download.ListKeys;
        var calls = new List<(string, object?[])>
        {
            ("aria2.getGlobalStat", []),
            ("aria2.tellActive", [keys]),
        };
        if (full)
        {
            calls.Add(("aria2.tellWaiting", [0, 100, keys]));
            // 负的 offset 从最新的往回数：最近完成的 100 个，新的在前。
            calls.Add(("aria2.tellStopped", [-1, 100, keys]));
        }

        if (detail is not null)
        {
            calls.Add(("aria2.tellStatus", [detail]));
        }

        var r = await conn.MulticallAsync(calls);
        if (r[0].Error is { } err)
        {
            throw err;
        }

        var stat = GlobalStat.Parse(r[0].Result!.Value);
        List<Download> Parse(int i) => r[i].Result is { ValueKind: JsonValueKind.Array } a ? [.. a.EnumerateArray().Select(e => Download.Parse(e))] : [];
        var active = Parse(1);
        var waiting = full ? Parse(2) : [.. Current.Waiting];
        var stopped = full ? Snapshot.WithoutFinishedMetadata(Parse(3)) : [.. Current.Stopped];
        if (detail is not null)
        {
            Detail = r[^1].Result is { } d ? Download.Parse(d) : null;
        }

        // 列表里不取 files：不是 BT 的任务，第一个文件另外取。下载中的任务文件名可能还会变（服务器给了文件名才知道），每次都重取。
        var need = active.Concat(waiting).Concat(stopped)
            .Where(d => !d.IsBt && (!_firstFile.TryGetValue(d.Gid, out var f) || (d.Status == "active" && f.Path.Length == 0)))
            .Select(d => d.Gid)
            .Take(30)
            .ToList();
        if (need.Count > 0)
        {
            var names = await conn.MulticallAsync([.. need.Select(g => ("aria2.tellStatus", new object?[] { g, NameKeys }))]);
            for (var i = 0; i < need.Count; i++)
            {
                if (names[i].Result is { } n && n.TryGetProperty("files", out var files) && Download.ParseFiles(files) is [var first, ..])
                {
                    _firstFile[need[i]] = first;
                }
            }
        }

        Download WithFile(Download d) => _firstFile.TryGetValue(d.Gid, out var f) ? d with { Files = [f] } : d;
        active = [.. active.Select(WithFile)];
        waiting = [.. waiting.Select(WithFile)];
        stopped = [.. stopped.Select(WithFile)];
        if (full)
        {
            var known = active.Concat(waiting).Concat(stopped).Select(d => d.Gid).ToHashSet();
            foreach (var gone in _firstFile.Keys.Where(g => !known.Contains(g)).ToList())
            {
                _firstFile.Remove(gone);
            }
        }

        Current = new Snapshot(stat, active, waiting, stopped);
        History.Add(stat.DownloadSpeed, stat.UploadSpeed);
        Ready = true;
        Changed?.Invoke();
    }

    /// <summary>
    /// 事件：先补取一次。完成、出错时取这个任务的详情决定要不要通知（docs/design.md 第 2 节「通知」）：
    /// 取种子信息的任务（followedBy 不空）不通知；BT 在 onBtDownloadComplete 时通知，之后做种结束的 onDownloadComplete 不再通知。
    /// </summary>
    private async Task OnNotified(string method, string gid)
    {
        Wake();
        var kind = method switch
        {
            "aria2.onDownloadComplete" or "aria2.onBtDownloadComplete" => NoticeKind.Complete,
            "aria2.onDownloadError" => (NoticeKind?)NoticeKind.Error,
            _ => null,
        };
        if (kind is null || _conn is not { } conn || !_notified.Add(gid + kind))
        {
            return;
        }

        try
        {
            var d = Download.Parse(await conn.CallAsync("aria2.tellStatus", gid));
            if (d.FollowedBy.Count > 0 || (method == "aria2.onDownloadComplete" && d.IsBt))
            {
                return;
            }

            Notice?.Invoke(d, kind.Value);
        }
        catch (Exception ex) when (ex is Aria2Exception or IOException or TimeoutException or WebSocketException or OperationCanceledException)
        {
        }
    }
}
