using System.IO;
using GameBuddy.Models;

namespace GameBuddy.Services.Scanning;

public enum DuplicateReason
{
    /// <summary>两条指向同一个可执行文件。</summary>
    SameExecutable,

    /// <summary>名称归一化后相同，且安装路径存在包含关系。</summary>
    InstallPathOverlap
}

public sealed record DuplicateCandidate(Game Primary, Game Secondary, DuplicateReason Reason)
{
    public string Description => Reason switch
    {
        DuplicateReason.SameExecutable => $"同一主程序：{Primary.ExecutablePath}",
        _ => $"同一安装目录：{Primary.InstallDirectory ?? Primary.ExecutablePath}"
    };
}

/// <summary>
/// 跨源去重与安装状态判定。
///
/// 为什么需要它：库里同一个游戏可能被多个来源同时扫到（例如 Steam 库目录又被加进"监控目录"），
/// 而 LibraryService 的合并键是「Source + ExternalId」，跨源时必然产生两条独立条目，
/// 各自计时、各自抓元数据，统计数据因此全部失真。
///
/// 判定策略刻意保守：只在"同一 exe"或"名称相同且路径互相包含"时才认为是同一个游戏，宁可漏判也不错合。
/// </summary>
public static class GameDeduplicator
{
    /// <summary>忽略用的唯一键，格式 "Source:ExternalId"。</summary>
    public static string IgnoreKey(GameSourceKind source, string? externalId)
        => $"{source}:{externalId ?? string.Empty}";

    /// <summary>名称归一化：只保留字母与数字并转小写，用于跨来源比较。</summary>
    public static string NormalizeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;

        var buffer = new System.Text.StringBuilder(name.Length);
        foreach (var c in name)
        {
            if (char.IsLetterOrDigit(c)) buffer.Append(char.ToLowerInvariant(c));
        }

        return buffer.ToString();
    }

    /// <summary>是否记录了任何安装位置信息（目录或主程序）。</summary>
    public static bool HasPathEvidence(Game game)
        => !string.IsNullOrWhiteSpace(game.InstallDirectory) || !string.IsNullOrWhiteSpace(game.ExecutablePath);

    /// <summary>
    /// 安装证据存在但文件/目录已不在 → 判定为已卸载。
    /// 完全没有路径证据的条目（如手动添加的纯启动协议）返回 false，避免误标。
    /// </summary>
    public static bool IsMissingInstall(Game game)
    {
        if (!HasPathEvidence(game)) return false;

        if (!string.IsNullOrWhiteSpace(game.InstallDirectory) && !Directory.Exists(game.InstallDirectory))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(game.ExecutablePath) && !File.Exists(game.ExecutablePath))
        {
            return true;
        }

        return false;
    }

    /// <summary>找出所有高置信度的重复对，Primary 是建议保留的那一条。</summary>
    public static IReadOnlyList<DuplicateCandidate> FindDuplicates(IReadOnlyList<Game> games)
    {
        var result = new List<DuplicateCandidate>();
        var list = games.Where(g => !string.IsNullOrWhiteSpace(g.ExternalId)).ToList();

        for (var i = 0; i < list.Count; i++)
        {
            for (var j = i + 1; j < list.Count; j++)
            {
                var reason = Match(list[i], list[j]);
                if (reason is null) continue;

                var (primary, secondary) = Order(list[i], list[j]);
                result.Add(new DuplicateCandidate(primary, secondary, reason.Value));
            }
        }

        return result;
    }

    private static DuplicateReason? Match(Game a, Game b)
    {
        if (!string.IsNullOrWhiteSpace(a.ExecutablePath) &&
            string.Equals(a.ExecutablePath, b.ExecutablePath, StringComparison.OrdinalIgnoreCase))
        {
            return DuplicateReason.SameExecutable;
        }

        var nameA = NormalizeName(a.DisplayTitle);
        var nameB = NormalizeName(b.DisplayTitle);
        if (nameA.Length == 0 || !string.Equals(nameA, nameB, StringComparison.Ordinal)) return null;

        return IsPathInside(a.InstallDirectory, b.InstallDirectory)
               || IsPathInside(b.InstallDirectory, a.InstallDirectory)
               || IsPathInside(a.ExecutablePath, b.InstallDirectory)
               || IsPathInside(b.ExecutablePath, a.InstallDirectory)
            ? DuplicateReason.InstallPathOverlap
            : null;
    }

    /// <summary>inner 是否等于 outer 或位于其下。</summary>
    public static bool IsPathInside(string? inner, string? outer)
    {
        if (string.IsNullOrWhiteSpace(inner) || string.IsNullOrWhiteSpace(outer)) return false;

        var a = NormalizePath(inner);
        var b = NormalizePath(outer);
        if (a.Length == 0 || b.Length == 0) return false;

        return a.Equals(b, StringComparison.Ordinal) || a.StartsWith(b + "\\", StringComparison.Ordinal);
    }

    /// <summary>
    /// 路径归一化：先统一分隔符，再用 GetFullPath 消掉 "c://x"、"a\.\b"、"a\..\b" 这类写法差异，
    /// 否则同一个目录的不同写法会被当成两个路径，去重就失效了。
    /// </summary>
    private static string NormalizePath(string path)
    {
        var trimmed = path.Trim();
        if (trimmed.Length == 0) return string.Empty;

        try
        {
            return Path.GetFullPath(trimmed).TrimEnd('\\').ToLowerInvariant();
        }
        catch
        {
            // 非法路径（含非法字符等）：退回纯字符串比较，至少还能处理大小写与分隔符
            return trimmed.Replace('/', '\\').TrimEnd('\\').ToLowerInvariant();
        }
    }

    /// <summary>按"信息完整度"挑出保留项：已安装的 > 来源更权威的 > 有 AppId/启动协议/元数据的。</summary>
    public static int Score(Game g)
    {
        var score = IsMissingInstall(g) ? 0 : 1000;

        score += g.Source switch
        {
            GameSourceKind.Steam => 40,
            GameSourceKind.Epic => 30,
            GameSourceKind.Folder => 20,
            _ => 10
        };

        if (g.SteamAppId.HasValue) score += 30;
        if (!string.IsNullOrWhiteSpace(g.LaunchUri)) score += 20;
        if (g.MetadataStatus == MetadataStatus.Ok) score += 20;
        if (!string.IsNullOrWhiteSpace(g.ExecutablePath))
        {
            score += 10;

            // 主程序名与游戏名一致 → 更可能是真正的游戏本体，而不是随包分发的工具 exe（createdump 之类）
            var exeName = NormalizeName(Path.GetFileNameWithoutExtension(g.ExecutablePath));
            if (exeName.Length > 0 && exeName == NormalizeName(g.DisplayTitle)) score += 25;
        }

        return score;
    }

    private static (Game Primary, Game Secondary) Order(Game a, Game b)
    {
        var sa = Score(a);
        var sb = Score(b);
        if (sa != sb) return sa > sb ? (a, b) : (b, a);

        return a.AddedAt <= b.AddedAt ? (a, b) : (b, a);
    }

    /// <summary>
    /// 把 secondary 的信息并入 primary（不处理会话迁移，那一步需要访问库数据，由 LibraryService 完成）。
    /// 原则：primary 已有值的不覆盖；标签与分组取并集；收藏/隐藏取或。
    /// </summary>
    public static void MergeInto(Game primary, Game secondary)
    {
        primary.Name = string.IsNullOrWhiteSpace(primary.Name) ? secondary.Name : primary.Name;
        primary.DisplayName ??= secondary.DisplayName;
        primary.InstallDirectory ??= secondary.InstallDirectory;
        primary.ExecutablePath ??= secondary.ExecutablePath;
        primary.LaunchUri ??= secondary.LaunchUri;
        primary.SteamAppId ??= secondary.SteamAppId;
        primary.InstallSizeBytes ??= secondary.InstallSizeBytes;

        primary.PosterPath ??= secondary.PosterPath;
        primary.PosterUrl ??= secondary.PosterUrl;
        primary.HeaderUrl ??= secondary.HeaderUrl;
        primary.ShortDescription ??= secondary.ShortDescription;
        primary.Description ??= secondary.Description;
        primary.Developers ??= secondary.Developers;
        primary.Publishers ??= secondary.Publishers;
        primary.ReleaseDate ??= secondary.ReleaseDate;
        primary.Website ??= secondary.Website;

        if (primary.Genres.Count == 0) primary.Genres.AddRange(secondary.Genres);
        if (primary.MetadataStatus != MetadataStatus.Ok)
        {
            primary.MetadataStatus = secondary.MetadataStatus;
            primary.MetadataFetchedAt = secondary.MetadataFetchedAt;
        }

        foreach (var tag in secondary.Tags.Where(t => !primary.Tags.Contains(t))) primary.Tags.Add(tag);
        foreach (var groupId in secondary.GroupIds.Where(g => !primary.GroupIds.Contains(g))) primary.GroupIds.Add(groupId);

        primary.IsFavorite |= secondary.IsFavorite;
        primary.IsHidden |= secondary.IsHidden;
        primary.Notes ??= secondary.Notes;

        primary.LastPlayedAt = Max(primary.LastPlayedAt, secondary.LastPlayedAt);
        primary.AddedAt = Min(primary.AddedAt, secondary.AddedAt);
    }

    private static DateTime? Max(DateTime? a, DateTime? b)
    {
        if (a is null) return b;
        if (b is null) return a;
        return a > b ? a : b;
    }

    private static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;
}
