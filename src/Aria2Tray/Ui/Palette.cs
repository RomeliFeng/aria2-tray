using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace Aria2Tray.Ui;

/// <summary>
/// 面板的颜色（接近 macOS 控制中心，和 HA Tray 同一套，见 docs/design.md 第 5 节）。按面板当前的深浅色取，
/// 换深浅色或系统强调色时面板整个重画，所以这里每次现取。
/// </summary>
internal sealed class Palette
{
    private static readonly UISettings Ui = new();

    private Palette(bool dark)
    {
        Dark = dark;
        Module = B(dark ? 0x13FFFFFF : 0x9EFFFFFF);
        ModuleHover = B(dark ? 0x1FFFFFFF : 0xC8FFFFFF);
        Control = B(dark ? 0x24FFFFFF : 0x11000000);
        Separator = B(dark ? 0x14FFFFFF : 0x14000000);
        Text1 = B(dark ? 0xEBFFFFFF : 0xDB000000);
        Text2 = B(dark ? 0x8CFFFFFF : 0x80000000);
        Text3 = B(dark ? 0x52FFFFFF : 0x4D000000);
        Track = B(dark ? 0x1FFFFFFF : 0x14000000);
        Idle = B(dark ? 0x66FFFFFF : 0x59000000);
        SegmentSelected = B(dark ? 0x42FFFFFF : 0xFFFFFFFF);
        OnAccent = B(0xFFFFFFFF);
        Success = B(dark ? 0xFF30D158 : 0xFF248A3D);
        Error = B(dark ? 0xFFFF453A : 0xFFD70015);
        Warning = B(dark ? 0xFFFFD60A : 0xFFB25000);
        Menu = B(dark ? 0xFF2C2C2E : 0xFFF6F6F6);
        MenuBorder = B(dark ? 0x26FFFFFF : 0x1A000000);
        AccentColor = Ui.GetColorValue(UIColorType.Accent);
        Accent = new SolidColorBrush(AccentColor);
        AccentFill = new SolidColorBrush(Color.FromArgb(0x38, AccentColor.R, AccentColor.G, AccentColor.B));
    }

    public bool Dark { get; }

    /// <summary>卡片底色。</summary>
    public Brush Module { get; }

    /// <summary>能点的卡片悬停时稍微亮一点。</summary>
    public Brush ModuleHover { get; }

    /// <summary>控件（圆形按钮、文本框）底色。</summary>
    public Brush Control { get; }

    public Brush Separator { get; }

    public Brush Text1 { get; }

    public Brush Text2 { get; }

    public Brush Text3 { get; }

    /// <summary>进度条底槽。</summary>
    public Brush Track { get; }

    /// <summary>暂停、等待中的进度条。</summary>
    public Brush Idle { get; }

    /// <summary>标签（分段控件）里选中的那段。</summary>
    public Brush SegmentSelected { get; }

    public Color AccentColor { get; }

    public Brush Accent { get; }

    /// <summary>速度曲线下面的淡填充。</summary>
    public Brush AccentFill { get; }

    public Brush OnAccent { get; }

    public Brush Success { get; }

    public Brush Error { get; }

    public Brush Warning { get; }

    /// <summary>
    /// 菜单底色，不透明。菜单在面板窗口里弹（不开独立窗口，免得抢焦点），自带的半透明背景透不出窗口下面的亚克力，看起来就是透明的。
    /// </summary>
    public Brush Menu { get; }

    public Brush MenuBorder { get; }

    /// <summary>给 MenuFlyout 用的样式：不透明底色、细边框、圆角 10。</summary>
    public Style MenuStyle => new(typeof(Microsoft.UI.Xaml.Controls.MenuFlyoutPresenter))
    {
        Setters =
        {
            new Setter(Microsoft.UI.Xaml.Controls.Control.BackgroundProperty, Menu),
            new Setter(Microsoft.UI.Xaml.Controls.Control.BorderBrushProperty, MenuBorder),
            new Setter(Microsoft.UI.Xaml.Controls.Control.CornerRadiusProperty, new CornerRadius(10)),
        },
    };

    public static Palette For(ElementTheme theme) => new(theme != ElementTheme.Light);

    /// <summary>系统强调色变了（从后台线程触发）。</summary>
    public static event Action? AccentChanged
    {
        add => Ui.ColorValuesChanged += (_, _) => value?.Invoke();
        remove { }
    }

    private static SolidColorBrush B(long argb) =>
        new(Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
}
