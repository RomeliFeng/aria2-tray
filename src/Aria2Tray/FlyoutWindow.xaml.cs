using System.Runtime.InteropServices;
using System.Text.Json;
using Aria2Tray.Aria2;
using Aria2Tray.Model;
using Aria2Tray.Ui;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Storage;
using WinRT.Interop;
using static Aria2Tray.Native.Win32;

namespace Aria2Tray;

/// <summary>
/// 点托盘图标弹出的面板：贴着任务栏，失焦就收起，尺寸固定（设置里的宽高），内容超出时在面板里滚动。
/// 这里管窗口、顶栏、页面切换、拖入种子和调试通道；页面内容由 <see cref="PanelView"/> 画。见 docs/design.md 第 4、5 节。
/// </summary>
internal sealed partial class FlyoutWindow : Window, IPanelActions
{
    private const double EdgeGap = 12;

    private static readonly JsonSerializerOptions DebugJson = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly Settings _settings;
    private readonly PanelView _view;
    private readonly nint _hwnd;
    private readonly AddDraft _draft = new();
    private IBackend? _backend;
    private Page _page;
    private string? _detailGid;
    private long _hiddenAt;
    private RECT? _anchor;
    private int _modal;
    private string? _lastViewKey;
    private Notice? _notice;
    private string? _bannerKey;
    private Kit _kit = null!;
    private TextBlock? _downText;
    private TextBlock? _upText;
    private Button? _allButton;
    private FileSystemWatcher? _debugWatcher;

    public FlyoutWindow(Settings settings)
    {
        InitializeComponent();
        _settings = settings;
        _view = new PanelView(this) { SortBy = settings.SortBy, SortDescending = settings.SortDescending };
        _hwnd = WindowNative.GetWindowHandle(this);

        SystemBackdrop = new DesktopAcrylicBackdrop();
        var presenter = OverlappedPresenter.Create();
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.IsResizable = false;
        presenter.IsAlwaysOnTop = true;
        presenter.SetBorderAndTitleBar(true, false);
        AppWindow.SetPresenter(presenter);
        AppWindow.IsShownInSwitchers = false;
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "Aria2Tray.ico"));
        AppWindow.Closing += (_, e) =>
        {
            e.Cancel = true;
            HideFlyout();
        };
        var round = 2; // DWMWCP_ROUND
        DwmSetWindowAttribute(_hwnd, 33 /* DWMWA_WINDOW_CORNER_PREFERENCE */, ref round, sizeof(int));

        // 选种子的对话框开着时不收（对话框是另一个窗口，面板会失焦）。
        Activated += (_, e) =>
        {
            if (e.WindowActivationState == WindowActivationState.Deactivated && !Pinned && _modal == 0)
            {
                HideFlyout();
            }
        };

        // Esc：二级页回主界面，主界面收起面板。
        Root.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Escape)
            {
                if (_page != Page.Main)
                {
                    GoMain();
                }
                else
                {
                    HideFlyout();
                }

                e.Handled = true;
            }
        };

        Root.DragOver += OnDragOver;
        Root.Drop += OnDrop;

        // 深浅色、强调色变了：整个重画。
        Root.ActualThemeChanged += (_, _) => Render();
        Palette.AccentChanged += () => DispatcherQueue.TryEnqueue(Render);
        Render();
    }

    private enum Page
    {
        Main,
        Detail,
        Add,
    }

    public event Action? SettingsRequested;

    /// <summary>失焦也不收起（调试通道的 show 命令用）。</summary>
    public bool Pinned { get; set; }

    /// <summary>换后端（启动、设置改了、演示模式）。旧的由调用方释放。</summary>
    public void SetBackend(IBackend? backend)
    {
        if (_backend is not null)
        {
            _backend.Changed -= OnBackendChanged;
        }

        _backend = backend;
        if (backend is not null)
        {
            backend.Changed += OnBackendChanged;
            backend.PanelOpen = AppWindow.IsVisible;
        }

        _draft.DirFilled = false;
        _page = Page.Main;
        Render();
    }

    /// <summary>启动时先激活一次再藏起来，第一次弹出就是现成的窗口。</summary>
    public void Prime()
    {
        Reposition();
        AppWindow.Move(new PointInt32(-32000, -32000));
        Activate();
        AppWindow.Hide();
    }

    public void Toggle(RECT? anchor)
    {
        if (AppWindow.IsVisible)
        {
            HideFlyout();
            return;
        }

        // 面板开着时再点图标：焦点先跑到任务栏，面板已经因失焦收起，这一下不该又把它弹出来。
        if (Environment.TickCount64 - _hiddenAt < 300)
        {
            return;
        }

        ShowFlyout(anchor);
    }

    public void ShowFlyout(RECT? anchor)
    {
        _anchor = anchor;
        Reposition();
        if (_backend is not null)
        {
            _backend.PanelOpen = true;
        }

        Render();
        AppWindow.Show(true);
        Activate();
        SetForegroundWindow(_hwnd);
        Root.Focus(FocusState.Programmatic);
    }

    /// <summary>收起后回到主界面，下次弹出时它已经画好。</summary>
    public void HideFlyout()
    {
        if (AppWindow.IsVisible)
        {
            AppWindow.Hide();
            _hiddenAt = Environment.TickCount64;
        }

        if (_backend is not null && !Pinned)
        {
            _backend.PanelOpen = false;
        }

        // 横幅里的消息说的是这次打开面板时做的事，收起就清掉。
        if (!Pinned)
        {
            _notice = null;
            UpdateBanner();
        }

        if (!Pinned && _page != Page.Main)
        {
            GoMain();
        }
    }

    /// <summary>设置改了：尺寸重新算，语言可能变了要重画。</summary>
    public void ApplySettings()
    {
        if (AppWindow.IsVisible)
        {
            Reposition();
        }

        Render();
    }

    /// <summary>弹出面板并进新建下载页（托盘菜单、拖入用）。</summary>
    public void ShowAdd()
    {
        ShowFlyout(_anchor);
        OpenAdd();
    }

    // ---------------- 页面 ----------------

    private void GoMain()
    {
        _page = Page.Main;
        _detailGid = null;
        if (_backend is not null)
        {
            _backend.DetailGid = null;
        }

        Render();
    }

    public void OpenDetail(Download d)
    {
        _page = Page.Detail;
        _detailGid = d.Gid;
        if (_backend is not null)
        {
            _backend.DetailGid = d.Gid;
            _backend.PanelOpen = true;
        }

        Render();
    }

    public void Rerender() => Render();

    public void OpenAdd()
    {
        _page = Page.Add;
        Render();
        _ = PrefillAsync();
    }

    /// <summary>新建下载页：剪贴板里是链接就先填上；「保存到」按 aria2 的 dir 填一次。</summary>
    private async Task PrefillAsync()
    {
        var changed = false;
        if (_draft.Links.Trim().Length == 0)
        {
            try
            {
                var content = Clipboard.GetContent();
                if (content.Contains(StandardDataFormats.Text))
                {
                    var text = (await content.GetTextAsync()).Trim();
                    var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    if (lines.Length > 0 && lines.All(LooksLikeLink))
                    {
                        _draft.Links = string.Join("\n", lines);
                        changed = true;
                    }
                }
            }
            catch (COMException)
            {
                // 剪贴板被别的程序占着。
            }
        }

        if (!_draft.DirFilled && _backend is { } b)
        {
            try
            {
                _draft.Dir = await b.GetDefaultDirAsync() ?? "";
                _draft.DirFilled = true;
                changed = true;
            }
            catch (Exception ex) when (IsCommandError(ex))
            {
            }
        }

        if (changed && _page == Page.Add)
        {
            Render();
        }
    }

    private static bool LooksLikeLink(string s) =>
        s.StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase)
        || (Uri.TryCreate(s, UriKind.Absolute, out var u) && u.Scheme is "http" or "https" or "ftp" or "sftp");

    // ---------------- 操作 ----------------

    public void PauseOrResume(Download d) => Run(b => d.CanResume ? b.ResumeAsync(d.Gid) : b.PauseAsync(d.Gid));

    public void Remove(Download d)
    {
        Run(b => b.RemoveAsync(d));
        if (_page == Page.Detail)
        {
            GoMain();
        }
    }

    public void Purge() => Run(b => b.PurgeAsync());

    public void Move(Download d, bool top) => Run(b => b.MoveAsync(d.Gid, top));

    public void Copy(string text)
    {
        var data = new DataPackage();
        data.SetText(text);
        try
        {
            Clipboard.SetContent(data);
            Clipboard.Flush();
        }
        catch (COMException)
        {
            ShowNotice(L.T("剪贴板被别的程序占着，没复制上。", "The clipboard is busy. Nothing was copied."));
        }
    }

    public void MenuOpenChanged(bool open) => _modal = Math.Max(0, _modal + (open ? 1 : -1));

    /// <summary>
    /// size 是显示尺寸（DIP），向 Windows 要图时乘上屏幕缩放：200% 的屏幕上按 DIP 要，图会被放大两倍，糊（2026-10-18 用户发现）。
    /// </summary>
    public Task<ImageSource?> ThumbnailAsync(Download d, uint size) =>
        Describe.RemotePath(d) is { } remote && _settings.ToLocal(remote, _backend?.DefaultDir) is { } local
            ? Thumbnails.GetAsync(local, (uint)Math.Ceiling(size * (Root.XamlRoot?.RasterizationScale ?? 1)), d.Status)
            : Task.FromResult<ImageSource?>(null);

    /// <summary>
    /// 打开下载到的文件，或在资源管理器里选中它。aria2 给的是它那台机器上的路径，按设置里的「文件位置」换成本机路径（一般是 NAS 的共享文件夹）。
    /// 查文件在不在放到后台线程：网络路径连不上时要等好几秒。
    /// </summary>
    public void Open(Download d, bool reveal) => _ = OpenAsync(d, reveal, dryRun: false);

    /// <summary>dryRun：只走到「文件在不在」为止，不真打开（调试通道 open 用，2026-10-18 真打开过一次用户的文件）。返回本机路径和在不在。</summary>
    private async Task<(string? Local, bool Exists)> OpenAsync(Download d, bool reveal, bool dryRun)
    {
        if (Describe.RemotePath(d) is not { } remote)
        {
            return (null, false);
        }

        var aria2Dir = _backend?.DefaultDir;
        if (_settings.ToLocal(remote, aria2Dir) is not { } local)
        {
            var from = _settings.EffectiveRemoteDir(aria2Dir);
            ShowNotice(
                _settings.LocalDir.Trim().Length == 0 || from.Length == 0
                    ? L.T("要打开文件，先在设置里填好这台电脑上的下载位置。", "To open files, set the download folder on this computer in Settings.")
                    : L.T($"这个文件不在 {from} 下面：{remote}", $"This file isn't under {from}: {remote}"),
                L.T("打开设置", "Open settings"),
                () =>
                {
                    HideFlyout();
                    SettingsRequested?.Invoke();
                });
            return (null, false);
        }

        var exists = await Task.Run(() => File.Exists(local) || Directory.Exists(local));
        if (!exists)
        {
            ShowNotice(L.T("找不到：", "Not found: ") + local);
            return (local, false);
        }

        if (dryRun)
        {
            return (local, true);
        }

        try
        {
            if (reveal)
            {
                System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{local}\"");
            }
            else
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(local) { UseShellExecute = true });
            }

            HideFlyout();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            ShowNotice(ex.Message);
        }

        return (local, true);
    }

    /// <summary>整组操作：逐个发命令，第一个失败就停下，在横幅里说。</summary>
    public void GroupAction(IReadOnlyList<Download> items, GroupCommand command) => Run(async b =>
    {
        foreach (var d in items)
        {
            await (command switch
            {
                GroupCommand.Pause => b.PauseAsync(d.Gid),
                GroupCommand.Resume => b.ResumeAsync(d.Gid),
                _ => b.RemoveAsync(d),
            });
        }
    });

    private void PauseOrResumeAll(bool pause) => Run(b => pause ? b.PauseAllAsync() : b.ResumeAllAsync());

    private static bool IsCommandError(Exception ex) =>
        ex is Aria2Exception or IOException or TimeoutException or InvalidOperationException or OperationCanceledException or System.Net.WebSockets.WebSocketException;

    /// <summary>发命令；失败时在横幅里说。</summary>
    private void Run(Func<IBackend, Task> command)
    {
        if (_backend is not { } b)
        {
            return;
        }

        _ = Go();
        async Task Go()
        {
            try
            {
                await command(b);
            }
            catch (Exception ex) when (IsCommandError(ex))
            {
                ShowNotice(ex.Message);
            }
        }
    }

    /// <summary>横幅里的一条消息：一句话，可以带一个动作按钮。</summary>
    private sealed record Notice(string Text, string? ActionText, Action? Action);

    /// <summary>在横幅里说一件事（命令失败、要用户去做的事），不自己消失，点 ✕ 关掉；面板收起时也清掉。</summary>
    private void ShowNotice(string text, string? actionText = null, Action? action = null)
    {
        _notice = new Notice(text, actionText, action);
        UpdateBanner();
    }

    private async void PickTorrent()
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        InitializeWithWindow.Initialize(picker, _hwnd);
        picker.FileTypeFilter.Add(".torrent");
        _modal++;
        IReadOnlyList<StorageFile> files;
        try
        {
            files = await picker.PickMultipleFilesAsync();
        }
        finally
        {
            _modal--;
        }

        await AddTorrentFiles(files);
        Activate();
    }

    private async Task AddTorrentFiles(IEnumerable<IStorageItem> items)
    {
        foreach (var f in items.OfType<StorageFile>().Where(f => f.FileType.Equals(".torrent", StringComparison.OrdinalIgnoreCase)))
        {
            if (_draft.Torrents.All(t => t.Name != f.Name))
            {
                var buffer = await FileIO.ReadBufferAsync(f);
                _draft.Torrents.Add((f.Name, System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.ToArray(buffer)));
            }
        }

        if (_page == Page.Add)
        {
            Render();
        }
    }

    /// <summary>每行链接一个 addUri，每个种子一个 addTorrent；全部成功回主界面，失败的留在框里，按钮上面写原因。</summary>
    private async void Submit()
    {
        if (_backend is not { } b || _draft.Busy)
        {
            return;
        }

        var links = _draft.Links.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        if (links.Count == 0 && _draft.Torrents.Count == 0)
        {
            _draft.Error = L.T("填上链接，或者添加种子文件。", "Enter a link or add a torrent file.");
            Render();
            return;
        }

        _draft.Busy = true;
        _draft.Error = null;
        Render();
        var dir = _draft.Dir.Trim();
        var failedLinks = new List<string>();
        var errors = new List<string>();
        foreach (var link in links)
        {
            try
            {
                await b.AddUriAsync(link, dir.Length > 0 ? dir : null);
            }
            catch (Exception ex) when (IsCommandError(ex))
            {
                failedLinks.Add(link);
                errors.Add(ex.Message);
            }
        }

        foreach (var t in _draft.Torrents.ToList())
        {
            try
            {
                await b.AddTorrentAsync(t.Data, dir.Length > 0 ? dir : null);
                _draft.Torrents.Remove(t);
            }
            catch (Exception ex) when (IsCommandError(ex))
            {
                errors.Add($"{t.Name}：{ex.Message}");
            }
        }

        _draft.Busy = false;
        _draft.Links = string.Join("\n", failedLinks);
        if (errors.Count == 0)
        {
            _draft.Clear();
            GoMain();
            return;
        }

        _draft.Error = L.T("没加上：", "Couldn't add: ") + string.Join("；", errors.Distinct().Take(3));
        Render();
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems) || e.DataView.Contains(StandardDataFormats.Text) || e.DataView.Contains(StandardDataFormats.WebLink))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
        }
    }

    /// <summary>拖进来的种子文件、链接都进新建下载页。</summary>
    private async void OnDrop(object sender, DragEventArgs e)
    {
        var deferral = e.GetDeferral();
        try
        {
            if (e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                await AddTorrentFiles(await e.DataView.GetStorageItemsAsync());
            }
            else if (e.DataView.Contains(StandardDataFormats.WebLink))
            {
                AppendLink((await e.DataView.GetWebLinkAsync()).AbsoluteUri);
            }
            else if (e.DataView.Contains(StandardDataFormats.Text))
            {
                foreach (var line in (await e.DataView.GetTextAsync()).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(LooksLikeLink))
                {
                    AppendLink(line);
                }
            }
        }
        finally
        {
            deferral.Complete();
        }

        OpenAdd();
    }

    private void AppendLink(string link) => _draft.Links = _draft.Links.Trim().Length == 0 ? link : _draft.Links.TrimEnd() + "\n" + link;

    // ---------------- 画界面 ----------------

    private void OnBackendChanged()
    {
        if (_backend is not { Ready: true } b || b.Status is ConnectionStatus.AuthFailed)
        {
            Render();
            return;
        }

        // 页面已经画好：原地更新顶栏的速度和页面内容；画不了（组成变了、连接状态变了）才整页重画。
        if (Scroller.Visibility != Visibility.Visible || (_downText is null && _page == Page.Main))
        {
            Render();
            return;
        }

        UpdateBanner();
        UpdateHeader(b.Current);
        switch (_page)
        {
            case Page.Main when !_view.TryUpdateMain(b.Current, b.History):
            case Page.Detail when CurrentDetail() is not { } d || !_view.TryUpdateDetail(d):
                Render();
                break;
        }
    }

    /// <summary>详情页的任务：带文件列表的那份，还没取到就用列表里的。都没有了（被清除）就是 null。</summary>
    private Download? CurrentDetail() =>
        _backend is not { } b || _detailGid is null ? null
        : b.Detail is { } d && d.Gid == _detailGid ? d
        : b.Current.All.FirstOrDefault(x => x.Gid == _detailGid);

    private void Render()
    {
        _kit = new Kit(Palette.For(Root.ActualTheme));
        _view.K = _kit;

        // 换了一套颜色（深浅色、强调色），横幅也要按新颜色重画。
        _bannerKey = null;
        if (_page == Page.Detail && CurrentDetail() is null && _backend?.Ready == true)
        {
            _page = Page.Main;
            _detailGid = null;
        }

        RenderHeader();
        if (_backend is null || !_backend.Ready || _backend.Status == ConnectionStatus.AuthFailed)
        {
            ShowPlaceholderForState();
            return;
        }

        Placeholder.Visibility = Visibility.Collapsed;
        Scroller.Visibility = Visibility.Visible;
        UpdateBanner();

        // 同一页重画保持滚动位置；换了页才回到顶部。
        var viewKey = $"{_page}|{_detailGid}";
        var offset = viewKey == _lastViewKey ? Scroller.VerticalOffset : 0;
        _lastViewKey = viewKey;
        Body.Children.Clear();
        switch (_page)
        {
            case Page.Detail when CurrentDetail() is { } d:
                _view.BuildDetail(Body, d);
                break;
            case Page.Add:
                _view.BuildAdd(Body, _draft, PickTorrent, Submit);
                break;
            default:
                _view.BuildMain(Body, _backend.Current, _backend.History);
                break;
        }

        UpdateHeader(_backend.Current);
        Body.UpdateLayout();
        Scroller.ChangeView(null, offset, null, disableAnimation: true);
    }

    /// <summary>
    /// 顶栏下面的横幅卡片（2026-10-18 用户嫌原来那行黄字不行，选了卡片）：
    /// 断线时写「连接断了，正在重连」，没有 ✕，连上了自己消失；其余是 <see cref="ShowNotice"/> 的消息，带 ✕，不自己消失。
    /// 每秒都会调到这里，内容没变就不重画，按钮的悬停不会每秒闪。断线时内容区不能点，数据保留最后一次的值。
    /// </summary>
    private void UpdateBanner()
    {
        var connected = _backend?.Status == ConnectionStatus.Connected;
        Body.IsHitTestVisible = connected;
        Body.Opacity = connected ? 1 : 0.6;

        Notice? show = !connected && _backend?.Ready == true
            ? new Notice(L.T("和 aria2 的连接断了，正在重连…", "Lost connection to aria2. Reconnecting…") + (_backend.Error is { } err ? "\n" + err : ""), null, null)
            : _notice;
        var key = show is null ? null : $"{!connected}|{show.Text}|{show.ActionText}";
        if (key == _bannerKey)
        {
            return;
        }

        _bannerKey = key;
        Banner.Child = show is null ? null : _kit.Banner(show.Text, show.ActionText, show.Action, connected ? () =>
        {
            _notice = null;
            UpdateBanner();
        }
        : null);
        Banner.Visibility = show is null ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>顶栏：主界面是全局速度、新建、全部暂停 / 全部开始；二级页是返回和标题。</summary>
    private void RenderHeader()
    {
        Header.Children.Clear();
        Header.ColumnDefinitions.Clear();
        Header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _downText = _upText = null;
        _allButton = null;
        if (_backend?.Ready != true || _backend.Status == ConnectionStatus.AuthFailed)
        {
            var title = _kit.Text("Aria2 Tray", 15, null, strong: true);
            title.Margin = new Thickness(4, 0, 0, 0);
            Header.Children.Add(title);
            return;
        }

        if (_page != Page.Main)
        {
            var back = _kit.GhostButton(Icons.Icon(Icons.Back, 14), GoMain, L.T("返回", "Back"));
            Header.Children.Add(back);
            var title = _kit.Text(_page == Page.Add ? L.T("新建下载", "New download") : L.T("详情", "Details"), 15, null, strong: true);
            title.Margin = new Thickness(4, 0, 0, 0);
            Grid.SetColumn(title, 1);
            Header.Children.Add(title);
            return;
        }

        // 全局速度：下载大、上传小。
        var speeds = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14, Margin = new Thickness(4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        var down = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
        down.Children.Add(Icons.Icon(Icons.Down, 13, _kit.P.Accent));
        _downText = _kit.Text("", 17, null, strong: true);
        down.Children.Add(_downText);
        var up = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
        up.Children.Add(Icons.Icon(Icons.Up, 11, _kit.P.Text2));
        _upText = _kit.Text("", 13, _kit.P.Text2);
        up.Children.Add(_upText);
        speeds.Children.Add(down);
        speeds.Children.Add(up);
        Grid.SetColumnSpan(speeds, 2);
        Header.Children.Add(speeds);

        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        var sort = _kit.GhostButton(Icons.Icon(Icons.Sort, 14), () => { }, L.T("排序方式", "Sort by"));
        sort.Flyout = SortMenu();
        right.Children.Add(sort);
        right.Children.Add(_kit.GhostButton(Icons.Icon(Icons.Add, 14), OpenAdd, L.T("新建下载", "New download")));
        _allButton = _kit.GhostButton(Icons.Icon(Icons.Pause, 14), () => PauseOrResumeAll((string?)_allButton?.Tag == "pause"));
        right.Children.Add(_allButton);
        Grid.SetColumn(right, 2);
        Header.Children.Add(right);
    }

    /// <summary>
    /// 排序菜单（2026-10-18 用户要求）：默认顺序、名字、大小、进度、速度，下面一段升序 / 降序；当前的打勾。改了存进设置，整页重排。
    /// 菜单限制在面板窗口里弹，不开独立窗口，免得抢走焦点让面板收起。
    /// </summary>
    private MenuFlyout SortMenu()
    {
        var menu = new MenuFlyout { Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedRight, ShouldConstrainToRootBounds = true, MenuFlyoutPresenterStyle = _kit.P.MenuStyle };
        menu.Opened += (_, _) => _modal++;
        menu.Closed += (_, _) => _modal = Math.Max(0, _modal - 1);
        void Pick(string? by, bool? descending)
        {
            _settings.SortBy = by ?? _settings.SortBy;
            _settings.SortDescending = descending ?? _settings.SortDescending;
            _settings.Save();
            _view.SortBy = _settings.SortBy;
            _view.SortDescending = _settings.SortDescending;
            Render();
        }

        foreach (var (key, text) in new[]
        {
            ("default", L.T("默认顺序", "Default order")),
            ("name", L.T("名字", "Name")),
            ("size", L.T("大小", "Size")),
            ("progress", L.T("进度", "Progress")),
            ("speed", L.T("速度", "Speed")),
        })
        {
            var item = new ToggleMenuFlyoutItem { Text = text, IsChecked = _settings.SortBy == key };
            item.Click += (_, _) => Pick(key, null);
            menu.Items.Add(item);
        }

        menu.Items.Add(new MenuFlyoutSeparator());
        foreach (var (descending, text) in new[] { (false, L.T("升序", "Ascending")), (true, L.T("降序", "Descending")) })
        {
            var item = new ToggleMenuFlyoutItem { Text = text, IsChecked = _settings.SortDescending == descending };
            item.Click += (_, _) => Pick(null, descending);
            menu.Items.Add(item);
        }

        return menu;
    }

    /// <summary>每秒更新顶栏的速度，和「全部暂停 / 全部开始」按哪个。</summary>
    private void UpdateHeader(Snapshot s)
    {
        if (_downText is null || _upText is null || _allButton is null)
        {
            return;
        }

        _downText.Text = Format.Speed(s.Stat.DownloadSpeed);
        _upText.Text = Format.Speed(s.Stat.UploadSpeed);
        var running = s.Active.Count > 0 || s.Waiting.Any(d => d.Status == "waiting");
        var paused = s.Waiting.Any(d => d.Status == "paused");
        _allButton.Visibility = running || paused ? Visibility.Visible : Visibility.Collapsed;
        var mode = running ? "pause" : "resume";
        if ((string?)_allButton.Tag != mode)
        {
            _allButton.Tag = mode;
            _allButton.Content = Icons.Icon(running ? Icons.Pause : Icons.Play, 14);
            ToolTipService.SetToolTip(_allButton, running ? L.T("全部暂停", "Pause all") : L.T("全部开始", "Resume all"));
        }
    }

    private void ShowPlaceholderForState()
    {
        Scroller.Visibility = Visibility.Collapsed;
        Banner.Visibility = Visibility.Collapsed;
        Banner.Child = null;
        _bannerKey = null;
        Placeholder.Visibility = Visibility.Visible;
        var (text, loading, button) = _backend?.Status switch
        {
            null => (L.T("还没设置 aria2 的地址。", "aria2 isn't set up yet."), false, true),
            ConnectionStatus.AuthFailed => (L.T("密钥不对，请在设置里重新填。", "The secret is wrong. Update it in Settings."), false, true),
            ConnectionStatus.Disconnected => (L.T("连不上 aria2，正在重试…", "Can't reach aria2. Retrying…") + (_backend.Error is { } err ? "\n" + err : ""), true, true),
            _ => (L.T("正在连接 aria2…", "Connecting to aria2…"), true, false),
        };
        PlaceholderText.Text = text;
        PlaceholderText.Foreground = _kit.P.Text2;
        PlaceholderText.FontSize = 12;
        Loading.IsActive = loading;
        Loading.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
        PlaceholderButton.Visibility = button ? Visibility.Visible : Visibility.Collapsed;
        PlaceholderButton.Style = (Style)Application.Current.Resources["MacButtonStyle"];
        PlaceholderButton.Background = _kit.P.Control;
        PlaceholderButton.Height = 28;
        PlaceholderButton.Padding = new Thickness(12, 0, 12, 0);
        PlaceholderButton.Content = L.T("打开设置", "Open settings");
    }

    private void PlaceholderButton_Click(object sender, RoutedEventArgs e)
    {
        HideFlyout();
        SettingsRequested?.Invoke();
    }

    private void Reposition()
    {
        POINT pt;
        if (_anchor is { } r)
        {
            pt = new POINT { X = (r.Left + r.Right) / 2, Y = (r.Top + r.Bottom) / 2 };
        }
        else
        {
            GetCursorPos(out pt);
        }

        var monitor = MonitorFromPoint(pt, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(monitor, ref info);
        GetDpiForMonitor(monitor, 0, out var dpi, out _);
        var scale = dpi / 96.0;

        var work = info.rcWork;
        var gap = (int)Math.Round(EdgeGap * scale);

        // 尺寸固定，就是设置里的宽高；屏幕放不下时缩到工作区里。
        var width = Math.Min((int)Math.Round(_settings.Width * scale), work.Right - work.Left - (2 * gap));
        var height = Math.Min((int)Math.Round(_settings.Height * scale), work.Bottom - work.Top - (2 * gap));

        // 按整个窗口定尺寸。标题栏藏起来以后，ResizeClient 仍然把原来标题栏那一截算在客户区外面（HA Tray 踩过）。
        AppWindow.Resize(new SizeInt32(width, height));

        // 默认贴右下角；任务栏在左边或上边时贴那一边。
        var size = AppWindow.Size;
        var x = work.Left > info.rcMonitor.Left ? work.Left + gap : work.Right - size.Width - gap;
        var y = work.Top > info.rcMonitor.Top ? work.Top + gap : work.Bottom - size.Height - gap;
        AppWindow.Move(new PointInt32(x, y));
    }

    // ---------------- 调试通道 ----------------

    /// <summary>
    /// --debug：往 %LOCALAPPDATA%\Aria2Tray\debug-cmd.txt 写一行命令，结果写进 debug-result.json。不截图，一律看文字。
    /// 命令见 docs/design.md 第 7 节。
    /// </summary>
    public void EnableDebug(Func<object> trayState)
    {
        Directory.CreateDirectory(Settings.DataDir);
        var input = Path.Combine(Settings.DataDir, "debug-cmd.txt");
        var output = Path.Combine(Settings.DataDir, "debug-result.json");
        var watcher = new FileSystemWatcher(Settings.DataDir, "debug-cmd.txt") { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName, EnableRaisingEvents = true };
        void Run() => DispatcherQueue.TryEnqueue(async () =>
        {
            string cmd;
            try
            {
                await Task.Delay(100);
                cmd = (await File.ReadAllTextAsync(input)).Trim();
            }
            catch (IOException)
            {
                return;
            }

            var verb = cmd.Split(' ', 2)[0];
            var arg = cmd.Length > verb.Length ? cmd[(verb.Length + 1)..].Trim() : "";
            object? report = null;
            switch (verb)
            {
                case "show":
                    Pinned = true;
                    ShowFlyout(null);
                    break;
                case "hide":
                    Pinned = false;
                    HideFlyout();
                    break;
                case "detail" when _backend?.Current.All.FirstOrDefault(d => d.Gid == arg) is { } d:
                    OpenDetail(d);
                    break;
                case "add":
                    OpenAdd();
                    break;
                case "back":
                    GoMain();
                    break;
                case "page" when arg.Split(' ') is [var group, var n] && int.TryParse(n, out var page):
                    _view.SetPage(group, page);
                    Render();
                    break;
                case "sort" when arg.Split(' ') is [var by, ..] && by is "default" or "name" or "size" or "progress" or "speed":
                    _view.SortBy = by;
                    _view.SortDescending = arg.EndsWith(" desc", StringComparison.Ordinal);
                    Render();
                    break;

                // 收起 / 展开分开两个命令，不做切换（命令文件写一次常常触发两次）。
                case "fold" or "unfold" when arg.Length > 0:
                    _view.SetCollapsed(arg, verb == "fold");
                    Render();
                    break;

                // 照「打开文件」走到查文件在不在为止，不真打开；出错时横幅照常出来。
                case "open" when _backend?.Current.All.FirstOrDefault(d => d.Gid == arg) is { } o:
                    var (local, exists) = await OpenAsync(o, reveal: false, dryRun: true);
                    report = new { local, exists };
                    break;
                case "menu" when _backend?.Current.All.FirstOrDefault(d => d.Gid == arg) is { } d:
                    report = new
                    {
                        items = _view.MenuTexts(d),
                        remote = Describe.RemotePath(d),
                        aria2Dir = _backend.DefaultDir,
                        local = Describe.RemotePath(d) is { } r ? _settings.ToLocal(r, _backend.DefaultDir) : null,
                        link = Describe.Link(d) is { } l ? l[..Math.Min(l.Length, 60)] : null,
                    };
                    break;
                case "texts":
                    report = TextReport();
                    break;
                case "layout":
                    report = LayoutReport();
                    break;
                case "align":
                    report = AlignReport();
                    break;
            }

            // 等一轮刷新，详情页的文件列表、新建页的预填都到了再报。
            await Task.Delay(verb is "detail" or "add" or "show" ? 1200 : 0);
            try
            {
                await File.WriteAllTextAsync(output, JsonSerializer.Serialize(DebugDump(cmd, report, trayState()), DebugJson));
            }
            catch (IOException)
            {
            }
        });
        watcher.Changed += (_, _) => Run();
        watcher.Created += (_, _) => Run();
        _debugWatcher = watcher;
    }

    private static IEnumerable<DependencyObject> Walk(DependencyObject d)
    {
        yield return d;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
        {
            foreach (var x in Walk(VisualTreeHelper.GetChild(d, i)))
            {
                yield return x;
            }
        }
    }

    /// <summary>调试用：当前页面（顶栏和内容）上全部文字，去重；带英文单词的列在前面，查中英混杂。提示框的文字另列。</summary>
    private object TextReport()
    {
        Root.UpdateLayout();
        var shown = new List<UIElement> { Header, Banner, Scroller, Placeholder }.Where(e => e.Visibility == Visibility.Visible);
        var all = shown.SelectMany(Walk).ToList();
        var texts = all.OfType<TextBlock>().Select(t => t.Text)
            .Concat(all.OfType<TextBox>().SelectMany(t => new[] { t.Header as string, t.PlaceholderText }))
            .Where(t => !string.IsNullOrWhiteSpace(t) && !t!.Any(c => c is >= '' and <= ''))
            .Select(t => t!)
            .Distinct()
            .ToList();
        var tips = all.Select(ToolTipService.GetToolTip).OfType<string>().Distinct().ToList();
        var english = texts.Where(t => System.Text.RegularExpressions.Regex.IsMatch(t, "[A-Za-z]{2,}")).ToList();
        return new { count = texts.Count, withEnglish = english, others = texts.Except(english), tooltips = tips };
    }

    /// <summary>
    /// 调试用：查三件事——文字有没有被截断（只报截断的）；元素有没有超出面板左右边；文字和按钮之间有没有重叠（不算包含关系）。
    /// 顶栏和整页内容都查，包括滚动到下面才看得见的部分。
    /// </summary>
    private object LayoutReport()
    {
        Root.UpdateLayout();
        Windows.Foundation.Rect R(FrameworkElement f) => f.TransformToVisual(Root).TransformBounds(new Windows.Foundation.Rect(0, 0, f.ActualWidth, f.ActualHeight));
        static bool Hit(Windows.Foundation.Rect a, Windows.Foundation.Rect b) =>
            Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left) > 0.5 && Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top) > 0.5;
        bool Ancestor(DependencyObject a, DependencyObject b)
        {
            for (var x = VisualTreeHelper.GetParent(b); x is not null; x = VisualTreeHelper.GetParent(x))
            {
                if (x == a)
                {
                    return true;
                }
            }

            return false;
        }

        var width = Root.ActualWidth;
        var elements = Walk(Header).Concat(Walk(Banner)).Concat(Walk(Body)).OfType<FrameworkElement>()
            .Where(f => f.Visibility == Visibility.Visible && f.ActualWidth > 0 && f.ActualHeight > 0 && (f is TextBlock || f is Button || f is TextBox))
            .Where(f => VisualTreeHelper.GetParent(f) is not ContentPresenter || f is not TextBlock)
            .Select(f => (Element: f, Rect: R(f)))
            .ToList();

        static string Name(FrameworkElement f) => f switch
        {
            TextBlock t => "字「" + t.Text + "」",
            TextBox => "文本框",
            Button { Content: FontIcon i } b => "按钮 " + (ToolTipService.GetToolTip(b) as string ?? ((int)i.Glyph[0]).ToString("X", System.Globalization.CultureInfo.InvariantCulture)),
            Button b => "按钮 " + (ToolTipService.GetToolTip(b) as string ?? ""),
            _ => f.GetType().Name,
        };

        var trimmed = elements.Where(x => x.Element is TextBlock { IsTextTrimmed: true }).Select(x => Name(x.Element)).ToList();
        var middle = Walk(Header).Concat(Walk(Body)).OfType<MiddleText>().Where(m => m.IsTrimmed).Select(m => m.Inner.Text).ToList();
        var outside = elements.Where(x => x.Rect.Left < -0.5 || x.Rect.Right > width + 0.5).Select(x => $"{Name(x.Element)} 左 {x.Rect.Left:0} 右 {x.Rect.Right:0}（面板宽 {width:0}）").ToList();
        // 顶栏和内容各自比：内容滚上去时本来就在顶栏底下（被滚动区裁掉），不算重叠。
        var inHeader = Walk(Header).Concat(Walk(Banner)).ToHashSet();
        var overlaps = new List<string>();
        for (var i = 0; i < elements.Count; i++)
        {
            for (var j = i + 1; j < elements.Count; j++)
            {
                var (a, ra) = elements[i];
                var (b, rb) = elements[j];
                if (inHeader.Contains(a) == inHeader.Contains(b) && Hit(ra, rb) && !Ancestor(a, b) && !Ancestor(b, a))
                {
                    overlaps.Add($"{Name(a)} 和 {Name(b)} @({ra.X:0},{ra.Y:0})");
                }
            }
        }

        return new { checkedCount = elements.Count, trimmed, middleTrimmed = middle, outside, overlaps = overlaps.Take(20) };
    }

    /// <summary>
    /// 调试用：每个图标和同一行的文字（同一个父容器里的文字块）竖直中心差多少，单位 DIP。只报差 0.5 以上的，和两边的字号、高度。
    /// </summary>
    private object AlignReport()
    {
        Root.UpdateLayout();
        Windows.Foundation.Rect R(FrameworkElement f) => f.TransformToVisual(Root).TransformBounds(new Windows.Foundation.Rect(0, 0, f.ActualWidth, f.ActualHeight));
        var rows = new List<string>();
        var count = 0;
        foreach (var icon in Walk(Header).Concat(Walk(Banner)).Concat(Walk(Body)).OfType<FontIcon>().Where(i => i.ActualHeight > 0))
        {
            if (VisualTreeHelper.GetParent(icon) is not Panel parent)
            {
                continue;
            }

            foreach (var text in parent.Children.OfType<FrameworkElement>()
                .Select(c => c as TextBlock ?? (c as MiddleText)?.Inner)
                .OfType<TextBlock>()
                .Where(t => t.ActualHeight > 0))
            {
                count++;
                var ri = R(icon);
                var rt = R(text);
                var diff = (ri.Y + (ri.Height / 2)) - (rt.Y + (rt.Height / 2));
                if (Math.Abs(diff) >= 0.5)
                {
                    rows.Add($"图标 {(int)icon.Glyph[0]:X}（{icon.FontSize} 号，高 {ri.Height:0.#}）和「{text.Text[..Math.Min(text.Text.Length, 16)]}」（{text.FontSize} 号，高 {rt.Height:0.#}）差 {diff:0.#}");
                }
            }
        }

        return new { pairs = count, misaligned = rows.Distinct().Take(30) };
    }

    private object DebugDump(string cmd, object? report, object tray)
    {
        var b = _backend;
        return new
        {
            cmd,
            report,
            status = b?.Status.ToString(),
            ready = b?.Ready,
            error = b?.Error,
            tray,
            window = new { visible = AppWindow.IsVisible, w = AppWindow.Size.Width, h = AppWindow.Size.Height, theme = Root.ActualTheme.ToString() },
            page = new { page = _page.ToString(), detail = _detailGid, banner = Banner.Visibility == Visibility.Visible ? _bannerKey : null, drawn = _view.Debug },
            stat = b?.Current.Stat,
            history = b is null ? null : new { max = b.History.Max, last = b.History.Down[^1] },
        };
    }
}
