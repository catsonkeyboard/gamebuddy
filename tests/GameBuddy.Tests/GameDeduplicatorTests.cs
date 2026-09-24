using System.IO;
using GameBuddy.Models;
using GameBuddy.Services.Scanning;

namespace GameBuddy.Tests;

/// <summary>
/// 跨源去重是"会改数据"的逻辑，宁可漏判也不能误合并，所以每个判定条件都单独锁死。
/// </summary>
public sealed class GameDeduplicatorTests : IDisposable
{
    private readonly string _root;

    public GameDeduplicatorTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "gb_dedup_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "steamapps", "common", "Dota 2"));
        Directory.CreateDirectory(Path.Combine(_root, "steamapps", "common", "Dota 2", "game"));
    }

    [Theory]
    [InlineData("Dota 2", "dota2")]
    [InlineData("Divinity: Original Sin 2", "divinityoriginalsin2")]
    [InlineData("The Farmer Was Replaced", "thefarmerwasreplaced")]
    [InlineData("Stardew  Valley", "stardewvalley")]
    public void NormalizeName_StripsPunctuationCaseAndSpaces(string input, string expected)
        => Assert.Equal(expected, GameDeduplicator.NormalizeName(input));

    [Fact]
    public void NormalizeName_HandlesEmpty()
    {
        Assert.Equal(string.Empty, GameDeduplicator.NormalizeName(null));
        Assert.Equal(string.Empty, GameDeduplicator.NormalizeName("   "));
    }

    [Fact]
    public void IsMissingInstall_NoPathEvidence_IsNeverMissing()
    {
        var game = new Game { Name = "只有启动协议", Source = GameSourceKind.Manual, LaunchUri = "steam://rungameid/570" };
        Assert.False(GameDeduplicator.IsMissingInstall(game));
    }

    [Fact]
    public void IsMissingInstall_DetectsDeletedDirectory()
    {
        var existing = new Game { InstallDirectory = Path.Combine(_root, "steamapps", "common", "Dota 2") };
        var gone = new Game { InstallDirectory = Path.Combine(_root, "steamapps", "common", "已删除的游戏") };

        Assert.False(GameDeduplicator.IsMissingInstall(existing));
        Assert.True(GameDeduplicator.IsMissingInstall(gone));
    }

    [Fact]
    public void IsPathInside_ComparesCaseAndSeparatorsInsensitively()
    {
        var parent = Path.Combine(_root, "steamapps", "common", "Dota 2");
        var child = Path.Combine(parent, "game");
        var sibling = Path.Combine(_root, "steamapps", "common", "Other");

        Assert.True(GameDeduplicator.IsPathInside(child, parent));
        Assert.True(GameDeduplicator.IsPathInside(parent, parent));
        Assert.False(GameDeduplicator.IsPathInside(parent, child));
        Assert.False(GameDeduplicator.IsPathInside(sibling, parent));
        Assert.False(GameDeduplicator.IsPathInside(null, parent));
    }

    [Fact]
    public void IsPathInside_TreatsEquivalentSpellingsAsSameDirectory()
    {
        // 用户可能把监控目录填成 "c://Games"、带尾斜杠、或大小写不同，必须都认成同一个目录
        var canonical = Path.Combine(_root, "steamapps", "common", "Dota 2");
        var child = Path.Combine(canonical, "game");

        Assert.True(GameDeduplicator.IsPathInside(child, canonical));
        Assert.True(GameDeduplicator.IsPathInside(canonical + "\\", canonical));
        Assert.True(GameDeduplicator.IsPathInside(canonical.ToUpperInvariant(), canonical));
        Assert.True(GameDeduplicator.IsPathInside(canonical.Replace('\\', '/'), canonical));
        Assert.True(GameDeduplicator.IsPathInside(
            Path.Combine(canonical, "game", ".."), canonical));
        Assert.True(GameDeduplicator.IsPathInside(child.Replace('\\', '/'), canonical.Replace('\\', '/')));
    }

    [Fact]
    public void FindDuplicates_MatchesSameExecutableAcrossSources()
    {
        var exe = Path.Combine(_root, "steamapps", "common", "Dota 2", "game", "dota2.exe");
        var steam = new Game
        {
            Name = "Dota 2", Source = GameSourceKind.Steam, ExternalId = "570",
            InstallDirectory = Path.Combine(_root, "steamapps", "common", "Dota 2"),
            ExecutablePath = exe, SteamAppId = 570
        };
        var folder = new Game
        {
            Name = "Dota 2", Source = GameSourceKind.Folder, ExternalId = exe.ToLowerInvariant(),
            InstallDirectory = Path.Combine(_root, "steamapps", "common", "Dota 2"),
            ExecutablePath = exe
        };

        var result = GameDeduplicator.FindDuplicates(new[] { steam, folder });

        var pair = Assert.Single(result);
        Assert.Equal(DuplicateReason.SameExecutable, pair.Reason);
        Assert.Equal(GameSourceKind.Steam, pair.Primary.Source);
        Assert.Equal(GameSourceKind.Folder, pair.Secondary.Source);
    }

    [Fact]
    public void FindDuplicates_MatchesWhenInstallPathOverlapsAndNameMatches()
    {
        var steamDir = Path.Combine(_root, "steamapps", "common", "Dota 2");
        var steam = new Game
        {
            Name = "Dota 2", Source = GameSourceKind.Steam, ExternalId = "570",
            InstallDirectory = steamDir
        };
        var folder = new Game
        {
            Name = "Dota 2", Source = GameSourceKind.Folder, ExternalId = Path.Combine(steamDir, "game"),
            InstallDirectory = Path.Combine(steamDir, "game")
        };

        var result = GameDeduplicator.FindDuplicates(new[] { steam, folder });

        Assert.Single(result);
        Assert.Equal(DuplicateReason.InstallPathOverlap, result[0].Reason);
    }

    [Fact]
    public void FindDuplicates_DoesNotMergeDifferentGames()
    {
        var a = new Game
        {
            Name = "Dota 2", Source = GameSourceKind.Steam, ExternalId = "570",
            InstallDirectory = Path.Combine(_root, "steamapps", "common", "Dota 2")
        };
        var b = new Game
        {
            Name = "Stardew Valley", Source = GameSourceKind.Steam, ExternalId = "413150",
            InstallDirectory = Path.Combine(_root, "steamapps", "common", "Stardew Valley")
        };

        Assert.Empty(GameDeduplicator.FindDuplicates(new[] { a, b }));
    }

    [Fact]
    public void FindDuplicates_DoesNotMergeSameNameInUnrelatedFolders()
    {
        // 同名但路径互不包含：保守起见不合并（例如两个不同版本的同名游戏装在不同盘）
        var a = new Game
        {
            Name = "My Game", Source = GameSourceKind.Folder, ExternalId = @"D:\a\game.exe",
            InstallDirectory = @"D:\a"
        };
        var b = new Game
        {
            Name = "My Game", Source = GameSourceKind.Folder, ExternalId = @"E:\b\game.exe",
            InstallDirectory = @"E:\b"
        };

        Assert.Empty(GameDeduplicator.FindDuplicates(new[] { a, b }));
    }

    [Fact]
    public void Score_PrefersInstalledEntry()
    {
        var installed = new Game { Source = GameSourceKind.Folder, InstallDirectory = _root, ExternalId = "a" };
        var uninstalled = new Game
        {
            Source = GameSourceKind.Steam, SteamAppId = 570,
            InstallDirectory = Path.Combine(_root, "不存在"), ExternalId = "b"
        };

        Assert.True(GameDeduplicator.Score(installed) > GameDeduplicator.Score(uninstalled));
    }

    [Fact]
    public void Score_PrefersExecutableMatchingGameName()
    {
        // 同一目录里既有真正的游戏本体，也有随包分发的工具 exe，必须挑本体做保留项
        var real = new Game
        {
            Name = "Stardew Valley", Source = GameSourceKind.Folder, ExternalId = "a",
            InstallDirectory = _root,
            ExecutablePath = Path.Combine(_root, "Stardew Valley.exe")
        };
        var tool = new Game
        {
            Name = "Stardew Valley", Source = GameSourceKind.Folder, ExternalId = "b",
            InstallDirectory = _root,
            ExecutablePath = Path.Combine(_root, "createdump.exe")
        };

        Assert.True(GameDeduplicator.Score(real) > GameDeduplicator.Score(tool));

        var pair = Assert.Single(GameDeduplicator.FindDuplicates(new[] { real, tool }));
        Assert.Equal("Stardew Valley.exe", Path.GetFileName(pair.Primary.ExecutablePath));
    }

    [Fact]
    public void MergeInto_KeepsPrimaryAndFillsBlanks()
    {
        var primary = new Game
        {
            Name = "Dota 2", Source = GameSourceKind.Steam, ExternalId = "570", SteamAppId = 570,
            InstallDirectory = Path.Combine(_root, "steamapps", "common", "Dota 2"),
            Tags = { "MOBA" }, GroupIds = { "g1" }
        };
        var secondary = new Game
        {
            Name = "Dota 2", Source = GameSourceKind.Folder, ExternalId = "exe",
            ExecutablePath = Path.Combine(_root, "steamapps", "common", "Dota 2", "game", "dota2.exe"),
            Developers = "Valve",
            Tags = { "常玩", "MOBA" },
            GroupIds = { "g2" },
            IsFavorite = true
        };

        GameDeduplicator.MergeInto(primary, secondary);

        Assert.Equal("570", primary.ExternalId);
        Assert.Equal(570, primary.SteamAppId);
        Assert.Equal(secondary.ExecutablePath, primary.ExecutablePath);
        Assert.Equal("Valve", primary.Developers);
        Assert.Equal(new[] { "MOBA", "常玩" }, primary.Tags);
        Assert.Equal(new[] { "g1", "g2" }, primary.GroupIds);
        Assert.True(primary.IsFavorite);
    }

    [Fact]
    public void IgnoreKey_IsStable()
    {
        Assert.Equal("Steam:570", GameDeduplicator.IgnoreKey(GameSourceKind.Steam, "570"));
        Assert.Equal("Folder:", GameDeduplicator.IgnoreKey(GameSourceKind.Folder, null));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // 清理失败不影响测试结果
        }
    }
}
