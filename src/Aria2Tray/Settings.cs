using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace Aria2Tray;

/// <summary>
/// 本机设置，存在 %LOCALAPPDATA%\Aria2Tray\settings.json。密钥按「这台机器的这个用户」用 DPAPI 加密，拷到别处解不开。
/// </summary>
internal sealed class Settings
{
    public static readonly string DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Aria2Tray");
    private static readonly string FilePath = Path.Combine(DataDir, "settings.json");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "Aria2Tray";

    /// <summary>aria2 的 RPC 地址，如 http://192.168.1.10:6800/jsonrpc；http、https、ws、wss 都行。</summary>
    public string Url { get; set; } = "";

    /// <summary>DPAPI 加密后的 rpc-secret，Base64；空串表示 aria2 没设密钥。</summary>
    public string ProtectedSecret { get; set; } = "";

    /// <summary>界面语言："auto"（跟 Windows）/ "zh" / "en"。</summary>
    public string Language { get; set; } = "auto";

    /// <summary>文件位置映射，aria2 那台机器上的目录，如 /downloads。留空就用 aria2 的全局下载目录（连上后自动取到）。</summary>
    public string RemoteDir { get; set; } = "";

    /// <summary>文件位置映射，<see cref="RemoteDir"/> 在这台电脑上是哪里，如 \\nas\Download。填了才能打开下载到的文件。</summary>
    public string LocalDir { get; set; } = "";

    /// <summary>映射用的 aria2 那一边：自己填的，没填就是 aria2 的下载目录。</summary>
    public string EffectiveRemoteDir(string? aria2Dir) => RemoteDir.Trim().Length > 0 ? RemoteDir.Trim() : aria2Dir ?? "";

    /// <summary>
    /// aria2 的路径换成本机路径：以 aria2 那一边的目录开头（按目录比，/downloads 不匹配 /downloads2）就换成 LocalDir，斜杠换成反斜杠。
    /// 没设映射或不在这个目录下是 null。
    /// </summary>
    public string? ToLocal(string remote, string? aria2Dir)
    {
        var from = EffectiveRemoteDir(aria2Dir).Replace('\\', '/').TrimEnd('/');
        var to = LocalDir.Trim().TrimEnd('\\', '/');
        var path = remote.Replace('\\', '/');
        if (from.Length == 0 || to.Length == 0 || !(path == from || path.StartsWith(from + "/", StringComparison.Ordinal)))
        {
            return null;
        }

        return to + path[from.Length..].Replace('/', '\\');
    }

    /// <summary>任务排序："default"（aria2 给的顺序）/ "name" / "size" / "progress" / "speed"。</summary>
    public string SortBy { get; set; } = "default";

    /// <summary>降序（大的、快的、Z 在前）。</summary>
    public bool SortDescending { get; set; }

    /// <summary>下载完成、出错时弹通知。</summary>
    public bool Notify { get; set; } = true;

    /// <summary>面板尺寸，单位是 DIP（100% 缩放下的像素）。</summary>
    public int Width { get; set; } = 380;

    public int Height { get; set; } = 560;

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsConfigured => Aria2.Aria2Connection.ToWebSocketUri(Url) is not null;

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
        }

        return new Settings();
    }

    public void Save()
    {
        Directory.CreateDirectory(DataDir);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOptions));
        File.Move(tmp, FilePath, overwrite: true);
    }

    public string GetSecret()
    {
        if (ProtectedSecret.Length == 0)
        {
            return "";
        }

        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(ProtectedSecret), null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return "";
        }
    }

    public void SetSecret(string secret)
    {
        if (secret.Length == 0)
        {
            ProtectedSecret = "";
            return;
        }

        var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), null, DataProtectionScope.CurrentUser);
        ProtectedSecret = Convert.ToBase64String(bytes);
    }

    public static bool StartWithWindows
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(RunValue) is string;
        }

        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value)
            {
                key.SetValue(RunValue, $"\"{Environment.ProcessPath}\"");
            }
            else
            {
                key.DeleteValue(RunValue, throwOnMissingValue: false);
            }
        }
    }
}
