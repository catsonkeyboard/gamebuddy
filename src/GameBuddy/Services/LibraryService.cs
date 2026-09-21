using System.IO;
using GameBuddy.Models;
using GameBuddy.Services.Diagnostics;
using GameBuddy.Services.Metadata;
using GameBuddy.Services.Scanning;
using GameBuddy.Services.Storage;

namespace GameBuddy.Services;

public sealed record ProgressReport(string Message, double? Value = null, bool IsIndeterminate = true);

/// <summary>
/// 库的业务门面：扫描 → 合并 → 元数据抓取 → 持久化。
/// </summary>
public interface ILibraryService
{
    Task<int> ScanAsync(IProgress<ProgressReport>? progress = null, CancellationToken ct = default);

    Task RefreshMetadataAsync(bool force, IProgress<ProgressReport>? progress = null, CancellationToken ct = default);

    Task<int> RefreshGameMetadataAsync(Game game, int? overrideAppId = null, CancellationToken ct = default);

    Game AddManualGame(string name, string? exePath);

    void RemoveGame(Game game);

    TimeSpan GetTotalPlayTime(string gameId);

    int GetSessionCount(string gameId);

    DateTime? GetLastPlayed(string gameId);
}

public sealed class LibraryService : ILibraryService
{
    private readonly ILibraryRepository _repository;
    private readonly IEnumerable<IGameScanner> _scanners;
    private readonly IMetadataProvider _metadata;
    private readonly IImageCache _images;

    public LibraryService(
        ILibraryRepository repository,
        IEnumerable<IGameScanner> scanners,
        IMetadataProvider metadata,
        IImageCache images)
    {
        _repository = repository;
        _scanners = scanners;
        _metadata = metadata;
        _images = images;
    }

    public async Task<int> ScanAsync(IProgress<ProgressReport>? progress = null, CancellationToken ct = default)
    {
        var options = new ScanOptions
        {
            WatchFolders = _repository.Settings.WatchFolders,
            SteamPathOverride = _repository.Settings.SteamPathOverride,
            EpicManifestsPathOverride = _repository.Settings.EpicManifestsPathOverride
        };

        var discovered = new List<ScannedGame>();
        foreach (var scanner in _scanners)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new ProgressReport($"正在扫描 {scanner.Source} ..."));
            try
            {
                var found = await scanner.ScanAsync(options, ct).ConfigureAwait(false);
                discovered.AddRange(found);
            }
            catch (Exception ex)
            {
                AppLog.Error($"{scanner.Source} 扫描失败", ex);
            }
        }

        var added = 0;
        var data = _repository.Data;

        foreach (var scanned in discovered)
        {
            var existing = data.Games.FirstOrDefault(g =>
                g.Source == scanned.Source &&
                string.Equals(g.ExternalId, scanned.ExternalId, StringComparison.OrdinalIgnoreCase));

            if (existing is null)
            {
                data.Games.Add(new Game
                {
                    Source = scanned.Source,
                    Name = scanned.Name,
                    ExternalId = scanned.ExternalId,
                    SteamAppId = scanned.SteamAppId,
                    InstallDirectory = scanned.InstallDirectory,
                    ExecutablePath = scanned.ExecutablePath,
                    LaunchUri = scanned.LaunchUri,
                    InstallSizeBytes = scanned.InstallSizeBytes,
                    AddedAt = DateTime.Now
                });
                added++;
            }
            else
            {
                existing.Name = scanned.Name;
                existing.InstallDirectory = scanned.InstallDirectory ?? existing.InstallDirectory;
                existing.ExecutablePath = scanned.ExecutablePath ?? existing.ExecutablePath;
                existing.LaunchUri = scanned.LaunchUri ?? existing.LaunchUri;
                existing.SteamAppId = scanned.SteamAppId ?? existing.SteamAppId;
                existing.InstallSizeBytes = scanned.InstallSizeBytes ?? existing.InstallSizeBytes;
            }
        }

        await _repository.SaveAsync().ConfigureAwait(false);
        return added;
    }

    public async Task RefreshMetadataAsync(bool force, IProgress<ProgressReport>? progress = null, CancellationToken ct = default)
    {
        var targets = _repository.Data.Games
            .Where(g => force || g.MetadataStatus is MetadataStatus.None or MetadataStatus.Failed)
            .ToList();

        var total = targets.Count;
        var done = 0;

        foreach (var game in targets)
        {
            ct.ThrowIfCancellationRequested();
            done++;
            progress?.Report(new ProgressReport($"抓取元数据 {done}/{total}：{game.DisplayTitle}", (double)done / total, false));
            await RefreshGameMetadataAsync(game, ct: ct).ConfigureAwait(false);
        }

        await _repository.SaveAsync().ConfigureAwait(false);
    }

    /// <summary>返回 0=成功, 1=未找到, 2=失败</summary>
    public async Task<int> RefreshGameMetadataAsync(Game game, int? overrideAppId = null, CancellationToken ct = default)
    {
        try
        {
            // 最多两轮：先用手上已知的 appid，失败再按名称搜索一次
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var appId = attempt == 0
                    ? overrideAppId ?? game.SteamAppId
                    : await _metadata.SearchAppIdAsync(game.DisplayTitle, ct).ConfigureAwait(false);

                if (appId is null)
                {
                    if (attempt == 0) continue;
                    game.MetadataStatus = MetadataStatus.Failed;
                    return 1;
                }

                game.SteamAppId = appId;

                var meta = await _metadata.FetchByAppIdAsync(appId.Value, ct).ConfigureAwait(false);
                if (meta is null)
                {
                    game.SteamAppId = null;
                    continue;
                }

                game.Name = string.IsNullOrWhiteSpace(game.DisplayName) ? meta.Name ?? game.Name : game.Name;
            game.ShortDescription = meta.ShortDescription;
            game.Description = meta.Description;
            game.Developers = meta.Developers;
            game.Publishers = meta.Publishers;
            game.ReleaseDate = meta.ReleaseDate;
            game.Genres = meta.Genres.ToList();
            game.Website = meta.Website;
            game.HeaderUrl = meta.HeaderUrl;
            game.PosterUrl = meta.PosterUrl;
            game.MetadataFetchedAt = DateTime.Now;

            foreach (var candidate in new[] { meta.PosterUrl, meta.HeaderUrl })
            {
                if (string.IsNullOrWhiteSpace(candidate)) continue;
                var local = await _images.GetOrDownloadAsync(candidate!, ct).ConfigureAwait(false);
                if (local is not null)
                {
                    game.PosterPath = local;
                    game.MetadataStatus = MetadataStatus.Ok;
                    return 0;
                }
            }

                game.MetadataStatus = MetadataStatus.Failed;
                return 1;
            }

            game.MetadataStatus = MetadataStatus.Failed;
            return 1;
        }
        catch (OperationCanceledException)
        {
            return 2;
        }
        catch (Exception ex)
        {
            AppLog.Error($"抓取 {game.DisplayTitle} 元数据失败", ex);
            game.MetadataStatus = MetadataStatus.Failed;
            return 2;
        }
    }

    public Game AddManualGame(string name, string? exePath)
    {
        var game = new Game
        {
            Source = GameSourceKind.Manual,
            Name = name,
            ExternalId = string.IsNullOrWhiteSpace(exePath) ? Guid.NewGuid().ToString("N") : exePath.ToLowerInvariant(),
            ExecutablePath = exePath,
            InstallDirectory = string.IsNullOrWhiteSpace(exePath) ? null : Path.GetDirectoryName(exePath)
        };

        _repository.Data.Games.Add(game);
        return game;
    }

    public void RemoveGame(Game game) => _repository.Data.Games.Remove(game);

    public TimeSpan GetTotalPlayTime(string gameId) => TimeSpan.FromTicks(
        _repository.Data.Sessions
            .Where(s => s.GameId == gameId)
            .Sum(s => ((s.EndedAt ?? DateTime.Now) - s.StartedAt).Ticks));

    public int GetSessionCount(string gameId) => _repository.Data.Sessions.Count(s => s.GameId == gameId);

    public DateTime? GetLastPlayed(string gameId) =>
        _repository.Data.Sessions.Where(s => s.GameId == gameId).Max(s => (DateTime?)s.StartedAt);
}
