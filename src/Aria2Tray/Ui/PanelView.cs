using Aria2Tray.Aria2;
using Aria2Tray.Model;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace Aria2Tray.Ui;

internal enum GroupCommand
{
    Pause,
    Resume,
    Remove,
}

/// <summary>面板上的操作，由 <see cref="FlyoutWindow"/> 实现。</summary>
internal interface IPanelActions
{
    void OpenDetail(Download d);

    void PauseOrResume(Download d);

    void Remove(Download d);

    void Purge();

    /// <summary>整组操作：逐个暂停、继续，或者移除（停止做种）。</summary>
    void GroupAction(IReadOnlyList<Download> items, GroupCommand command);

    void OpenAdd();

    /// <summary>翻页、展开收起之后重画这一页（保持滚动位置）。</summary>
    void Rerender();

    /// <summary>等待中的任务挪到队首 / 队尾。</summary>
    void Move(Download d, bool top);

    /// <summary>复制到剪贴板。</summary>
    void Copy(string text);

    /// <summary>在这台电脑上打开下载到的文件（reveal：在资源管理器里选中它），按设置里的「文件位置」把 aria2 的路径换成本机路径。</summary>
    void Open(Download d, bool reveal);

    /// <summary>右键菜单开着时面板失焦也不收起。</summary>
    void MenuOpenChanged(bool open);

    /// <summary>下载到的文件的缩略图（size 是长边像素）；没设「文件位置」、文件不在、不是能出缩略图的文件时是 null。</summary>
    Task<Microsoft.UI.Xaml.Media.ImageSource?> ThumbnailAsync(Download d, uint size);
}

/// <summary>
/// 面板的页面内容：主界面（速度曲线、按组排的任务卡片）、详情页、新建下载（PanelView.Add.cs）。布局见 docs/design.md 第 4 节。
/// 每秒的数据更新尽量原地改：任务的组成和顺序没变时只改卡片上的字和进度条，变了才整页重建。
/// </summary>
internal sealed partial class PanelView(IPanelActions actions)
{
    private readonly Dictionary<string, TaskCard> _cards = [];
    private SpeedChart? _chart;
    private string? _mainKey;
    private DetailParts? _detail;

    public Kit K { get; set; } = null!;

    /// <summary>调试通道用：当前页画了什么。</summary>
    public object? Debug { get; private set; }

    public void Reset()
    {
        _cards.Clear();
        _chart = null;
        _mainKey = null;
        _detail = null;
    }

    public static bool InsideButton(DependencyObject? d, DependencyObject stop)
    {
        for (; d is not null && d != stop; d = VisualTreeHelper.GetParent(d))
        {
            if (d is ButtonBase)
            {
                return true;
            }
        }

        return false;
    }

    // ---------------- 主界面 ----------------

    /// <summary>每组一页最多几张卡片，多了分页（2026-10-18 用户要求：真实数据里做种和已完成各有几十个；先定 5 个，用户嫌少，改成 10 个）。</summary>
    public const int PageSize = 10;

    private readonly Dictionary<string, int> _pages = [];

    /// <summary>
    /// 每组收起了没有。每组的标题都能点着收起 / 展开（2026-10-18 用户发现只有「做种中」能收，点「已完成」没反应）；
    /// 默认只有「做种中」收着。程序运行期间记着。
    /// </summary>
    private readonly Dictionary<string, bool> _collapsed = new() { ["seeding"] = true };
    private TextBlock? _seedingTitle;

    /// <summary>调试通道用：某一组翻到第几页（从 1 起）。</summary>
    public void SetPage(string group, int page) => _pages[group] = page - 1;

    /// <summary>调试通道用：收起 / 展开某一组。</summary>
    public void SetCollapsed(string group, bool collapsed) => _collapsed[group] = collapsed;

    private sealed record TaskGroup(string Key, string Title, IReadOnlyList<Download> Items);

    /// <summary>排序方式（设置里存着，见 Settings.SortBy）：default、name、size、progress、speed。每组各自按它排。</summary>
    public string SortBy { get; set; } = "default";

    public bool SortDescending { get; set; }

    /// <summary>
    /// 上次整页重建时排出来的先后（gid → 名次）。每秒原地更新时照这个顺序，不重新排：按速度、进度排时顺序每秒都会变，
    /// 卡片跟着跳就没法点，提示框也弹不出来。整页重建（打开面板、翻页、收起展开、改排序、任务增减）时才按最新的值重排。
    /// </summary>
    private Dictionary<string, int> _rank = [];
    private bool _resort;

    /// <summary>分组：下载中 → 等待中 → 做种中 → 已完成。做种的任务在 aria2 里也是 active，单列一组，不和真在下的混在一起。</summary>
    private List<TaskGroup> Groups(Snapshot s) =>
    [
        new("downloading", L.T("下载中", "Downloading"), Sort(s.Active.Where(d => !d.IsSeeding))),
        new("waiting", L.T("等待中", "Waiting"), Sort(s.Waiting)),
        new("seeding", L.T("做种中", "Seeding"), Sort(s.Active.Where(d => d.IsSeeding))),
        new("stopped", L.T("已完成", "Finished"), Sort(s.Stopped)),
    ];

    /// <summary>
    /// 排序；值相同时保持 aria2 给的先后。名字按「自然顺序」比（第 2 集排在第 10 集前面），不分大小写。
    /// 速度：做种的按上传速度，其余按下载速度。
    /// </summary>
    private List<Download> Sort(IEnumerable<Download> list)
    {
        var indexed = list.Select((d, i) => (d, i)).ToList();
        if (!_resort)
        {
            return [.. indexed.OrderBy(x => _rank.TryGetValue(x.d.Gid, out var r) ? r : int.MaxValue).ThenBy(x => x.i).Select(x => x.d)];
        }

        IComparer<(Download d, int i)> by = SortBy switch
        {
            "name" => Comparer<(Download d, int i)>.Create((a, b) =>
                L.Culture.CompareInfo.Compare(Describe.Name(a.d), Describe.Name(b.d), System.Globalization.CompareOptions.IgnoreCase | System.Globalization.CompareOptions.NumericOrdering)),
            "size" => Comparer<(Download d, int i)>.Create((a, b) => a.d.TotalLength.CompareTo(b.d.TotalLength)),
            "progress" => Comparer<(Download d, int i)>.Create((a, b) => (a.d.Progress ?? 0).CompareTo(b.d.Progress ?? 0)),
            "speed" => Comparer<(Download d, int i)>.Create((a, b) => Speed(a.d).CompareTo(Speed(b.d))),
            _ => Comparer<(Download d, int i)>.Create((a, b) => a.i.CompareTo(b.i)),
        };
        static long Speed(Download d) => d.IsSeeding ? d.UploadSpeed : d.DownloadSpeed;
        var ordered = SortDescending ? indexed.Order(Comparer<(Download d, int i)>.Create((a, b) => by.Compare(b, a))) : indexed.Order(by);
        return [.. ordered.ThenBy(x => x.i).Select(x => x.d)];
    }

    private static int PageCount(TaskGroup g) => Math.Max(1, (g.Items.Count + PageSize - 1) / PageSize);

    /// <summary>这一组当前在第几页（从 0 起）；任务变少、页数变少时退到最后一页。</summary>
    private int PageOf(TaskGroup g)
    {
        var p = Math.Clamp(_pages.GetValueOrDefault(g.Key), 0, PageCount(g) - 1);
        _pages[g.Key] = p;
        return p;
    }

    private bool Collapsed(TaskGroup g) => _collapsed.GetValueOrDefault(g.Key);

    private List<Download> Visible(TaskGroup g) => Collapsed(g) ? [] : [.. g.Items.Skip(PageOf(g) * PageSize).Take(PageSize)];

    /// <summary>画出来的组成：每组的个数、页码、展开没有、有哪几个组操作、这一页有哪些任务。没变就原地更新。</summary>
    private string MainKey(List<TaskGroup> groups) => string.Join("#", groups.Select(g =>
        $"{g.Key}:{g.Items.Count}:{PageOf(g)}:{Collapsed(g)}:{string.Join(",", GroupOps(g))}:" + string.Join("|", Visible(g).Select(d => d.Gid))));

    /// <summary>
    /// 组标题右边的整组操作（2026-10-18 用户要求），按这一组里有什么状态的任务决定出哪几个：
    /// 下载中「全部暂停」；等待中有暂停的就「全部开始」、有排队的就「全部暂停」；做种中「全部暂停」「停止做种」；已完成「清除」。
    /// </summary>
    private static List<GroupCommand?> GroupOps(TaskGroup g)
    {
        var ops = new List<GroupCommand?>();
        switch (g.Key)
        {
            case "downloading":
                ops.Add(GroupCommand.Pause);
                break;
            case "waiting":
                if (g.Items.Any(d => d.CanResume))
                {
                    ops.Add(GroupCommand.Resume);
                }

                if (g.Items.Any(d => d.CanPause))
                {
                    ops.Add(GroupCommand.Pause);
                }

                break;
            case "seeding":
                ops.Add(GroupCommand.Pause);
                ops.Add(GroupCommand.Remove);
                break;
            case "stopped":
                // null 是「清除」（purgeDownloadResult，不是逐个操作）。
                ops.Add(null);
                break;
        }

        return ops;
    }

    private StackPanel GroupButtons(TaskGroup g)
    {
        var s = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        var items = g.Items;
        foreach (var op in GroupOps(g))
        {
            s.Children.Add(op switch
            {
                GroupCommand.Pause => K.CircleButton(Icons.Pause, 22, () => actions.GroupAction([.. items.Where(d => d.CanPause)], GroupCommand.Pause), L.T("全部暂停", "Pause all")),
                GroupCommand.Resume => K.CircleButton(Icons.Play, 22, () => actions.GroupAction([.. items.Where(d => d.CanResume)], GroupCommand.Resume), L.T("全部开始", "Resume all")),
                GroupCommand.Remove => K.ConfirmButton(Icons.Stop, L.T("停止做种", "Stop seeding"), L.T("停止做种？", "Stop seeding?"), () => actions.GroupAction(items, GroupCommand.Remove)),
                _ => K.CircleButton(Icons.Clear, 22, actions.Purge, L.T("清除已完成", "Clear finished")),
            });
        }

        return s;
    }

    /// <summary>组标题：名字和个数；「做种中」另写这一组的总上传速度（每秒原地更新）。</summary>
    private static string Title(TaskGroup g) => g.Key == "seeding"
        ? $"{g.Title} · {g.Items.Count} · ↑ {Format.Speed(g.Items.Sum(d => d.UploadSpeed))}"
        : $"{g.Title} · {g.Items.Count}";

    /// <summary>组成和顺序没变：原地更新，返回 true；变了返回 false，由调用方重建。</summary>
    public bool TryUpdateMain(Snapshot s, SpeedHistory history)
    {
        var groups = Groups(s);
        if (_mainKey != MainKey(groups) || _chart is null)
        {
            return false;
        }

        _chart.Update(history);
        foreach (var d in s.All)
        {
            if (_cards.TryGetValue(d.Gid, out var card))
            {
                card.Update(d);
            }
        }

        if (_seedingTitle is not null)
        {
            _seedingTitle.Text = Title(groups.First(g => g.Key == "seeding"));
        }

        Debug = MainDebug(s);
        return true;
    }

    public void BuildMain(Panel body, Snapshot s, SpeedHistory history)
    {
        Reset();
        _seedingTitle = null;

        // 整页重建：按最新的值重排一次，记下名次，之后每秒原地更新都照这个顺序。
        _resort = true;
        var groups = Groups(s);
        _resort = false;
        _rank = groups.SelectMany(g => g.Items).Select((d, i) => (d.Gid, i)).ToDictionary(x => x.Gid, x => x.i);
        _mainKey = MainKey(groups);
        _chart = new SpeedChart(K);
        _chart.Update(history);
        body.Children.Add(K.Card(_chart, padding: 12));

        if (!s.All.Any())
        {
            var empty = new StackPanel { Spacing = 14, Margin = new Thickness(0, 48, 0, 0), HorizontalAlignment = HorizontalAlignment.Center };
            var text = K.Text(L.T("没有下载任务", "No downloads"), 13, K.P.Text2);
            text.HorizontalAlignment = HorizontalAlignment.Center;
            empty.Children.Add(text);
            var add = K.PlainButton(L.T("新建下载", "New download"), Icons.Add, actions.OpenAdd);
            add.HorizontalAlignment = HorizontalAlignment.Center;
            empty.Children.Add(add);
            body.Children.Add(empty);
            Debug = MainDebug(s);
            return;
        }

        foreach (var g in groups.Where(g => g.Items.Count > 0))
        {
            var key = g.Key;
            var (header, title) = K.CollapsibleSection(
                Title(g),
                !Collapsed(g),
                () =>
                {
                    _collapsed[key] = !_collapsed.GetValueOrDefault(key);
                    actions.Rerender();
                },
                GroupButtons(g));
            if (key == "seeding")
            {
                _seedingTitle = title;
            }

            body.Children.Add(header);

            if (Collapsed(g))
            {
                continue;
            }

            var stack = new StackPanel { Spacing = 8 };
            foreach (var d in Visible(g))
            {
                var card = new TaskCard(K, d, actions.OpenDetail, actions.PauseOrResume, actions.Remove, TaskMenu, actions.ThumbnailAsync);
                _cards[d.Gid] = card;
                stack.Children.Add(card);
            }

            body.Children.Add(stack);
            var pages = PageCount(g);
            if (pages > 1)
            {
                var page = PageOf(g);
                body.Children.Add(K.Pager(page, pages, delta =>
                {
                    _pages[key] = page + delta;
                    actions.Rerender();
                }));
            }
        }

        Debug = MainDebug(s);
    }

    private object MainDebug(Snapshot s) => new
    {
        page = "main",
        sort = SortBy + (SortDescending ? " desc" : " asc"),
        groups = Groups(s).Select(g => new
        {
            group = g.Key,
            count = g.Items.Count,
            page = $"{PageOf(g) + 1}/{PageCount(g)}",
            collapsed = Collapsed(g),
            items = Visible(g).Select(d => new { d.Gid, name = Describe.Name(d), left = Describe.Lines(d).Left, right = Describe.Lines(d).Right, bar = Describe.BarFraction(d) }),
        }),
    };

    // ---------------- 右键菜单 ----------------

    /// <summary>
    /// 任务卡片的右键菜单（2026-10-18 用户要求）。每次弹出时按这个任务当时的状态现排菜单项：
    /// 详情；暂停 / 继续；等待中的移到队首 / 队尾；复制名字、链接（BT 是磁力链接）、保存位置；移除（做种中的叫「停止做种」）。
    /// 菜单限制在面板窗口里弹，免得抢走焦点让面板收起。
    /// </summary>
    public MenuFlyout TaskMenu(Func<Download> current)
    {
        var menu = new MenuFlyout { ShouldConstrainToRootBounds = true, MenuFlyoutPresenterStyle = K.P.MenuStyle };
        menu.Opened += (_, _) => actions.MenuOpenChanged(true);
        menu.Closed += (_, _) => actions.MenuOpenChanged(false);
        menu.Opening += (_, _) =>
        {
            menu.Items.Clear();
            foreach (var (text, glyph, click) in MenuEntries(current()))
            {
                if (text is null)
                {
                    menu.Items.Add(new MenuFlyoutSeparator());
                    continue;
                }

                var item = new MenuFlyoutItem { Text = text, Icon = new FontIcon { FontFamily = Icons.Font, Glyph = glyph } };
                item.Click += (_, _) => click!();
                menu.Items.Add(item);
            }
        };
        return menu;
    }

    /// <summary>调试通道用：某个任务的右键菜单有哪几项（分隔线写成「—」）。</summary>
    public IEnumerable<string> MenuTexts(Download d) => MenuEntries(d).Select(e => e.Text ?? "—");

    /// <summary>菜单项；Text 是 null 的是分隔线。</summary>
    private List<(string? Text, string Glyph, Action? Click)> MenuEntries(Download d)
    {
        var list = new List<(string?, string, Action?)> { (L.T("详情", "Details"), Icons.Info, () => actions.OpenDetail(d)) };
        if (d.CanResume)
        {
            list.Add((L.T("继续", "Resume"), Icons.Play, () => actions.PauseOrResume(d)));
        }
        else if (d.CanPause)
        {
            list.Add((L.T("暂停", "Pause"), Icons.Pause, () => actions.PauseOrResume(d)));
        }

        if (d.Group == Group.Waiting)
        {
            list.Add((L.T("移到队首", "Move to top"), Icons.Top, () => actions.Move(d, top: true)));
            list.Add((L.T("移到队尾", "Move to bottom"), Icons.Bottom, () => actions.Move(d, top: false)));
        }

        if (Describe.RemotePath(d) is not null)
        {
            list.Add((null, "", null));
            list.Add((L.T("打开文件", "Open"), Icons.OpenFile, () => actions.Open(d, reveal: false)));
            list.Add((L.T("打开文件位置", "Show in folder"), Icons.Folder, () => actions.Open(d, reveal: true)));
        }

        list.Add((null, "", null));
        list.Add((L.T("复制名字", "Copy name"), Icons.Copy, () => actions.Copy(Describe.Name(d))));
        if (Describe.Link(d) is { } link)
        {
            list.Add((d.IsBt ? L.T("复制磁力链接", "Copy magnet link") : L.T("复制链接", "Copy link"), Icons.Link, () => actions.Copy(link)));
        }

        if (d.Dir.Length > 0)
        {
            list.Add((L.T("复制保存位置", "Copy location"), Icons.Folder, () => actions.Copy(d.Dir)));
        }

        list.Add((null, "", null));
        list.Add((d.IsSeeding ? L.T("停止做种", "Stop seeding") : L.T("移除", "Remove"), d.IsSeeding ? Icons.Stop : Icons.Cancel, () => actions.Remove(d)));
        return list;
    }

    // ---------------- 详情页 ----------------

    private sealed record DetailParts(string Key, TextBlock Percent, ProgressPill Bar, TextBlock Amount, Dictionary<string, TextBlock> Values, List<(ProgressPill Bar, TextBlock Text)> Files);

    private static string DetailKey(Download d) => $"{d.Gid}|{d.Status}|{d.IsSeeding}|{d.Files.Count}|{d.IsBt}|{d.ErrorCode}";

    private List<(string Key, string Label, string Value)> Properties(Download d)
    {
        var rows = new List<(string, string, string)>
        {
            ("status", L.T("状态", "Status"), Describe.Status(d)),
            ("size", L.T("大小", "Size"), d.TotalLength > 0 ? Format.Size(d.TotalLength) : "—"),
            ("done", L.T("已下载", "Downloaded"), Format.Size(d.CompletedLength)),
            // 速度和连接数只在活动中时有意义，其余状态写「—」。
            ("down", L.T("下载速度", "Download speed"), d.Status == "active" ? Format.Speed(d.DownloadSpeed) : "—"),
            ("up", L.T("上传速度", "Upload speed"), d.Status == "active" ? Format.Speed(d.UploadSpeed) : "—"),
            ("eta", L.T("剩余时间", "Time left"), d.Eta is { } eta ? Format.Duration(eta) : "—"),
            ("conn", L.T("连接数", "Connections"), d.Status == "active" ? d.Connections.ToString(L.Culture) : "—"),
            ("dir", L.T("保存位置", "Location"), d.Dir.Length > 0 ? d.Dir : "—"),
        };
        if (d.IsBt)
        {
            rows.Add(("seeders", L.T("做种者", "Seeders"), d.NumSeeders?.ToString(L.Culture) ?? "—"));
            rows.Add(("ratio", L.T("分享率", "Ratio"), d.Ratio is { } r ? r.ToString("0.00", L.Culture) : "—"));
            rows.Add(("hash", L.T("信息哈希", "Info hash"), d.InfoHash ?? "—"));
        }

        if (d.Status == "error")
        {
            rows.Add(("code", L.T("出错代码", "Error code"), d.ErrorCode ?? "—"));
            rows.Add(("msg", L.T("出错信息", "Error message"), d.ErrorMessage ?? "—"));
        }

        return rows;
    }

    /// <summary>文件名相对保存位置；不在保存位置下面就写完整路径。</summary>
    private static string FileName(Download d, DownloadFile f)
    {
        if (f.Path.Length == 0)
        {
            return f.Uri ?? "—";
        }

        var dir = d.Dir.TrimEnd('/', '\\');
        return dir.Length > 0 && f.Path.StartsWith(dir, StringComparison.Ordinal) && f.Path.Length > dir.Length + 1 ? f.Path[(dir.Length + 1)..] : f.Path;
    }

    private static string FileLine(DownloadFile f) =>
        f.Length > 0 ? $"{Format.Size(f.Length)} · {Format.Percent((double)f.Completed / f.Length)}" : Format.Size(f.Completed);

    public bool TryUpdateDetail(Download d)
    {
        if (_detail is not { } p || p.Key != DetailKey(d))
        {
            return false;
        }

        p.Percent.Text = d.Progress is { } f ? Format.Percent(f) : "—";
        p.Bar.Set(Describe.BarFraction(d), Describe.BarFill(d, K.P));
        p.Amount.Text = Describe.Lines(d).Right is { Length: > 0 } right ? $"{Describe.Amount(d)} · {right}" : Describe.Amount(d);
        foreach (var (key, _, value) in Properties(d))
        {
            if (p.Values.TryGetValue(key, out var t))
            {
                t.Text = value;
            }
        }

        var files = d.Files.Take(p.Files.Count).ToList();
        for (var i = 0; i < files.Count; i++)
        {
            p.Files[i].Bar.Set(files[i].Length > 0 ? (double)files[i].Completed / files[i].Length : null, Describe.BarFill(d, K.P));
            p.Files[i].Text.Text = FileLine(files[i]);
        }

        Debug = DetailDebug(d);
        return true;
    }

    public void BuildDetail(Panel body, Download d)
    {
        Reset();

        // 完整的名字（折行，能选中复制），下面是大号进度。顶栏只写「详情」，名字不重复出现（2026-10-18 用户发现顶栏省略了名字、下面明明有空间也不写全）。
        var top = new StackPanel { Spacing = 8 };

        // 最上面是缩略图（有的话，圆角 10，等宽于卡片、最高 200），取到了才出现，取不到这一块就不占地方。
        var thumb = new Image { Stretch = Stretch.Uniform, MaxHeight = 200 };
        var thumbFrame = new Border { CornerRadius = new CornerRadius(10), Child = thumb, Visibility = Visibility.Collapsed, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 4) };
        top.Children.Add(thumbFrame);
        ThumbState = "loading";
        _ = LoadThumb();
        async Task LoadThumb()
        {
            var image = await actions.ThumbnailAsync(d, 480);
            thumb.Source = image;
            thumbFrame.Visibility = image is null ? Visibility.Collapsed : Visibility.Visible;
            ThumbState = image is Microsoft.UI.Xaml.Media.Imaging.BitmapImage b ? $"shown {b.PixelWidth}x{b.PixelHeight}" : image is null ? "none" : "shown";
        }

        var fullName = K.Text(Describe.Name(d), 14, null, strong: true, trim: false);
        fullName.IsTextSelectionEnabled = true;
        top.Children.Add(fullName);
        var percent = K.Text(d.Progress is { } f ? Format.Percent(f) : "—", 40, null);
        percent.FontWeight = Microsoft.UI.Text.FontWeights.Light;
        top.Children.Add(percent);
        var bar = K.Progress(Describe.BarFraction(d), Describe.BarFill(d, K.P), 8);
        top.Children.Add(bar);
        var amount = K.Text("", 12, K.P.Text2);
        top.Children.Add(amount);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 6, 0, 0) };
        if (d.CanPause || d.CanResume)
        {
            buttons.Children.Add(K.PlainButton(d.CanResume ? L.T("继续", "Resume") : L.T("暂停", "Pause"), d.CanResume ? Icons.Play : Icons.Pause, () => actions.PauseOrResume(d)));
        }

        buttons.Children.Add(K.PlainButton(L.T("移除", "Remove"), Icons.Cancel, () => actions.Remove(d)));
        if (Describe.RemotePath(d) is not null)
        {
            buttons.Children.Add(K.PlainButton(L.T("打开", "Open"), Icons.OpenFile, () => actions.Open(d, reveal: false)));
            buttons.Children.Add(K.PlainButton(L.T("位置", "Folder"), Icons.Folder, () => actions.Open(d, reveal: true)));
        }
        top.Children.Add(buttons);
        body.Children.Add(K.Card(top, padding: 14));

        // 属性列表。
        var values = new Dictionary<string, TextBlock>();
        var props = new StackPanel();
        var rows = Properties(d);
        for (var i = 0; i < rows.Count; i++)
        {
            if (i > 0)
            {
                props.Children.Add(K.Separator());
            }

            var row = K.Row(rows[i].Label, rows[i].Value, rows[i].Key == "msg" ? K.P.Error : null);
            values[rows[i].Key] = (TextBlock)row.Children[1];
            props.Children.Add(row);
        }

        body.Children.Add(K.Section(L.T("信息", "Info")));
        body.Children.Add(K.Card(props, padding: 12));

        // 文件列表：太多时只列前 200 个。
        var fileParts = new List<(ProgressPill, TextBlock)>();
        if (d.Files.Count > 0)
        {
            const int Limit = 200;
            body.Children.Add(K.Section($"{L.T("文件", "Files")} · {d.Files.Count}"));
            var list = new StackPanel();
            foreach (var file in d.Files.Take(Limit))
            {
                if (list.Children.Count > 0)
                {
                    list.Children.Add(K.Separator());
                }

                var g = new Grid { Padding = new Thickness(0, 8, 0, 8), RowSpacing = 5, ColumnSpacing = 10, Opacity = file.Selected ? 1 : 0.5 };
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                // 文件名常常很长（种子里带目录）：最多折成两行，再长才省略，悬停看完整路径。
                var name = K.Text(FileName(d, file), 12);
                name.TextWrapping = TextWrapping.Wrap;
                name.MaxLines = 2;
                ToolTipService.SetToolTip(name, file.Path);
                var line = K.Text(FileLine(file), 11, K.P.Text2);
                Grid.SetColumn(line, 1);
                var fileBar = K.Progress(file.Length > 0 ? (double)file.Completed / file.Length : null, Describe.BarFill(d, K.P), 4);
                Grid.SetRow(fileBar, 1);
                Grid.SetColumnSpan(fileBar, 2);
                g.Children.Add(name);
                g.Children.Add(line);
                g.Children.Add(fileBar);
                list.Children.Add(g);
                fileParts.Add((fileBar, line));
            }

            if (d.Files.Count > Limit)
            {
                list.Children.Add(K.Separator());
                var more = K.Text(L.T($"还有 {d.Files.Count - Limit} 个文件", $"{d.Files.Count - Limit} more files"), 12, K.P.Text2);
                more.Margin = new Thickness(0, 8, 0, 8);
                list.Children.Add(more);
            }

            body.Children.Add(K.Card(list, padding: 12));
        }

        _detail = new DetailParts(DetailKey(d), percent, bar, amount, values, fileParts);
        TryUpdateDetail(d);
    }

    /// <summary>调试通道用：详情页的缩略图 loading / none / shown。</summary>
    public string? ThumbState { get; private set; }

    private object DetailDebug(Download d) => new
    {
        page = "detail",
        thumb = ThumbState,
        d.Gid,
        name = Describe.Name(d),
        properties = Properties(d).Select(r => $"{r.Label}: {r.Value}"),
        files = d.Files.Count,
    };
}
