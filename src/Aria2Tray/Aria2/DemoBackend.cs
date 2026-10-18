using Aria2Tray.Model;
using Microsoft.UI.Dispatching;

namespace Aria2Tray.Aria2;

/// <summary>
/// --demo 的假数据：各种状态各有一个（下载中、BT、做种、取种子信息、等待、暂停、完成、出错），每秒往前走一点。
/// 名字都是公开的软件和影片，不含任何真实的下载记录。
/// </summary>
internal sealed class DemoBackend : IBackend
{
    private const long MB = 1024 * 1024, GB = 1024 * MB;

    private readonly DispatcherQueueTimer _timer;
    private readonly Random _random = new(7);
    private readonly List<Download> _tasks = [];
    private int _nextGid = 0x100;

    public DemoBackend(DispatcherQueue ui)
    {
        _timer = ui.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(1);
        _timer.Tick += (_, _) => Tick();

        Add(Http("ubuntu-26.04-desktop-amd64.iso", 5.9 * GB, 0.38, "active", 11.2 * MB, 16));
        Add(Bt("Big Buck Bunny 4K", [("Big Buck Bunny 4K/bbb_sunflower_2160p_60fps.mp4", 1.4 * GB), ("Big Buck Bunny 4K/poster.jpg", 0.4 * MB)], 0.61, "active", 4.3 * MB, 320 * 1024, 23));
        Add(Bt("Sintel", [("Sintel/sintel-2048-surround.mp4", 1.1 * GB)], 1, "active", 0, 120 * 1024, 41) with { Seeder = true });
        Add(new Download { Gid = Gid(), Status = "active", IsBt = true, Dir = "/downloads", Connections = 3, Files = [new(1, "[METADATA]", 0, 0, true, null)] });
        Add(Http("debian-13.1.0-amd64-netinst.iso", 754 * MB, 0, "waiting", 0, 0));
        Add(Http("archlinux-2026.10.01-x86_64.iso", 1.3 * GB, 0.12, "paused", 0, 0));
        Add(Http("LibreOffice_25.8.2_Win_x86-64.msi", 352 * MB, 1, "complete", 0, 0));
        Add(Http("sample-video.mp4", 0, 0, "error", 0, 0) with { ErrorCode = "3", ErrorMessage = "Resource not found" });
        Add(Http("node-v24.10.0-x64.msi", 31 * MB, 1, "complete", 0, 0));
    }

    public ConnectionStatus Status => ConnectionStatus.Connected;

    public string? Error => null;

    public bool Ready => true;

    public Snapshot Current { get; private set; } = Snapshot.Empty;

    public SpeedHistory History { get; } = new();

    public bool PanelOpen { get; set; }

    public string? DetailGid { get; set; }

    public Download? Detail => _tasks.FirstOrDefault(t => t.Gid == DetailGid);

    public event Action? Changed;

    public event Action<Download, NoticeKind>? Notice;

    public void Start()
    {
        // 曲线一开始就有一段。
        for (var i = 0; i < SpeedHistory.Length; i++)
        {
            History.Add((long)((14 + (4 * Math.Sin(i / 6.0)) + _random.NextDouble()) * MB), (long)((0.4 + (0.1 * Math.Cos(i / 4.0))) * MB));
        }

        Publish();
        _timer.Start();
    }

    public Task PauseAsync(string gid) => Change(gid, d => d with { Status = "paused", DownloadSpeed = 0, UploadSpeed = 0, Connections = 0 });

    public Task ResumeAsync(string gid) => Change(gid, d => d with { Status = "waiting" });

    public Task RemoveAsync(Download d)
    {
        var i = _tasks.FindIndex(t => t.Gid == d.Gid);
        if (i >= 0)
        {
            if (_tasks[i].Group == Group.Done)
            {
                _tasks.RemoveAt(i);
            }
            else
            {
                _tasks[i] = _tasks[i] with { Status = "removed", DownloadSpeed = 0, UploadSpeed = 0, Connections = 0 };
            }
        }

        Publish();
        return Task.CompletedTask;
    }

    public Task PurgeAsync()
    {
        _tasks.RemoveAll(t => t.Group == Group.Done);
        Publish();
        return Task.CompletedTask;
    }

    public Task MoveAsync(string gid, bool top)
    {
        var i = _tasks.FindIndex(t => t.Gid == gid);
        if (i >= 0)
        {
            var t = _tasks[i];
            _tasks.RemoveAt(i);
            if (top)
            {
                _tasks.Insert(0, t);
            }
            else
            {
                _tasks.Add(t);
            }
        }

        Publish();
        return Task.CompletedTask;
    }

    public Task PauseAllAsync()
    {
        for (var i = 0; i < _tasks.Count; i++)
        {
            if (_tasks[i].CanPause)
            {
                _tasks[i] = _tasks[i] with { Status = "paused", DownloadSpeed = 0, UploadSpeed = 0, Connections = 0 };
            }
        }

        Publish();
        return Task.CompletedTask;
    }

    public Task ResumeAllAsync()
    {
        for (var i = 0; i < _tasks.Count; i++)
        {
            if (_tasks[i].CanResume)
            {
                _tasks[i] = _tasks[i] with { Status = "waiting" };
            }
        }

        Publish();
        return Task.CompletedTask;
    }

    public Task<string?> GetDefaultDirAsync() => Task.FromResult<string?>("/downloads");

    public string? DefaultDir => "/downloads";

    public Task AddUriAsync(string uri, string? dir)
    {
        var name = Uri.TryCreate(uri, UriKind.Absolute, out var u) && u.Segments.LastOrDefault()?.Trim('/') is { Length: > 0 } last ? last : "download";
        Add(Http(Uri.UnescapeDataString(name), (200 + _random.Next(800)) * MB, 0, "waiting", 0, 0) with { Dir = dir ?? "/downloads" });
        Publish();
        return Task.CompletedTask;
    }

    public Task AddTorrentAsync(byte[] torrent, string? dir)
    {
        Add(Bt("Torrent " + (_tasks.Count + 1), [("file.bin", 600 * MB)], 0, "waiting", 0, 0, 0) with { Dir = dir ?? "/downloads" });
        Publish();
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _timer.Stop();
        return ValueTask.CompletedTask;
    }

    private string Gid() => (_nextGid++).ToString("x16", System.Globalization.CultureInfo.InvariantCulture);

    private Download Http(string name, double size, double done, string status, double speed, int connections) => new()
    {
        Gid = Gid(),
        Status = status,
        TotalLength = (long)size,
        CompletedLength = (long)(size * done),
        DownloadSpeed = (long)speed,
        Connections = connections,
        Dir = "/downloads",
        Files = [new(1, "/downloads/" + name, (long)size, (long)(size * done), true, "https://example.org/files/" + name)],
    };

    private Download Bt(string name, (string Path, double Size)[] files, double done, string status, double down, double up, int seeders)
    {
        var total = files.Sum(f => (long)f.Size);
        return new Download
        {
            Gid = Gid(),
            Status = status,
            TotalLength = total,
            CompletedLength = (long)(total * done),
            UploadLength = (long)(total * done * 0.8),
            DownloadSpeed = (long)down,
            UploadSpeed = (long)up,
            Connections = status == "active" ? 12 : 0,
            NumSeeders = seeders,
            Dir = "/downloads",
            IsBt = true,
            BtName = name,
            InfoHash = Convert.ToHexStringLower(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(name))),
            Files = [.. files.Select((f, i) => new DownloadFile(i + 1, "/downloads/" + f.Path, (long)f.Size, (long)(f.Size * done), true, null))],
        };
    }

    private void Add(Download d) => _tasks.Add(d);

    private Task Change(string gid, Func<Download, Download> change)
    {
        var i = _tasks.FindIndex(t => t.Gid == gid);
        if (i >= 0)
        {
            _tasks[i] = change(_tasks[i]);
        }

        Publish();
        return Task.CompletedTask;
    }

    /// <summary>每秒：下载中的往前走，下完的转成完成（BT 转成做种），等待中的在空位里开始；取种子信息的过几秒变成真任务。</summary>
    private void Tick()
    {
        for (var i = 0; i < _tasks.Count; i++)
        {
            var t = _tasks[i];
            if (t.IsMetadata && t.Status == "active" && _random.Next(8) == 0)
            {
                var real = Bt("Elephants Dream", [("Elephants Dream/ed_hd.mp4", 820 * MB)], 0, "active", 2 * MB, 64 * 1024, 9);
                _tasks[i] = t with { Status = "complete", FollowedBy = [real.Gid] };
                _tasks.Add(real);
                continue;
            }

            if (!t.IsDownloading || t.TotalLength == 0)
            {
                if (t.IsSeeding)
                {
                    _tasks[i] = t with { UploadSpeed = (long)(t.UploadSpeed * (0.8 + (0.4 * _random.NextDouble()))) + 1024, UploadLength = t.UploadLength + t.UploadSpeed };
                }

                continue;
            }

            var speed = (long)(Math.Max(t.DownloadSpeed, 512 * 1024) * (0.85 + (0.3 * _random.NextDouble())));
            var completed = Math.Min(t.TotalLength, t.CompletedLength + speed);
            t = t with { DownloadSpeed = speed, CompletedLength = completed, Files = [.. t.Files.Select(f => f with { Completed = Math.Min(f.Length, (long)(f.Length * ((double)completed / t.TotalLength))) })] };
            if (completed >= t.TotalLength)
            {
                t = t.IsBt
                    ? t with { DownloadSpeed = 0, Seeder = true }
                    : t with { Status = "complete", DownloadSpeed = 0, Connections = 0 };
                Notice?.Invoke(t, NoticeKind.Complete);
            }

            _tasks[i] = t;
        }

        var running = _tasks.Count(t => t.IsDownloading);
        for (var i = 0; i < _tasks.Count && running < 3; i++)
        {
            if (_tasks[i].Status == "waiting")
            {
                _tasks[i] = _tasks[i] with { Status = "active", DownloadSpeed = 3 * MB, Connections = 8 };
                running++;
            }
        }

        Publish(tick: true);
    }

    /// <summary>tick：每秒那一次才往曲线里记点，点按钮时不记。</summary>
    private void Publish(bool tick = false)
    {
        var active = _tasks.Where(t => t.Status == "active").ToList();
        var waiting = _tasks.Where(t => t.Status is "waiting" or "paused").ToList();
        var stopped = Snapshot.WithoutFinishedMetadata(_tasks.Where(t => t.Group == Group.Done).Reverse());
        var stat = new GlobalStat(active.Sum(t => t.DownloadSpeed), active.Sum(t => t.UploadSpeed), active.Count, waiting.Count, stopped.Count);
        Current = new Snapshot(stat, active, waiting, stopped);
        if (tick)
        {
            History.Add(stat.DownloadSpeed, stat.UploadSpeed);
        }

        Changed?.Invoke();
    }
}
