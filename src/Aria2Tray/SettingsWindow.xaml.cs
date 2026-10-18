using System.Runtime.InteropServices;
using Aria2Tray.Aria2;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using static Aria2Tray.Native.Win32;

namespace Aria2Tray;

/// <summary>
/// aria2 地址、密钥、文件位置映射、界面语言、面板尺寸、通知、开机启动。地址或密钥改了，保存前先真连一次，连不上或密钥不对就留在窗口里说清楚。
/// </summary>
internal sealed partial class SettingsWindow : Window
{
    private static readonly string[] Languages = ["auto", "zh", "en"];
    private readonly Settings _settings;

    /// <param name="aria2Dir">连着的 aria2 的下载目录（没连上是 null）：「aria2 上的下载目录」留空时用它，框里拿它当提示显示。</param>
    public SettingsWindow(Settings settings, string? aria2Dir)
    {
        InitializeComponent();
        _settings = settings;
        SystemBackdrop = new MicaBackdrop();
        Title = L.T("Aria2 Tray 设置", "Aria2 Tray settings");
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "Aria2Tray.ico"));
        if (AppWindow.Presenter is OverlappedPresenter p)
        {
            p.IsMaximizable = false;
            p.IsMinimizable = false;
            p.IsResizable = false;
        }

        TitleText.Text = L.T("Aria2 Tray 设置", "Aria2 Tray settings");
        UrlBox.Header = L.T("aria2 RPC 地址", "aria2 RPC address");
        UrlBox.Text = settings.Url;
        SecretBox.Header = L.T("RPC 密钥", "RPC secret");
        SecretBox.PlaceholderText = settings.ProtectedSecret.Length > 0 ? L.T("已保存，留空不改", "Saved. Leave empty to keep it") : L.T("没设密钥就留空", "Leave empty if there is none");
        LocationTitle.Text = L.T("文件位置", "File location");
        // 要填的只有本机这一边；aria2 那一边留空就用 aria2 自己的下载目录（2026-10-18 用户：程序明明取得到，为啥还要填）。
        LocalDirBox.Header = L.T("下载目录在这台电脑上的位置", "Download folder on this computer");
        LocalDirBox.Text = settings.LocalDir;
        RemoteDirBox.Header = L.T("aria2 上的下载目录", "Download folder on the aria2 machine");
        RemoteDirBox.Text = settings.RemoteDir;
        RemoteDirBox.PlaceholderText = aria2Dir ?? "/downloads";
        LanguageBox.Header = L.T("界面语言", "Language");
        LanguageBox.Items.Add(L.T("自动（跟随 Windows）", "Auto (same as Windows)"));
        LanguageBox.Items.Add("中文");
        LanguageBox.Items.Add("English");
        LanguageBox.SelectedIndex = Math.Max(0, Array.IndexOf(Languages, settings.Language));
        WidthBox.Header = L.T("面板宽度", "Panel width");
        WidthBox.Value = settings.Width;
        HeightBox.Header = L.T("面板高度", "Panel height");
        HeightBox.Value = settings.Height;
        NotifySwitch.Header = L.T("下载完成、出错时通知", "Notify when a download finishes or fails");
        NotifySwitch.IsOn = settings.Notify;
        StartupSwitch.Header = L.T("开机时启动", "Start with Windows");
        StartupSwitch.IsOn = Settings.StartWithWindows;
        SaveButton.Content = L.T("保存", "Save");
        CancelButton.Content = L.T("取消", "Cancel");

        // 按屏幕缩放定尺寸，放在鼠标所在屏幕的中间。
        GetCursorPos(out var pt);
        var monitor = MonitorFromPoint(pt, MONITOR_DEFAULTTONEAREST);
        GetDpiForMonitor(monitor, 0, out var dpi, out _);
        var info = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(monitor, ref info);
        var scale = dpi / 96.0;
        var size = new SizeInt32((int)(500 * scale), (int)(720 * scale));
        var work = info.rcWork;
        AppWindow.MoveAndResize(new RectInt32(
            work.Left + ((work.Right - work.Left - size.Width) / 2),
            work.Top + ((work.Bottom - work.Top - size.Height) / 2),
            size.Width,
            size.Height));
    }

    /// <summary>保存成功。参数表示地址或密钥变了，要重连。</summary>
    public event Action<bool>? Saved;

    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        ErrorText.Text = "";
        var url = UrlBox.Text.Trim();
        if (Aria2Connection.ToWebSocketUri(url) is not { } uri)
        {
            ErrorText.Text = L.T("地址要以 http:// 或 ws:// 开头，比如 http://192.168.1.10:6800/jsonrpc", "Enter an address starting with http:// or ws://, like http://192.168.1.10:6800/jsonrpc");
            return;
        }

        var newSecret = SecretBox.Password.Trim();
        var secret = newSecret.Length > 0 ? newSecret : _settings.GetSecret();
        var reconnect = url != _settings.Url || newSecret.Length > 0;
        if (reconnect)
        {
            SaveButton.IsEnabled = false;
            Busy.IsActive = true;
            var error = await Aria2Connection.TestAsync(uri, secret);
            Busy.IsActive = false;
            SaveButton.IsEnabled = true;
            if (error is not null)
            {
                ErrorText.Text = error;
                return;
            }
        }

        _settings.Url = url;
        if (newSecret.Length > 0)
        {
            _settings.SetSecret(newSecret);
        }

        _settings.RemoteDir = RemoteDirBox.Text.Trim();
        _settings.LocalDir = LocalDirBox.Text.Trim();
        _settings.Language = Languages[Math.Max(0, LanguageBox.SelectedIndex)];
        _settings.Width = double.IsNaN(WidthBox.Value) ? 380 : (int)WidthBox.Value;
        _settings.Height = double.IsNaN(HeightBox.Value) ? 560 : (int)HeightBox.Value;
        _settings.Notify = NotifySwitch.IsOn;
        _settings.Save();
        try
        {
            Settings.StartWithWindows = StartupSwitch.IsOn;
        }
        catch (UnauthorizedAccessException)
        {
        }

        Saved?.Invoke(reconnect);
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => Close();
}
