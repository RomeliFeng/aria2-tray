using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;
using Windows.Storage.FileProperties;

namespace Aria2Tray.Ui;

/// <summary>
/// 下载到的文件的缩略图，向 Windows 要（和资源管理器里看到的同一张，Windows 自己有缓存）。文件要经「文件位置」换成本机路径才拿得到。
/// 只要真缩略图：Windows 给回来的是文件类型图标（不是视频、图片，或者文件还没下完生成不出）就当没有。
/// 按「本机路径 + 尺寸 + 版本」记在内存里，没有的也记（免得每次悬停都去 NAS 读一遍）；最多记 300 个，多了清掉重来。
/// 版本由调用方给（任务状态）：下到一半生成不出的，下完了换个版本会重取。
/// 在 UI 线程上调用（BitmapImage 要在 UI 线程建）；查文件在不在放到后台线程，网络路径连不上时要等几秒。
/// </summary>
internal static class Thumbnails
{
    private const int Limit = 300;
    private static readonly Dictionary<string, ImageSource?> Cache = [];

    public static async Task<ImageSource?> GetAsync(string localPath, uint size, string version)
    {
        var key = $"{size}|{version}|{localPath}";
        if (Cache.TryGetValue(key, out var hit))
        {
            return hit;
        }

        ImageSource? result = null;
        try
        {
            var isFile = await Task.Run(() => File.Exists(localPath));
            IStorageItem item = isFile
                ? await StorageFile.GetFileFromPathAsync(localPath)
                : await StorageFolder.GetFolderFromPathAsync(localPath);
            using var thumb = item switch
            {
                StorageFile f => await f.GetThumbnailAsync(ThumbnailMode.SingleItem, size, ThumbnailOptions.ResizeThumbnail),
                StorageFolder d => await d.GetThumbnailAsync(ThumbnailMode.SingleItem, size, ThumbnailOptions.ResizeThumbnail),
                _ => null,
            };
            if (thumb is { Type: ThumbnailType.Image })
            {
                var bitmap = new BitmapImage();
                await bitmap.SetSourceAsync(thumb);
                result = bitmap;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException or ArgumentException)
        {
        }

        if (Cache.Count >= Limit)
        {
            Cache.Clear();
        }

        Cache[key] = result;
        return result;
    }
}
