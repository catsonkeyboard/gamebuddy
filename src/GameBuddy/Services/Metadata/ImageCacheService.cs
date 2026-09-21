using System.IO;
using System.Net.Http;
using GameBuddy.Services.Diagnostics;
using GameBuddy.Services.Storage;

namespace GameBuddy.Services.Metadata;

public interface IImageCache
{
    Task<string?> GetOrDownloadAsync(string url, CancellationToken ct = default);
    string? GetCachedPath(string url);
}

/// <summary>把远程海报下载到本地缓存目录，避免每次启动反复请求 CDN。</summary>
public sealed class ImageCacheService : IImageCache
{
    private readonly HttpClient _http;

    public ImageCacheService(HttpClient http)
    {
        _http = http;
    }

    public string? GetCachedPath(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var file = Path.Combine(AppPaths.PosterCacheDir, JsonLibraryRepository.Hash(url) + GuessExtension(url));
        return File.Exists(file) ? file : null;
    }

    public async Task<string?> GetOrDownloadAsync(string url, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;

        var existing = GetCachedPath(url);
        if (existing is not null) return existing;

        try
        {
            Directory.CreateDirectory(AppPaths.PosterCacheDir);
            var file = Path.Combine(AppPaths.PosterCacheDir, JsonLibraryRepository.Hash(url) + GuessExtension(url));
            var bytes = await _http.GetByteArrayAsync(url, ct).ConfigureAwait(false);
            if (bytes.Length < 1024) return null;
            await File.WriteAllBytesAsync(file, bytes, ct).ConfigureAwait(false);
            return file;
        }
        catch (Exception ex)
        {
            AppLog.Error($"下载图片失败: {url}", ex);
            return null;
        }
    }

    private static string GuessExtension(string url)
    {
        var uri = new Uri(url);
        var ext = Path.GetExtension(uri.AbsolutePath);
        return string.IsNullOrWhiteSpace(ext) ? ".jpg" : ext;
    }
}
