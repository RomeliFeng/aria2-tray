using Aria2Tray.Aria2;
using Aria2Tray.Model;
using Aria2Tray.Native;
using Aria2Tray.Ui;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using static Aria2Tray.Native.Win32;

namespace Aria2Tray;

/// <summary>
/// 托盘常驻：没有主窗口，只有托盘图标、弹出面板和设置窗口。只允许一个实例。
/// 参数：--demo 用内置假数据，不连 aria2；--debug 打开文字调试通道（见 FlyoutWindow.EnableDebug）；
/// --lang=zh / --lang=en 只在这次运行里固定界面语言，不改设置。
/// </summary>
public partial class App : Application
{
    private const int MenuOpen = 1, MenuAdd = 2, MenuResumeAll = 3, MenuPauseAll = 4, MenuSettings = 5, MenuStartup = 6, MenuExit = 9;
    private const int SM_CXICON = 11;

    private readonly bool _demo = Environment.GetCommandLineArgs().Contains("--demo");
    private Mutex? _singleInstance;
    private Settings _settings = new();
    private TrayIcon? _tray;
    private FlyoutWindow? _flyout;
    private SettingsWindow? _settingsWindow;
    private IBackend? _backend;

    /// <summary>托盘图标当前画的进度（百分之几；-1 是空闲图标），变了才重画。</summary>
    private int _drawnPercent = -1;
    private string _tooltip = "Aria2 Tray";
    private nint _balloonIcon;

    public App()
    {
        InitializeComponent();

        // 没人处理的异常写进 %LOCALAPPDATA%\Aria2Tray\crash.log（程序照样退出），排查崩溃用。
        UnhandledException += (_, e) =>
        {
            try
            {
                Directory.CreateDirectory(Settings.DataDir);
                File.AppendAllText(Path.Combine(Settings.DataDir, "crash.log"), $"{DateTime.Now:O}\n{e.Exception}\n{e.Message}\n\n");
            }
            catch (IOException)
            {
            }
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _singleInstance = new Mutex(true, @"Local\Aria2Tray.SingleInstance", out var created);
        if (!created)
        {
            Exit();
            return;
        }

        _settings = Settings.Load();
        L.Init(Environment.GetCommandLineArgs().FirstOrDefault(a => a.StartsWith("--lang=", StringComparison.Ordinal))?[7..] ?? _settings.Language);

        _flyout = new FlyoutWindow(_settings);
        _flyout.SettingsRequested += OpenSettings;
        _flyout.Prime();

        _tray = new TrayIcon(_tooltip, (light, size) => TrayArt.Render(light, size, _drawnPercent < 0 ? null : _drawnPercent / 100.0));
        _tray.Invoked += () => _flyout.Toggle(_tray.GetRect());
        _tray.ContextMenuRequested += ShowTrayMenu;
        _tray.BalloonClicked += () => _flyout.ShowFlyout(_tray.GetRect());
        if (Environment.GetCommandLineArgs().Contains("--debug"))
        {
            _flyout.EnableDebug(() => new { tooltip = _tooltip, percent = _drawnPercent });
        }

        var iconSize = GetSystemMetricsForDpi(SM_CXICON, GetDpiForSystem());
        _balloonIcon = LoadImage(0, Path.Combine(AppContext.BaseDirectory, "Assets", "Aria2Tray.ico"), IMAGE_ICON, iconSize, iconSize, LR_LOADFROMFILE);

        _ = ConnectAsync();
        if (!_demo && !_settings.IsConfigured)
        {
            OpenSettings();
        }
    }

    /// <summary>按当前设置换一个后端：演示数据，或连 aria2。</summary>
    private async Task ConnectAsync()
    {
        var old = _backend;
        _backend = null;
        _flyout!.SetBackend(null);
        if (old is not null)
        {
            old.Changed -= UpdateTray;
            old.Notice -= OnNotice;
            await old.DisposeAsync();
        }

        var ui = DispatcherQueue.GetForCurrentThread();
        IBackend? backend = _demo
            ? new DemoBackend(ui)
            : _settings.IsConfigured && Aria2Connection.ToWebSocketUri(_settings.Url) is { } uri
                ? new Aria2Session(ui, uri, _settings.GetSecret())
                : null;
        _backend = backend;
        if (backend is not null)
        {
            backend.Changed += UpdateTray;
            backend.Notice += OnNotice;
        }

        _flyout.SetBackend(backend);
        UpdateTray();
        backend?.Start();
    }

    /// <summary>托盘提示：两行，速度和下载中的个数；进度环按总进度画，变化不到 1% 不重画。</summary>
    private void UpdateTray()
    {
        if (_tray is null)
        {
            return;
        }

        var b = _backend;
        int percent;
        if (b is null)
        {
            _tooltip = "Aria2 Tray";
            percent = -1;
        }
        else if (b.Status != ConnectionStatus.Connected)
        {
            _tooltip = b.Status switch
            {
                ConnectionStatus.AuthFailed => L.T("aria2 的密钥不对", "aria2 secret is wrong"),
                ConnectionStatus.Connecting => L.T("正在连接 aria2…", "Connecting to aria2…"),
                _ => L.T("连不上 aria2", "Can't reach aria2"),
            };
            percent = -1;
        }
        else
        {
            var s = b.Current;
            var n = s.DownloadingCount;
            _tooltip = $"↓ {Format.Speed(s.Stat.DownloadSpeed)}  ↑ {Format.Speed(s.Stat.UploadSpeed)}\n"
                + (n > 0 ? L.T($"{n} 个下载中", n == 1 ? "1 download" : $"{n} downloads") : L.T("没有下载中的任务", "Nothing downloading"));
            percent = s.TotalProgress is { } p ? (int)Math.Floor(p * 100) : -1;
        }

        _tray.SetTooltip(_tooltip);
        if (percent != _drawnPercent)
        {
            _drawnPercent = percent;
            _tray.Redraw();
        }
    }

    private void OnNotice(Download d, NoticeKind kind)
    {
        if (!_settings.Notify || _tray is null)
        {
            return;
        }

        var name = Describe.Name(d);
        if (kind == NoticeKind.Complete)
        {
            _tray.ShowBalloon(L.T("下载完成", "Download finished"), name, _balloonIcon);
        }
        else
        {
            _tray.ShowBalloon(L.T("下载出错", "Download failed"), d.ErrorMessage is { } msg ? $"{name}\n{msg}" : name, _balloonIcon);
        }
    }

    private void OpenSettings()
    {
        if (_settingsWindow is not null)
        {
            _settingsWindow.Activate();
            return;
        }

        _settingsWindow = new SettingsWindow(_settings, _backend?.DefaultDir);
        _settingsWindow.Saved += reconnect =>
        {
            L.Init(_settings.Language);
            _flyout!.ApplySettings();
            UpdateTray();
            if (reconnect)
            {
                _ = ConnectAsync();
            }
        };
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Activate();
    }

    private void ShowTrayMenu(int x, int y)
    {
        var connected = _backend?.Status == ConnectionStatus.Connected;
        var cmd = _tray!.ShowMenu(x, y,
        [
            (MenuOpen, L.T("打开面板", "Open panel"), false, true),
            (MenuAdd, L.T("新建下载…", "New download…"), false, connected),
            (0, null, false, true),
            (MenuResumeAll, L.T("全部开始", "Resume all"), false, connected),
            (MenuPauseAll, L.T("全部暂停", "Pause all"), false, connected),
            (0, null, false, true),
            (MenuSettings, L.T("设置…", "Settings…"), false, true),
            (MenuStartup, L.T("开机时启动", "Start with Windows"), Settings.StartWithWindows, true),
            (0, null, false, true),
            (MenuExit, L.T("退出", "Exit"), false, true),
        ]);
        switch (cmd)
        {
            case MenuOpen:
                _flyout!.ShowFlyout(_tray.GetRect());
                break;
            case MenuAdd:
                _flyout!.ShowFlyout(_tray.GetRect());
                _flyout.OpenAdd();
                break;
            case MenuResumeAll when _backend is { } b:
                _ = Safe(b.ResumeAllAsync());
                break;
            case MenuPauseAll when _backend is { } b:
                _ = Safe(b.PauseAllAsync());
                break;
            case MenuSettings:
                OpenSettings();
                break;
            case MenuStartup:
                Settings.StartWithWindows = !Settings.StartWithWindows;
                break;
            case MenuExit:
                Quit();
                break;
        }
    }

    /// <summary>托盘菜单发的命令：失败了不弹窗（面板开着时下一轮刷新会显示真实状态）。</summary>
    private static async Task Safe(Task t)
    {
        try
        {
            await t;
        }
        catch (Exception ex) when (ex is Aria2Exception or IOException or TimeoutException or InvalidOperationException)
        {
        }
    }

    private void Quit()
    {
        _tray?.Dispose();
        _tray = null;
        if (_balloonIcon != 0)
        {
            DestroyIcon(_balloonIcon);
        }

        _singleInstance?.ReleaseMutex();
        Exit();
    }
}
