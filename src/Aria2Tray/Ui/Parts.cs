using Aria2Tray.Model;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace Aria2Tray.Ui;

/// <summary>
/// 一行文字，放不下时像 macOS（Finder、Safari 下载列表）那样从中间省略：保留开头和结尾，结尾的集数、哈希、扩展名还看得见
/// （2026-10-18 用户要求；字幕组的文件名很长，从尾巴省略时每一集都长得一样）。
/// WinUI 的 TextBlock 只能从尾巴省略，所以按可用宽度量字，二分找出能放下的最多字数。
/// </summary>
internal sealed partial class MiddleText : Panel
{
    private readonly TextBlock _t;
    private string _full = "";
    private string? _fittedFor;
    private double _fittedWidth = -1;

    public MiddleText(TextBlock t)
    {
        _t = t;
        _t.TextTrimming = TextTrimming.None;
        _t.TextWrapping = TextWrapping.NoWrap;

        // 透明背景让整块都能悬停（提示框挂在这一块上），不只是字的笔画。
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        Children.Add(_t);
    }

    public string Text
    {
        get => _full;
        set
        {
            if (_full != value)
            {
                _full = value;
                InvalidateMeasure();
            }
        }
    }

    /// <summary>当前显示的是省略过的（调试通道查截断用）。</summary>
    public bool IsTrimmed { get; private set; }

    public TextBlock Inner => _t;

    protected override Size MeasureOverride(Size availableSize)
    {
        var w = availableSize.Width;
        if (_fittedFor != _full || Math.Abs(_fittedWidth - w) > 0.5)
        {
            _t.Text = Fit(w);
            _fittedFor = _full;
            _fittedWidth = w;
        }

        _t.Measure(new Size(double.PositiveInfinity, availableSize.Height));
        return new Size(double.IsInfinity(w) ? _t.DesiredSize.Width : Math.Min(_t.DesiredSize.Width, w), _t.DesiredSize.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _t.Arrange(new Rect(0, 0, finalSize.Width, finalSize.Height));
        return finalSize;
    }

    private double Measure(string s)
    {
        _t.Text = s;
        _t.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return _t.DesiredSize.Width;
    }

    private string Fit(double width)
    {
        IsTrimmed = false;
        if (double.IsInfinity(width) || _full.Length < 4 || Measure(_full) <= width)
        {
            return _full;
        }

        IsTrimmed = true;
        int lo = 0, hi = _full.Length - 1;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (Measure(Cut(mid)) <= width)
            {
                lo = mid;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return Cut(lo);
    }

    /// <summary>保留 keep 个字：前一半、后一半，中间放省略号；不把一个字（代理对）拆成两半。</summary>
    private string Cut(int keep)
    {
        var head = (keep + 1) / 2;
        var tail = keep - head;
        if (head > 0 && char.IsHighSurrogate(_full[head - 1]))
        {
            head--;
        }

        if (tail > 0 && char.IsLowSurrogate(_full[^tail]))
        {
            tail--;
        }

        return _full[..head].TrimEnd() + "…" + _full[^tail..].TrimStart();
    }
}

/// <summary>胶囊进度条：底槽淡色，填充从左往右；进度是 null 时只有底槽。每秒原地更新，不重建。</summary>
internal sealed partial class ProgressPill : Grid
{
    private readonly Border _bar;
    private readonly double _height;
    private double? _fraction;

    public ProgressPill(Palette p, double height)
    {
        _height = height;
        Height = height;
        CornerRadius = new CornerRadius(height / 2);
        Background = p.Track;
        _bar = new Border { CornerRadius = new CornerRadius(height / 2), HorizontalAlignment = HorizontalAlignment.Left, Width = 0 };
        Children.Add(_bar);
        SizeChanged += (_, _) => Layout();
    }

    public double? Fraction => _fraction;

    public void Set(double? fraction, Brush fill)
    {
        _fraction = fraction;
        _bar.Background = fill;
        Layout();
    }

    /// <summary>有进度时至少画出一个圆点，0.1% 也看得见。</summary>
    private void Layout() =>
        _bar.Width = _fraction is { } f && f > 0 ? Math.Max(_height, ActualWidth * Math.Clamp(f, 0, 1)) : 0;
}

/// <summary>
/// 速度曲线（docs/design.md 第 4 节）：最近 60 秒，下载是强调色的线加淡填充，上传是次要色的线；纵轴按这段时间的最大值缩放，右上角写最大值。
/// </summary>
internal sealed partial class SpeedChart : Grid
{
    private const double ChartHeight = 64;

    private readonly Polygon _downFill;
    private readonly Polyline _down;
    private readonly Polyline _up;
    private readonly TextBlock _max;
    private readonly Canvas _canvas;
    private SpeedHistory? _history;

    public SpeedChart(Kit k)
    {
        Height = ChartHeight;
        _canvas = new Canvas();
        _downFill = new Polygon { Fill = k.P.AccentFill };
        _down = new Polyline { Stroke = k.P.Accent, StrokeThickness = 1.5, StrokeLineJoin = PenLineJoin.Round };
        _up = new Polyline { Stroke = k.P.Text3, StrokeThickness = 1.2, StrokeLineJoin = PenLineJoin.Round };
        _canvas.Children.Add(_downFill);
        _canvas.Children.Add(_up);
        _canvas.Children.Add(_down);
        _max = k.Text("", 10, k.P.Text3);
        _max.HorizontalAlignment = HorizontalAlignment.Right;
        _max.VerticalAlignment = VerticalAlignment.Top;
        _max.Margin = new Thickness(0, -2, 0, 0);
        Children.Add(_canvas);
        Children.Add(_max);
        SizeChanged += (_, _) => Draw();
    }

    public void Update(SpeedHistory history)
    {
        _history = history;
        Draw();
    }

    private void Draw()
    {
        if (_history is null || ActualWidth <= 0)
        {
            return;
        }

        var w = ActualWidth;
        var h = ChartHeight;
        var max = Math.Max(_history.Max, 1024);
        _max.Text = Format.Speed(max);

        // 顶上留 14 给最大值的字，底下留 1 免得线压在边上被裁。
        const double top = 14;
        PointCollection Line(IReadOnlyList<long> values)
        {
            var pts = new PointCollection();
            for (var i = 0; i < values.Count; i++)
            {
                var x = values.Count == 1 ? 0 : w * i / (values.Count - 1);
                var y = h - 1 - ((h - 1 - top) * values[i] / max);
                pts.Add(new Point(x, y));
            }

            return pts;
        }

        _down.Points = Line(_history.Down);
        _up.Points = Line(_history.Up);
        var fill = Line(_history.Down);
        fill.Add(new Point(w, h));
        fill.Add(new Point(0, h));
        _downFill.Points = fill;
    }
}

/// <summary>
/// 任务卡片（docs/design.md 第 4 节「任务卡片」）：第一行图标、名字、暂停 / 继续和移除；第二行进度条；第三行左右两段文字。
/// 每秒原地更新文字和进度条，不重建，悬停和提示框不会每秒闪一下。点卡片（按钮以外）进详情页，右键弹菜单（PanelView.TaskMenu）。
/// </summary>
internal sealed partial class TaskCard : Grid
{
    private readonly Kit _k;
    private readonly FontIcon _icon;
    private readonly MiddleText _name;
    private readonly Button _toggle;
    private readonly ProgressPill _bar;
    private readonly TextBlock _left;
    private readonly TextBlock _right;
    private readonly Action<Download> _pauseOrResume;
    private readonly TextBlock _tipText;
    private readonly Image _tipThumb;
    private Download _d;

    public TaskCard(Kit k, Download d, Action<Download> open, Action<Download> pauseOrResume, Action<Download> remove, Func<Func<Download>, MenuFlyout> menu, Func<Download, uint, Task<ImageSource?>> thumbnail)
    {
        _k = k;
        _d = d;
        _pauseOrResume = pauseOrResume;
        Background = k.P.Module;
        CornerRadius = new CornerRadius(14);
        Padding = new Thickness(12, 10, 10, 10);

        var grid = new Grid { RowSpacing = 0, ColumnSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        _icon = Icons.Icon(Icons.File, 15, k.P.Text2);
        _name = new MiddleText(k.Text("", 13, null, strong: true)) { VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(_name, 1);

        // 悬停名字：上面缩略图（有的话），下面全名（2026-10-18 用户要求）。提示框只建一次，每秒更新时只改里面的字，
        // 不重设提示框（重设的话还没弹出来就被换掉）。缩略图在提示框弹出时才去取。
        _tipThumb = new Image { MaxWidth = 320, MaxHeight = 200, Stretch = Stretch.Uniform, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 2, 0, 6) };
        _tipText = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxWidth = 320 };
        var tipBody = new StackPanel();
        tipBody.Children.Add(_tipThumb);
        tipBody.Children.Add(_tipText);
        var tip = new ToolTip { Content = tipBody, MaxWidth = 360 };
        tip.Opened += async (_, _) =>
        {
            var image = await thumbnail(_d, 320);
            _tipThumb.Source = image;
            _tipThumb.Visibility = image is null ? Visibility.Collapsed : Visibility.Visible;
        };
        ToolTipService.SetToolTip(_name, tip);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        _toggle = k.CircleButton(Icons.Pause, 26, () => _pauseOrResume(_d), L.T("暂停", "Pause"));
        buttons.Children.Add(_toggle);
        buttons.Children.Add(k.CircleButton(Icons.Cancel, 26, () => remove(_d), L.T("移除", "Remove")));
        Grid.SetColumn(buttons, 2);

        _bar = new ProgressPill(k.P, 6) { Margin = new Thickness(0, 8, 0, 0) };
        Grid.SetRow(_bar, 1);
        Grid.SetColumnSpan(_bar, 3);

        var line = new Grid { Margin = new Thickness(0, 6, 0, 0), ColumnSpacing = 8 };
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _left = k.Text("", 11, k.P.Text2);
        _right = k.Text("", 11, k.P.Text2);
        Grid.SetColumn(_right, 1);
        line.Children.Add(_left);
        line.Children.Add(_right);
        Grid.SetRow(line, 2);
        Grid.SetColumnSpan(line, 3);

        grid.Children.Add(_icon);
        grid.Children.Add(_name);
        grid.Children.Add(buttons);
        grid.Children.Add(_bar);
        grid.Children.Add(line);
        Children.Add(grid);

        Tapped += (_, e) =>
        {
            if (!PanelView.InsideButton(e.OriginalSource as DependencyObject, this))
            {
                open(_d);
            }
        };
        PointerEntered += (_, _) => Background = _k.P.ModuleHover;
        PointerExited += (_, _) => Background = _k.P.Module;
        ContextFlyout = menu(() => _d);
        Update(d);
    }

    public string Gid => _d.Gid;

    public void Update(Download d)
    {
        _d = d;
        _icon.Glyph = d.IsBt ? Icons.Peers : Icons.File;
        _name.Text = Describe.Name(d);
        if (_tipText.Text != _name.Text)
        {
            _tipText.Text = _name.Text;
        }
        _bar.Set(Describe.BarFraction(d), Describe.BarFill(d, _k.P));
        var (left, right) = Describe.Lines(d);
        _left.Text = left;
        _left.Foreground = d.Status == "error" ? _k.P.Error : _k.P.Text2;

        // 出错信息常常放不下，悬停看全。
        Tip(_left, d.Status == "error" ? left : null);
        _right.Text = right;
        var canToggle = d.CanPause || d.CanResume;
        _toggle.Visibility = canToggle ? Visibility.Visible : Visibility.Collapsed;
        ((FontIcon)_toggle.Content).Glyph = d.CanResume ? Icons.Play : Icons.Pause;
        Tip(_toggle, d.CanResume ? L.T("继续", "Resume") : L.T("暂停", "Pause"));
    }

    /// <summary>
    /// 提示框的文字变了才重设。每秒都重设的话，提示框还没弹出来就被换掉，悬停看不到全名（2026-10-18 用户发现）。
    /// </summary>
    private static void Tip(DependencyObject target, string? text)
    {
        if (ToolTipService.GetToolTip(target) as string != text)
        {
            ToolTipService.SetToolTip(target, text);
        }
    }
}
