using System.IO;
using GameBuddy.Models;
using GameBuddy.Services.Scanning;

namespace GameBuddy.Tests;

public sealed class SteamScannerTests : IDisposable
{
    private readonly string _root;
    private readonly string _secondLibrary;

    public SteamScannerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "gb_steam_" + Guid.NewGuid().ToString("N"));
        _secondLibrary = Path.Combine(Path.GetTempPath(), "gb_steam2_" + Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(Path.Combine(_root, "steamapps", "common", "dota 2 beta"));
        Directory.CreateDirectory(Path.Combine(_secondLibrary, "steamapps", "common", "Cyberpunk 2077"));

        Write(Path.Combine(_root, "steamapps", "appmanifest_570.acf"),
            Manifest(appId: 570, name: "Dota 2", installDir: "dota 2 beta"));

        // 应被忽略：Steamworks 公共运行库
        Write(Path.Combine(_root, "steamapps", "appmanifest_228980.acf"),
            Manifest(appId: 228980, name: "Steamworks Common Redistributables", installDir: "steamworks_common_redist"));

        // 应被忽略：Proton 运行库（按名称过滤）
        Write(Path.Combine(_root, "steamapps", "appmanifest_1493710.acf"),
            Manifest(appId: 1493710, name: "Proton Experimental", installDir: "Proton - Experimental"));

        // 应被忽略：安装目录不存在（已卸载但残留清单）
        Write(Path.Combine(_root, "steamapps", "appmanifest_999999.acf"),
            Manifest(appId: 999999, name: "Ghost Game", installDir: "ghost"));

        Write(Path.Combine(_root, "steamapps", "libraryfolders.vdf"), $$"""
            "libraryfolders"
            {
            	"0"
            	{
            		"path"		"{{_root.Replace("\\", "\\\\")}}"
            	}
            	"1"
            	{
            		"path"		"{{_secondLibrary.Replace("\\", "\\\\")}}"
            	}
            }
            """);

        Write(Path.Combine(_secondLibrary, "steamapps", "appmanifest_1091500.acf"),
            Manifest(appId: 1091500, name: "Cyberpunk 2077", installDir: "Cyberpunk 2077"));
    }

    [Fact]
    public async Task ScanAsync_FindsInstalledGamesAcrossLibraries()
    {
        var scanner = new SteamScanner();
        var results = await scanner.ScanAsync(new ScanOptions { SteamPathOverride = _root });

        Assert.Equal(GameSourceKind.Steam, scanner.Source);
        Assert.Contains(results, g => g.Name == "Dota 2" && g.SteamAppId == 570);
        Assert.Contains(results, g => g.Name == "Cyberpunk 2077" && g.SteamAppId == 1091500);
    }

    [Fact]
    public async Task ScanAsync_SkipsRedistributablesRuntimesAndGhostEntries()
    {
        var results = await new SteamScanner().ScanAsync(new ScanOptions { SteamPathOverride = _root });

        Assert.DoesNotContain(results, g => g.SteamAppId == 228980);
        Assert.DoesNotContain(results, g => g.Name.Contains("Proton"));
        Assert.DoesNotContain(results, g => g.Name == "Ghost Game");
    }

    [Fact]
    public async Task ScanAsync_BuildsSteamLaunchUriAndInstallPath()
    {
        var results = await new SteamScanner().ScanAsync(new ScanOptions { SteamPathOverride = _root });
        var dota = results.Single(g => g.SteamAppId == 570);

        Assert.Equal("steam://rungameid/570", dota.LaunchUri);
        Assert.Equal(Path.Combine(_root, "steamapps", "common", "dota 2 beta"), dota.InstallDirectory);
        Assert.Equal(32212254720, dota.InstallSizeBytes);
    }

    [Fact]
    public async Task ScanAsync_ReturnsEmptyWhenSteamMissing()
    {
        var results = await new SteamScanner().ScanAsync(new ScanOptions { SteamPathOverride = @"Z:\not\here" });
        Assert.Empty(results);
    }

    public void Dispose()
    {
        TryDelete(_root);
        TryDelete(_secondLibrary);
    }

    private static void TryDelete(string dir)
    {
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch
        {
            // 清理失败不影响测试结果
        }
    }

    private static void Write(string path, string content) => File.WriteAllText(path, content);

    private static string Manifest(int appId, string name, string installDir) => $$"""
        "AppState"
        {
        	"appid"		"{{appId}}"
        	"name"		"{{name}}"
        	"installdir"		"{{installDir}}"
        	"SizeOnDisk"		"32212254720"
        }
        """;
}

public sealed class FolderScannerTests : IDisposable
{
    private readonly string _root;

    public FolderScannerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "gb_folder_" + Guid.NewGuid().ToString("N"));

        // 正常游戏：主程序与目录同名
        Create(Path.Combine(_root, "MyGame", "MyGame.exe"));
        Create(Path.Combine(_root, "MyGame", "uninstall.exe"));
        Create(Path.Combine(_root, "MyGame", "UnityCrashHandler64.exe"));

        // 只有卸载器的目录，不应被识别
        Create(Path.Combine(_root, "JunkFolder", "unins000.exe"));

        // 根目录里的安装器，不应被识别
        Create(Path.Combine(_root, "setup.exe"));
    }

    [Fact]
    public async Task ScanAsync_PicksMainExecutableAndIgnoresInstallers()
    {
        var results = await new FolderScanner().ScanAsync(new ScanOptions
        {
            WatchFolders = new[] { _root }
        });

        var game = Assert.Single(results, r => r.Name == "MyGame");
        Assert.Equal(Path.Combine(_root, "MyGame", "MyGame.exe"), game.ExecutablePath);
        Assert.Equal(Path.Combine(_root, "MyGame"), game.InstallDirectory);
        Assert.DoesNotContain(results, r => r.Name.Contains("unins", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(results, r => r.Name.Equals("setup", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ScanAsync_IgnoresUnknownAndMissingFolders()
    {
        var results = await new FolderScanner().ScanAsync(new ScanOptions
        {
            WatchFolders = new[] { @"Z:\definitely\not\here" }
        });

        Assert.Empty(results);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // ignored
        }
    }

    private static void Create(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Array.Empty<byte>());
    }
}
