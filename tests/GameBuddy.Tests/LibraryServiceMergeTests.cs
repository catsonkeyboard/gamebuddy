using System.IO;
using GameBuddy.Models;
using GameBuddy.Services;
using GameBuddy.Services.Metadata;
using GameBuddy.Services.Scanning;
using GameBuddy.Services.Storage;

namespace GameBuddy.Tests;

/// <summary>
/// 内存版仓储，让 LibraryService 能在测试里跑完整管道而不碰用户真实库文件。
/// </summary>
internal sealed class InMemoryLibraryRepository : ILibraryRepository
{
    public InMemoryLibraryRepository(LibraryData data) => Data = data;

    public LibraryData Data { get; }
    public LibrarySettings Settings => Data.Settings;
    public int SaveCount { get; private set; }

    public Task LoadAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task SaveAsync()
    {
        SaveCount++;
        return Task.CompletedTask;
    }

    public void Save() => SaveAsync();
}

/// <summary>什么都不做的扫描器，测试里手动往库里塞数据、只验证后处理阶段。</summary>
internal sealed class NoOpScanner : IGameScanner
{
    public GameSourceKind Source => GameSourceKind.Folder;
    public Task<IReadOnlyList<ScannedGame>> ScanAsync(ScanOptions options, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ScannedGame>>(Array.Empty<ScannedGame>());
}

internal sealed class StubMetadataProvider : IMetadataProvider
{
    public string Name => "stub";

    public Task<int?> SearchAppIdAsync(string name, CancellationToken ct = default)
        => Task.FromResult<int?>(null);

    public Task<GameMetadata?> FetchByAppIdAsync(int appId, CancellationToken ct = default)
        => Task.FromResult<GameMetadata?>(null);
}

internal sealed class StubImageCache : IImageCache
{
    public Task<string?> GetOrDownloadAsync(string url, CancellationToken ct = default)
        => Task.FromResult<string?>(null);

    public string? GetCachedPath(string url) => null;
}

public sealed class LibraryServiceMergeTests : IDisposable
{
    private readonly string _root;

    public LibraryServiceMergeTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "gb_merge_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "steamapps", "common", "Dota 2"));
        Directory.CreateDirectory(Path.Combine(_root, "steamapps", "common", "Dota 2", "game"));
    }

    private LibraryService CreateService(LibraryData data, out InMemoryLibraryRepository repo)
    {
        repo = new InMemoryLibraryRepository(data);
        return new LibraryService(
            repo,
            new IGameScanner[] { new NoOpScanner() },
            new StubMetadataProvider(),
            new StubImageCache());
    }

    [Fact]
    public async Task ScanAsync_MergesDuplicateAndMigratesSessions()
    {
        var dir = Path.Combine(_root, "steamapps", "common", "Dota 2");
        var exe = Path.Combine(dir, "game", "dota2.exe");

        var steam = new Game
        {
            Id = "steam-entry", Name = "Dota 2", Source = GameSourceKind.Steam, ExternalId = "570",
            SteamAppId = 570, InstallDirectory = dir, ExecutablePath = exe
        };
        var folder = new Game
        {
            Id = "folder-entry", Name = "Dota 2", Source = GameSourceKind.Folder, ExternalId = exe.ToLowerInvariant(),
            InstallDirectory = dir, ExecutablePath = exe
        };

        var data = new LibraryData();
        data.Games.Add(steam);
        data.Games.Add(folder);
        // 游玩记录挂在 Folder 那条上 —— 合并后必须归到保留下来的 Steam 那条，时长才不会凭空消失
        data.Sessions.Add(new PlaySession { GameId = "folder-entry", StartedAt = DateTime.Now.AddHours(-2), EndedAt = DateTime.Now.AddHours(-1) });
        data.Sessions.Add(new PlaySession { GameId = "steam-entry", StartedAt = DateTime.Now.AddMinutes(-30), EndedAt = DateTime.Now });

        var service = CreateService(data, out var repo);
        var summary = await service.ScanAsync();

        Assert.Equal(1, summary.Merged);
        Assert.Single(data.Games);
        Assert.Equal("steam-entry", data.Games[0].Id);

        // 两条会话都还在，且都指向保留条目
        Assert.Equal(2, data.Sessions.Count);
        Assert.All(data.Sessions, s => Assert.Equal("steam-entry", s.GameId));
        // 1 小时 + 30 分钟，两次 DateTime.Now 取样有微秒差，用容差比较
        var total = service.GetTotalPlayTime("steam-entry");
        Assert.InRange(total, TimeSpan.FromHours(1.5) - TimeSpan.FromSeconds(1), TimeSpan.FromHours(1.5) + TimeSpan.FromSeconds(1));

        // 被合并掉的那条进了忽略列表，否则下次扫描又会复活成两条
        Assert.Contains(GameDeduplicator.IgnoreKey(GameSourceKind.Folder, exe.ToLowerInvariant()),
            repo.Settings.IgnoredExternalIds);
    }

    [Fact]
    public async Task ScanAsync_MarksMissingInstallAndKeepsEntry()
    {
        var dir = Path.Combine(_root, "steamapps", "common", "Dota 2");
        var installed = new Game
        {
            Id = "ok", Name = "Dota 2", Source = GameSourceKind.Steam, ExternalId = "570", InstallDirectory = dir
        };
        var removed = new Game
        {
            Id = "gone", Name = "已删除的游戏", Source = GameSourceKind.Steam, ExternalId = "999",
            InstallDirectory = Path.Combine(_root, "steamapps", "common", "已删除的游戏")
        };
        var protocolOnly = new Game
        {
            Id = "protocol", Name = "只有协议", Source = GameSourceKind.Manual, ExternalId = "x",
            LaunchUri = "steam://rungameid/570"
        };

        var data = new LibraryData();
        data.Games.AddRange(new[] { installed, removed, protocolOnly });

        var service = CreateService(data, out _);
        var summary = await service.ScanAsync();

        Assert.Equal(1, summary.Uninstalled);
        Assert.False(installed.IsUninstalled);
        Assert.True(removed.IsUninstalled);
        // 没有路径证据的条目不能被误标
        Assert.False(protocolOnly.IsUninstalled);
        // 标记是"软删除"，条目本身必须还在，用户的时长/标签不能丢
        Assert.Equal(3, data.Games.Count);
    }

    [Fact]
    public void RemoveGame_RecordsIgnoreKey()
    {
        var game = new Game { Id = "g", Source = GameSourceKind.Steam, ExternalId = "570", Name = "Dota 2" };
        var data = new LibraryData();
        data.Games.Add(game);

        var service = CreateService(data, out var repo);
        service.RemoveGame(game);

        Assert.Empty(data.Games);
        Assert.Contains("Steam:570", repo.Settings.IgnoredExternalIds);
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
