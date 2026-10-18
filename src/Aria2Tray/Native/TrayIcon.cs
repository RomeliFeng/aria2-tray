using System.Runtime.InteropServices;
using Microsoft.Win32;
using static Aria2Tray.Native.Win32;

namespace Aria2Tray.Native;

/// <summary>
/// 通知区域图标。自己建一个不显示的顶层窗口收回调（仅消息窗口收不到 TaskbarCreated 和主题变化的广播）。
/// 图标由调用方现画（参数：任务栏是不是浅色、边长），任务栏深浅色变了、调用 <see cref="Redraw"/> 时重画；
/// 资源管理器重启后自动重新加图标。通知用气泡（Windows 10 / 11 显示成系统通知）。
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private const int CallbackMessage = WM_USER + 100;
    private const uint IconId = 1;
    private static readonly uint TaskbarCreated = RegisterWindowMessage("TaskbarCreated");

    private readonly WndProc _proc;
    private readonly nint _hwnd;
    private readonly Func<bool, int, nint> _render;
    private nint _icon;
    private string _tooltip;
    private bool _added;

    public TrayIcon(string tooltip, Func<bool, int, nint> render)
    {
        _tooltip = tooltip;
        _render = render;
        _proc = Proc;
        var wc = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc),
            hInstance = GetModuleHandle(null),
            lpszClassName = "Aria2TrayNotifyWindow",
        };
        RegisterClassEx(ref wc);
        _hwnd = CreateWindowEx(0, wc.lpszClassName, "Aria2 Tray", 0x80000000 /* WS_POPUP */, 0, 0, 0, 0, 0, 0, wc.hInstance, 0);
        RenderIcon();
        Add();
    }

    /// <summary>左键单击或键盘选中图标。</summary>
    public event Action? Invoked;

    /// <summary>右键，参数是屏幕坐标（物理像素）。</summary>
    public event Action<int, int>? ContextMenuRequested;

    /// <summary>点了通知。</summary>
    public event Action? BalloonClicked;

    public void SetTooltip(string text)
    {
        // szTip 最多 127 个字符。
        text = text.Length > 127 ? text[..127] : text;
        if (text == _tooltip)
        {
            return;
        }

        _tooltip = text;
        var nid = Data(NIF_TIP | NIF_SHOWTIP);
        Shell_NotifyIcon(NIM_MODIFY, ref nid);
    }

    /// <summary>按调用方当前的状态重画图标。</summary>
    public void Redraw()
    {
        RenderIcon();
        var nid = Data(NIF_ICON);
        Shell_NotifyIcon(NIM_MODIFY, ref nid);
    }

    /// <summary>弹通知。标题最多 63 个字符，正文最多 255 个。</summary>
    public void ShowBalloon(string title, string text, nint largeIcon)
    {
        var nid = Data(NIF_INFO);
        nid.szInfoTitle = title.Length > 63 ? title[..63] : title;
        nid.szInfo = text.Length > 255 ? text[..255] : text;
        nid.dwInfoFlags = largeIcon != 0 ? NIIF_USER | NIIF_LARGE_ICON : 0;
        nid.hBalloonIcon = largeIcon;
        Shell_NotifyIcon(NIM_MODIFY, ref nid);
    }

    /// <summary>图标在屏幕上的位置；图标收在「^」溢出区里时取不到。</summary>
    public RECT? GetRect()
    {
        var id = new NOTIFYICONIDENTIFIER { cbSize = (uint)Marshal.SizeOf<NOTIFYICONIDENTIFIER>(), hWnd = _hwnd, uID = IconId };
        return Shell_NotifyIconGetRect(ref id, out var r) == 0 ? r : null;
    }

    /// <summary>弹 Win32 右键菜单，返回选中的命令 id，没选返回 0。</summary>
    public int ShowMenu(int x, int y, IEnumerable<(int Id, string? Text, bool Checked, bool Enabled)> items)
    {
        var menu = CreatePopupMenu();
        foreach (var (id, text, isChecked, enabled) in items)
        {
            if (text is null)
            {
                AppendMenu(menu, MF_SEPARATOR, 0, null);
            }
            else
            {
                AppendMenu(menu, MF_STRING | (isChecked ? MF_CHECKED : 0) | (enabled ? 0 : MF_GRAYED), (nuint)id, text);
            }
        }

        // 不先把自己的窗口切到前台，点菜单外面时菜单不会消失。
        SetForegroundWindow(_hwnd);
        var cmd = TrackPopupMenuEx(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON | TPM_BOTTOMALIGN | TPM_RIGHTALIGN, x, y, _hwnd, 0);
        PostMessage(_hwnd, WM_NULL, 0, 0);
        DestroyMenu(menu);
        return (int)cmd;
    }

    public void Dispose()
    {
        if (_added)
        {
            var nid = Data(0);
            Shell_NotifyIcon(NIM_DELETE, ref nid);
            _added = false;
        }

        if (_icon != 0)
        {
            DestroyIcon(_icon);
            _icon = 0;
        }

        DestroyWindow(_hwnd);
    }

    public static bool TaskbarIsLight()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("SystemUsesLightTheme") is int v && v != 0;
    }

    private void RenderIcon()
    {
        var size = GetSystemMetricsForDpi(SM_CXSMICON, GetDpiForSystem());
        var icon = _render(TaskbarIsLight(), size);
        if (icon != 0)
        {
            if (_icon != 0)
            {
                DestroyIcon(_icon);
            }

            _icon = icon;
        }
    }

    private NOTIFYICONDATA Data(uint flags) => new()
    {
        cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = _hwnd,
        uID = IconId,
        uFlags = flags,
        uCallbackMessage = CallbackMessage,
        hIcon = _icon,
        szTip = _tooltip,
        szInfo = "",
        szInfoTitle = "",
    };

    private void Add()
    {
        var nid = Data(NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP);
        _added = Shell_NotifyIcon(NIM_ADD, ref nid);
        nid.uVersion = NOTIFYICON_VERSION_4;
        Shell_NotifyIcon(NIM_SETVERSION, ref nid);
    }

    private nint Proc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        if (msg == CallbackMessage)
        {
            switch ((int)(lParam & 0xFFFF))
            {
                case NIN_SELECT:
                case NIN_KEYSELECT:
                    Invoked?.Invoke();
                    break;
                case WM_CONTEXTMENU:
                    ContextMenuRequested?.Invoke((short)(wParam & 0xFFFF), (short)((wParam >> 16) & 0xFFFF));
                    break;
                case NIN_BALLOONUSERCLICK:
                    BalloonClicked?.Invoke();
                    break;
            }

            return 0;
        }

        if (msg == TaskbarCreated)
        {
            Add();
            return 0;
        }

        if (msg == WM_SETTINGCHANGE && lParam != 0 && Marshal.PtrToStringUni(lParam) == "ImmersiveColorSet")
        {
            Redraw();
        }

        return DefWindowProc(hwnd, msg, wParam, lParam);
    }
}
