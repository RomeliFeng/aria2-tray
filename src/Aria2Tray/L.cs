using System.Globalization;
using System.Runtime.InteropServices;

namespace Aria2Tray;

/// <summary>
/// 界面文案，两种说法写在调用处，不拆资源文件。语言可以固定成中文或英文，也可以选「自动」（跟 Windows 显示语言）。
/// </summary>
internal static partial class L
{
    public static bool Chinese { get; private set; } = SystemIsChinese;

    /// <summary>数字和时间格式用的区域。</summary>
    public static CultureInfo Culture => Chinese ? CultureInfo.GetCultureInfo("zh-CN") : CultureInfo.GetCultureInfo("en-US");

    private static bool SystemIsChinese => (GetUserDefaultUILanguage() & 0x3FF) == 0x04;

    /// <summary>"auto" / "zh" / "en"。</summary>
    public static void Init(string mode) => Chinese = mode switch
    {
        "zh" => true,
        "en" => false,
        _ => SystemIsChinese,
    };

    public static string T(string zh, string en) => Chinese ? zh : en;

    [LibraryImport("kernel32.dll")]
    private static partial ushort GetUserDefaultUILanguage();
}
