using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Aria2Tray.Ui;

/// <summary>新建下载页还没提交的内容。离开这一页也留着，下次进来接着填；全部加成功才清空。</summary>
internal sealed class AddDraft
{
    public string Links { get; set; } = "";

    public List<(string Name, byte[] Data)> Torrents { get; } = [];

    public string Dir { get; set; } = "";

    /// <summary>「保存到」已经按 aria2 的 dir 填过（只填一次，用户改了就不再覆盖）。</summary>
    public bool DirFilled { get; set; }

    public string? Error { get; set; }

    public bool Busy { get; set; }

    public void Clear()
    {
        Links = "";
        Torrents.Clear();
        Error = null;
    }
}

/// <summary>新建下载页（docs/design.md 第 4 节「新建下载」）。这一页不随每秒的数据重画，文本框里的光标不会丢。</summary>
internal sealed partial class PanelView
{
    public void BuildAdd(Panel body, AddDraft draft, Action pickTorrent, Action submit)
    {
        Reset();
        var links = new TextBox
        {
            Text = draft.Links,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            Height = 132,
            FontSize = 12,
            PlaceholderText = L.T("链接或磁力链接，每行一个", "Links or magnet links, one per line"),
            IsSpellCheckEnabled = false,
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(links, ScrollBarVisibility.Auto);
        links.TextChanged += (_, _) => draft.Links = links.Text;
        body.Children.Add(links);

        // 种子：每个一行，右边 ✕ 拿掉。
        var torrents = new StackPanel { Spacing = 6, Margin = new Thickness(0, 10, 0, 0) };
        foreach (var t in draft.Torrents.ToList())
        {
            var g = new Grid { ColumnSpacing = 8 };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.Children.Add(Icons.Icon(Icons.Peers, 13, K.P.Text2));
            var name = K.Text(t.Name, 12);
            Grid.SetColumn(name, 1);
            g.Children.Add(name);
            var remove = K.CircleButton(Icons.Cancel, 22, () =>
            {
                draft.Torrents.Remove(t);
                torrents.Children.Remove(g);
            }, L.T("拿掉", "Remove"));
            Grid.SetColumn(remove, 2);
            g.Children.Add(remove);
            torrents.Children.Add(K.Card(g, padding: 8, radius: 10));
        }

        var pick = K.PlainButton(L.T("添加种子文件…", "Add torrent file…"), Icons.OpenFile, pickTorrent);
        pick.HorizontalAlignment = HorizontalAlignment.Left;
        torrents.Children.Add(pick);
        body.Children.Add(torrents);

        var dir = new TextBox
        {
            Header = L.T("保存到", "Save to"),
            Text = draft.Dir,
            FontSize = 12,
            Margin = new Thickness(0, 14, 0, 0),
            IsSpellCheckEnabled = false,
        };
        dir.TextChanged += (_, _) =>
        {
            if (dir.Text != draft.Dir)
            {
                draft.Dir = dir.Text;
                draft.DirFilled = true;
            }
        };
        body.Children.Add(dir);

        if (draft.Error is { } error)
        {
            var e = K.Text(error, 12, K.P.Error, trim: false);
            e.Margin = new Thickness(0, 12, 0, 0);
            body.Children.Add(e);
        }

        var start = K.PlainButton(L.T("开始下载", "Download"), Icons.Down, submit, 30, accent: true);
        start.HorizontalAlignment = HorizontalAlignment.Right;
        start.Margin = new Thickness(0, 16, 0, 0);
        start.IsEnabled = !draft.Busy;
        body.Children.Add(start);
        Debug = new { page = "add", links = draft.Links.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length, torrents = draft.Torrents.Count, dirSet = draft.Dir.Length > 0, draft.Error, draft.Busy };
    }
}
