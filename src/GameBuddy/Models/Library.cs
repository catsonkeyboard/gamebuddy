using System.Text.Json.Serialization;
using GameBuddy.Models;

namespace GameBuddy.Models;

/// <summary>一次游玩记录。</summary>
public sealed class PlaySession
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string GameId { get; set; } = string.Empty;
    public DateTime StartedAt { get; set; } = DateTime.Now;
    public DateTime? EndedAt { get; set; }

    /// <summary>是否正常结束（false 表示进程未捕获到，靠手动停止或程序退出补齐）。</summary>
    public bool Completed { get; set; }

    [JsonIgnore]
    public TimeSpan Duration => (EndedAt ?? DateTime.Now) - StartedAt;

    [JsonIgnore]
    public bool IsActive => EndedAt is null;
}

/// <summary>用户自定义分组（类似 Steam 的收藏分类）。</summary>
public sealed class GameGroup
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "新分组";
    public string ColorHex { get; set; } = "#5B8CFF";
    public string Emoji { get; set; } = "🎮";
    public int SortOrder { get; set; }
}

public sealed class LibrarySettings
{
    public string? SteamPathOverride { get; set; }
    public string? EpicManifestsPathOverride { get; set; }
    public List<string> WatchFolders { get; set; } = new();
    public string ThemeId { get; set; } = "dark";
    public bool AutoFetchMetadata { get; set; } = true;
    public bool ShowHiddenGames { get; set; }
    public double PosterWidth { get; set; } = 190;
}

/// <summary>持久化到本地 JSON 的完整库数据。</summary>
public sealed class LibraryData
{
    public int SchemaVersion { get; set; } = 1;
    public List<Game> Games { get; set; } = new();
    public List<GameGroup> Groups { get; set; } = new();
    public List<PlaySession> Sessions { get; set; } = new();
    public LibrarySettings Settings { get; set; } = new();
}
