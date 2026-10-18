using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Aria2Tray.Ui;

/// <summary>系统图标字体 Segoe Fluent Icons（Windows 10 回退 Segoe MDL2 Assets）里用到的几个图标。</summary>
internal static class Icons
{
    public const string Add = "";
    public const string Cancel = "";
    public const string Play = "";
    public const string Pause = "";
    public const string Stop = "";
    public const string Sort = "";
    public const string Info = "";
    public const string Copy = "";
    public const string Link = "";
    public const string Top = "";
    public const string Bottom = "";
    public const string Back = "";
    public const string Forward = "";    public const string Down = "";
    public const string Up = "";
    public const string File = "";
    public const string Folder = "";
    public const string OpenFile = "";
    public const string Peers = "";
    public const string Error = "";
    public const string Check = "";
    public const string Clear = "";

    public static readonly FontFamily Font = new("Segoe Fluent Icons,Segoe MDL2 Assets");

    public static FontIcon Icon(string glyph, double size, Brush? foreground = null)
    {
        var icon = new FontIcon { FontFamily = Font, FontSize = size, Glyph = glyph };
        if (foreground is not null)
        {
            icon.Foreground = foreground;
        }

        return icon;
    }
}
