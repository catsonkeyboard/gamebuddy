using GameBuddy.Models;

namespace GameBuddy.Services.Scanning;

/// <summary>扫描器产出的原始条目，后续由 LibraryService 合并进库。</summary>
public sealed class ScannedGame
{
    public GameSourceKind Source { get; init; }
    public string Name { get; init; } = string.Empty;
    public string ExternalId { get; init; } = string.Empty;
    public int? SteamAppId { get; init; }
    public string? InstallDirectory { get; init; }
    public string? ExecutablePath { get; init; }
    public string? LaunchUri { get; init; }
    public long? InstallSizeBytes { get; init; }
}

public sealed class ScanOptions
{
    public IReadOnlyList<string> WatchFolders { get; init; } = Array.Empty<string>();
    public string? SteamPathOverride { get; init; }
    public string? EpicManifestsPathOverride { get; init; }
}

public interface IGameScanner
{
    GameSourceKind Source { get; }
    Task<IReadOnlyList<ScannedGame>> ScanAsync(ScanOptions options, CancellationToken ct = default);
}
