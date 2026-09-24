namespace GameBuddy.Models;

public enum GameSourceKind
{
    Steam,
    Epic,
    Folder,
    Manual
}

public enum MetadataStatus
{
    None,
    Pending,
    Ok,
    Failed
}

/// <summary>
/// 库中的一个游戏条目。既保存本地安装信息，也保存从 Steam 抓取的元数据与用户自定义的分组/标签。
/// </summary>
public sealed class Game
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public GameSourceKind Source { get; set; }

    /// <summary>扫描得到的原始名称。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>用户可改写的显示名，为空时使用 <see cref="Name"/>。</summary>
    public string? DisplayName { get; set; }

    public string DisplayTitle => string.IsNullOrWhiteSpace(DisplayName) ? Name : DisplayName;

    public string? InstallDirectory { get; set; }

    public string? ExecutablePath { get; set; }

    /// <summary>启动协议，例如 steam://rungameid/570 或 com.epicgames.launcher://apps/... 。</summary>
    public string? LaunchUri { get; set; }

    /// <summary>平台侧唯一 ID：Steam 为 appid，Epic 为 AppName，目录扫描为 exe 路径。</summary>
    public string? ExternalId { get; set; }

    public int? SteamAppId { get; set; }

    public long? InstallSizeBytes { get; set; }

    // ---- 元数据 ----
    public string? PosterPath { get; set; }
    public string? PosterUrl { get; set; }
    public string? HeaderUrl { get; set; }
    public string? ShortDescription { get; set; }
    public string? Description { get; set; }
    public string? Developers { get; set; }
    public string? Publishers { get; set; }
    public string? ReleaseDate { get; set; }
    public string? Website { get; set; }
    public List<string> Genres { get; set; } = new();
    public MetadataStatus MetadataStatus { get; set; } = MetadataStatus.None;
    public DateTime? MetadataFetchedAt { get; set; }

    // ---- 用户组织 ----
    public List<string> Tags { get; set; } = new();
    public List<string> GroupIds { get; set; } = new();
    public bool IsFavorite { get; set; }
    public bool IsHidden { get; set; }
    public string? Notes { get; set; }

    /// <summary>
    /// 安装位置已失效（目录或主程序被删除）。条目仍保留在库中，用户数据不丢，只是不再可启动。
    /// </summary>
    public bool IsUninstalled { get; set; }

    // ---- 统计 ----
    public DateTime AddedAt { get; set; } = DateTime.Now;
    public DateTime? LastPlayedAt { get; set; }

    public string StoreUrl => SteamAppId is { } appId
        ? $"https://store.steampowered.com/app/{appId}/"
        : string.Empty;

    public string SteamDbUrl => SteamAppId is { } appId
        ? $"https://steamdb.info/app/{appId}/"
        : string.Empty;
}
