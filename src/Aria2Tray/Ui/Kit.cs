using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace Aria2Tray.Ui;

/// <summary>
/// 面板的控件套件，样子照 docs/design.md 第 5 节（接近 macOS 控制中心，和 HA Tray 一样）。全部在代码里拼，按钮共用 App.xaml 的 MacButtonStyle。
/// </summary>
internal sealed class Kit(Palette p)
{
    private static Style ButtonStyle => (Style)Application.Current.Resources["MacButtonStyle"];

    public Palette P => p;

    // ---------------- 文字和容器 ----------------

    /// <summary>文字。数字一律用等宽数字，速度每秒变时不左右跳。</summary>
    public TextBlock Text(string text, double size = 13, Brush? brush = null, bool strong = false, bool trim = true)
    {
        var t = new TextBlock
        {
            Text = text,
            FontSize = size,
            Foreground = brush ?? p.Text1,
            FontWeight = strong ? FontWeights.SemiBold : FontWeights.Normal,
            TextTrimming = trim ? TextTrimming.CharacterEllipsis : TextTrimming.None,
            TextWrapping = trim ? TextWrapping.NoWrap : TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Typography.SetNumeralAlignment(t, FontNumeralAlignment.Tabular);
        return t;
    }

    /// <summary>分组标题：11 号、半粗、次要色；右边可以放一个小按钮（「清除」）。</summary>
    public Grid Section(string text, UIElement? right = null)
    {
        var g = new Grid { Margin = new Thickness(6, 14, 2, 6), MinHeight = 18 };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var t = Text(text, 11, p.Text2, strong: true);
        g.Children.Add(t);
        if (right is FrameworkElement r)
        {
            Grid.SetColumn(r, 1);
            g.Children.Add(r);
        }

        return g;
    }

    /// <summary>
    /// 标签（macOS 的分段控件）：灰色底槽，各段等宽；选中的那段是凸起的浅色块、字半粗。每段是名字加个数，个数用次要色。
    /// </summary>
    public Border Tabs(IReadOnlyList<(string Key, string Name, int Count)> tabs, string selected, Action<string> picked)
    {
        var grid = new Grid { ColumnSpacing = 2 };
        for (var i = 0; i < tabs.Count; i++)
        {
            var (key, name, count) = tabs[i];
            var on = key == selected;
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, HorizontalAlignment = HorizontalAlignment.Center };
            var nameText = Text(name, 12, p.Text1, strong: on);
            content.Children.Add(nameText);
            content.Children.Add(Text(count.ToString(L.Culture), 11, p.Text2));
            var b = new Button
            {
                Style = ButtonStyle,
                Height = 28,
                Padding = new Thickness(4, 0, 4, 0),
                CornerRadius = new CornerRadius(7),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Background = on ? p.SegmentSelected : null,
                Foreground = p.Text1,
                Content = content,
                Tag = key,
            };
            b.Click += (_, _) => picked(key);
            Grid.SetColumn(b, i);
            grid.Children.Add(b);
        }

        return new Border { Background = p.Control, CornerRadius = new CornerRadius(9), Padding = new Thickness(2), Child = grid };
    }

    /// <summary>分页：居中的「‹  2 / 5  ›」，到头的那边按钮不可用。参数是 −1 或 +1。</summary>
    public StackPanel Pager(int page, int pages, Action<int> go)
    {
        var s = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0) };
        var prev = GhostButton(Icons.Icon(Icons.Back, 11), () => go(-1), L.T("上一页", "Previous page"));
        prev.IsEnabled = page > 0;
        var next = GhostButton(Icons.Icon(Icons.Forward, 11), () => go(1), L.T("下一页", "Next page"));
        next.IsEnabled = page < pages - 1;
        var label = Text($"{page + 1} / {pages}", 11, p.Text2);
        label.MinWidth = 44;
        label.TextAlignment = TextAlignment.Center;
        s.Children.Add(prev);
        s.Children.Add(label);
        s.Children.Add(next);
        return s;
    }

    /// <summary>
    /// 横幅卡片：左边警告色的图标，中间一句话（折行），右边可以有一个动作按钮和 ✕。close 是 null 时没有 ✕（断线的横幅，连上了自己消失）。
    /// </summary>
    public Border Banner(string text, string? actionText, Action? action, Action? close)
    {
        var g = new Grid { ColumnSpacing = 10 };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        // 图标和文字都按整行竖直居中（右边按钮比字高，贴顶放的图标会比字高出一截，2026-10-18 用户发现没对齐）。
        var icon = Icons.Icon(Icons.Error, 15, p.Warning);
        icon.VerticalAlignment = VerticalAlignment.Center;
        g.Children.Add(icon);
        var t = Text(text, 12, null, trim: false);
        t.IsTextSelectionEnabled = true;
        Grid.SetColumn(t, 1);
        g.Children.Add(t);
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        if (actionText is not null && action is not null)
        {
            right.Children.Add(PlainButton(actionText, null, action, 26, accent: true));
        }

        if (close is not null)
        {
            right.Children.Add(CircleButton(Icons.Cancel, 22, close, L.T("关闭", "Close")));
        }

        Grid.SetColumn(right, 2);
        g.Children.Add(right);
        return new Border { Background = p.Module, CornerRadius = new CornerRadius(12), Padding = new Thickness(12, 10, 8, 10), Child = g };
    }

    /// <summary>卡片：半透明白，圆角 14，不画边框。</summary>
    public Border Card(UIElement child, double padding = 10, double radius = 14) => new()
    {
        Background = p.Module,
        CornerRadius = new CornerRadius(radius),
        Padding = new Thickness(padding),
        Child = child,
    };

    public Rectangle Separator() => new() { Height = 0.5, Fill = p.Separator, HorizontalAlignment = HorizontalAlignment.Stretch };

    // ---------------- 按钮 ----------------

    /// <summary>圆形图标按钮，灰底。</summary>
    public Button CircleButton(string glyph, double size, Action? click, string tip)
    {
        var b = new Button
        {
            Style = ButtonStyle,
            Width = size,
            Height = size,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(size / 2),
            Background = p.Control,
            Foreground = p.Text1,
            Content = Icons.Icon(glyph, Math.Round(size * 0.42)),
            IsEnabled = click is not null,
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (click is not null)
        {
            b.Click += (_, _) => click();
        }

        ToolTipService.SetToolTip(b, tip);
        return b;
    }

    /// <summary>
    /// 要确认的小圆按钮（停止做种）：点一下变成红底的「停止做种？」，4 秒内再点一下才执行，过时恢复原样。
    /// 和 HA 前端门锁「开门」的确认一样，不弹对话框。
    /// </summary>
    public Button ConfirmButton(string glyph, string tip, string confirmText, Action confirmed)
    {
        const double Size = 22;
        var b = new Button
        {
            Style = ButtonStyle,
            Height = Size,
            MinWidth = Size,
            CornerRadius = new CornerRadius(Size / 2),
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTipService.SetToolTip(b, tip);
        var armed = false;
        var timer = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(4);
        timer.IsRepeating = false;
        void Disarm()
        {
            armed = false;
            timer.Stop();
            b.Content = Icons.Icon(glyph, Math.Round(Size * 0.42));
            b.Padding = new Thickness(0);
            b.Background = p.Control;
            b.Foreground = p.Text1;
        }

        timer.Tick += (_, _) => Disarm();
        b.Click += (_, _) =>
        {
            if (armed)
            {
                Disarm();
                confirmed();
                return;
            }

            armed = true;
            b.Content = new TextBlock { Text = confirmText, FontSize = 11, FontWeight = FontWeights.SemiBold };
            b.Padding = new Thickness(10, 0, 10, 0);
            b.Background = p.Error;
            b.Foreground = p.OnAccent;
            timer.Start();
        };
        Disarm();
        return b;
    }

    /// <summary>灰色圆角按钮；accent 时填强调色。</summary>
    public Button PlainButton(string text, string? glyph, Action click, double height = 28, bool accent = false)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        if (glyph is not null)
        {
            content.Children.Add(Icons.Icon(glyph, 12));
        }

        content.Children.Add(new TextBlock { Text = text, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, FontWeight = accent ? FontWeights.SemiBold : FontWeights.Normal });
        var b = new Button
        {
            Style = ButtonStyle,
            Height = height,
            Padding = new Thickness(12, 0, 12, 0),
            Background = accent ? p.Accent : p.Control,
            Foreground = accent ? p.OnAccent : p.Text1,
            Content = content,
        };
        b.Click += (_, _) => click();
        return b;
    }

    /// <summary>无底色的按钮（顶栏的新建、全部暂停、返回）。</summary>
    public Button GhostButton(UIElement content, Action click, string? tip = null)
    {
        var b = new Button
        {
            Style = ButtonStyle,
            Height = 28,
            MinWidth = 28,
            Padding = new Thickness(6, 0, 6, 0),
            CornerRadius = new CornerRadius(6),
            Foreground = p.Text1,
            Content = content,
            VerticalAlignment = VerticalAlignment.Center,
        };
        b.Click += (_, _) => click();
        if (tip is not null)
        {
            ToolTipService.SetToolTip(b, tip);
        }

        return b;
    }


    // ---------------- 进度 ----------------

    public ProgressPill Progress(double? fraction, Brush fill, double height = 6)
    {
        var bar = new ProgressPill(p, height);
        bar.Set(fraction, fill);
        return bar;
    }

    /// <summary>属性列表的一行：左边名字（次要色），右边数值；数值太长（路径、哈希、出错信息）时折行，能选中复制。</summary>
    public Grid Row(string label, string value, Brush? valueBrush = null)
    {
        var g = new Grid { MinHeight = 32, ColumnSpacing = 12, Padding = new Thickness(0, 6, 0, 6) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var name = Text(label, 13, p.Text2);
        name.VerticalAlignment = VerticalAlignment.Top;
        g.Children.Add(name);
        var v = Text(value, 13, valueBrush, trim: false);
        v.HorizontalAlignment = HorizontalAlignment.Right;
        v.TextAlignment = TextAlignment.Right;
        v.IsTextSelectionEnabled = true;
        Grid.SetColumn(v, 1);
        g.Children.Add(v);
        return g;
    }
}
