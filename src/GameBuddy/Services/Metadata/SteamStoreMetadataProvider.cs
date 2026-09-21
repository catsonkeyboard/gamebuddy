using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using GameBuddy.Services.Diagnostics;

namespace GameBuddy.Services.Metadata;

public sealed record GameMetadata(
    string? Name,
    string? ShortDescription,
    string? Description,
    string? Developers,
    string? Publishers,
    string? ReleaseDate,
    IReadOnlyList<string> Genres,
    string? PosterUrl,
    string? HeaderUrl,
    string? Website);

public interface IMetadataProvider
{
    string Name { get; }
    Task<GameMetadata?> FetchByAppIdAsync(int appId, CancellationToken ct = default);
    Task<int?> SearchAppIdAsync(string query, CancellationToken ct = default);
}

/// <summary>
/// 通过公开 Steam Store Web API 抓取游戏元数据（无需 API Key）。
/// 注意：Steam 对 storefront 接口有频率限制，这里用信号量 + 间隔做节流，并支持 429 退避。
/// </summary>
public sealed class SteamStoreMetadataProvider : IMetadataProvider
{
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _throttle = new(1, 1);
    private DateTime _lastRequest = DateTime.MinValue;
    private static readonly TimeSpan MinInterval = TimeSpan.FromMilliseconds(1100);

    public string Name => "Steam Store";

    public SteamStoreMetadataProvider(HttpClient http) => _http = http;

    public async Task<GameMetadata?> FetchByAppIdAsync(int appId, CancellationToken ct = default)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var url = $"https://store.steampowered.com/api/appdetails?appids={appId}&l=schinese&cc=us";
            var json = await GetStringWithThrottleAsync(url, ct).ConfigureAwait(false);
            if (json is null)
            {
                if (ct.IsCancellationRequested) return null;
                continue;
            }

            try
            {
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty(appId.ToString(), out var entry)) return null;
                if (!entry.TryGetProperty("success", out var ok) || ok.ValueKind != JsonValueKind.True) return null;
                if (!entry.TryGetProperty("data", out var data)) return null;

                var type = GetString(data, "type");
                if (type is not null && type != "game" && type != "demo") return null;

                var header = GetString(data, "header_image");
                var poster = $"https://cdn.cloudflare.steamstatic.com/steam/apps/{appId}/library_600x900_2x.jpg";

                return new GameMetadata(
                    Name: GetString(data, "name"),
                    ShortDescription: GetString(data, "short_description"),
                    Description: HtmlText.ToPlainText(GetString(data, "detailed_description") ?? GetString(data, "about_the_game")),
                    Developers: JoinList(data, "developers"),
                    Publishers: JoinList(data, "publishers"),
                    ReleaseDate: GetString(data, "release_date", "date"),
                    Genres: GetGenres(data),
                    PosterUrl: poster,
                    HeaderUrl: header,
                    Website: GetString(data, "website"));
            }
            catch (Exception ex)
            {
                AppLog.Error($"解析 appdetails 失败 appid={appId}", ex);
                return null;
            }
        }

        return null;
    }

    public async Task<int?> SearchAppIdAsync(string query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return null;

        var url = $"https://store.steampowered.com/api/storesearch/?term={Uri.EscapeDataString(query)}&l=schinese&cc=us";
        var json = await GetStringWithThrottleAsync(url, ct).ConfigureAwait(false);
        if (json is null) return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("items", out var items)) return null;
            foreach (var item in items.EnumerateArray())
            {
                if (item.TryGetProperty("id", out var id) && id.TryGetInt32(out var appId))
                {
                    return appId;
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Error($"解析 storesearch 失败 q={query}", ex);
        }

        return null;
    }

    private async Task<string?> GetStringWithThrottleAsync(string url, CancellationToken ct)
    {
        await _throttle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var wait = MinInterval - (DateTime.Now - _lastRequest);
            if (wait > TimeSpan.Zero) await Task.Delay(wait, ct).ConfigureAwait(false);

            for (var attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    using var response = await _http.GetAsync(url, ct).ConfigureAwait(false);
                    _lastRequest = DateTime.Now;

                    if (response.StatusCode == HttpStatusCode.TooManyRequests)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(5 * (attempt + 1)), ct).ConfigureAwait(false);
                        continue;
                    }

                    if (!response.IsSuccessStatusCode)
                    {
                        AppLog.Error($"Steam 接口返回 {(int)response.StatusCode}: {url}");
                        return null;
                    }

                    return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    AppLog.Error($"请求 Steam 接口失败: {url}", ex);
                    await Task.Delay(1200, ct).ConfigureAwait(false);
                }
            }

            return null;
        }
        finally
        {
            _throttle.Release();
        }
    }

    private static List<string> GetGenres(JsonElement data)
    {
        var list = new List<string>();
        if (data.TryGetProperty("genres", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var g in arr.EnumerateArray())
            {
                var d = GetString(g, "description");
                if (!string.IsNullOrWhiteSpace(d)) list.Add(d!);
            }
        }

        return list;
    }

    private static string? JoinList(JsonElement data, string property)
    {
        if (!data.TryGetProperty(property, out var arr) || arr.ValueKind != JsonValueKind.Array) return null;
        var values = arr.EnumerateArray()
            .Where(x => x.ValueKind == JsonValueKind.String)
            .Select(x => x.GetString())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToList();
        return values.Count == 0 ? null : string.Join(" / ", values);
    }

    private static string? GetString(JsonElement el, string name)
    {
        if (el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String)
        {
            var s = v.GetString();
            return string.IsNullOrWhiteSpace(s) ? null : s;
        }

        return null;
    }

    private static string? GetString(JsonElement el, string name, string nested)
    {
        if (el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object)
        {
            return GetString(v, nested);
        }

        return null;
    }
}

internal static class HtmlText
{
    private static readonly Regex Tag = new(@"<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex MultiSpace = new(@"\s{2,}", RegexOptions.Compiled);

    public static string? ToPlainText(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return null;
        var text = Tag.Replace(html, " ");
        text = text.Replace("&quot;", "\"")
            .Replace("&amp;", "&")
            .Replace("&lt;", "<")
            .Replace("&gt;", ">")
            .Replace("&nbsp;", " ")
            .Replace("&#39;", "'");
        text = MultiSpace.Replace(text, " ").Trim();
        return text.Length == 0 ? null : text;
    }
}
