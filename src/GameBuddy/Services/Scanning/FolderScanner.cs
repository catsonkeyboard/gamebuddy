using System.Diagnostics;
using System.IO;
using GameBuddy.Models;

namespace GameBuddy.Services.Scanning;

/// <summary>
/// 指定目录扫描：把目录下的子文件夹视作"一个游戏"，挑出最可能的主程序。
/// </summary>
public sealed class FolderScanner : IGameScanner
{
    private static readonly string[] BlockList =
    {
        "uninstall", "unins000", "setup", "install", "crash", "report", "redist", "vcredist",
        "dxwebsetup", "dotnet", "oalinst", "physx", "ue4prereq", "ueprereq", "prereq",
        "activation", "launcherinstaller", "dxsetup", "vcruntime", "easyanticheat", "battleye",
        "unitycrashhandler", "crashhandler", "eac", "be_service", "steam_api", "steamclient"
    };

    private const int MaxDepth = 3;

    public GameSourceKind Source => GameSourceKind.Folder;

    public Task<IReadOnlyList<ScannedGame>> ScanAsync(ScanOptions options, CancellationToken ct = default)
    {
        var results = new List<ScannedGame>();

        foreach (var folder in options.WatchFolders)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) continue;

            // 1) 直接位于根目录的 exe
            foreach (var exe in SafeEnumerateFiles(folder, "*.exe", SearchOption.TopDirectoryOnly))
            {
                ct.ThrowIfCancellationRequested();
                if (IsBlocked(exe) || !LooksLikeGameExe(exe)) continue;
                results.Add(Create(folder, exe));
            }

            // 2) 每个子目录视作一个游戏，选一个最佳主程序
            foreach (var dir in SafeEnumerateDirectories(folder))
            {
                ct.ThrowIfCancellationRequested();
                var best = FindBestExecutable(dir, 0);
                if (best is null) continue;
                results.Add(Create(dir, best));
            }
        }

        return Task.FromResult<IReadOnlyList<ScannedGame>>(results);
    }

    private static ScannedGame Create(string gameDir, string exePath)
    {
        var name = Path.GetFileNameWithoutExtension(exePath);
        if (string.Equals(Path.GetDirectoryName(exePath)?.TrimEnd(Path.DirectorySeparatorChar),
                gameDir.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase) == false)
        {
            // 子目录里的 exe：优先用目录名
            name = new DirectoryInfo(gameDir).Name;
        }

        return new ScannedGame
        {
            Source = GameSourceKind.Folder,
            Name = name,
            ExternalId = exePath.ToLowerInvariant(),
            InstallDirectory = gameDir,
            ExecutablePath = exePath
        };
    }

    private static string? FindBestExecutable(string dir, int depth)
    {
        var local = SafeEnumerateFiles(dir, "*.exe", SearchOption.TopDirectoryOnly)
            .Where(p => !IsBlocked(p) && LooksLikeGameExe(p))
            .ToList();

        if (local.Count == 0)
        {
            if (depth >= MaxDepth) return null;
            return SafeEnumerateDirectories(dir)
                .Select(d => FindBestExecutable(d, depth + 1))
                .FirstOrDefault(x => x is not null);
        }

        var dirName = new DirectoryInfo(dir).Name.ToLowerInvariant();

        // 优先级：与目录同名 > 体积最大
        return local.FirstOrDefault(p => Path.GetFileNameWithoutExtension(p).ToLowerInvariant() == dirName)
               ?? local.OrderByDescending(p => SafeLength(p)).First();
    }

    private static bool LooksLikeGameExe(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
        if (name.Length < 2) return false;
        if (IsBlocked(path)) return false;

        // 排除明显的工具/引擎壳
        if (name.Contains("crashhandler") || name.Contains("prereq") || name.Contains("redis")) return false;

        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            var desc = info.FileDescription ?? string.Empty;
            if (desc.Contains("Installer", StringComparison.OrdinalIgnoreCase) ||
                desc.Contains("Setup", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }
        catch
        {
            // ignored
        }

        return true;
    }

    private static bool IsBlocked(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
        return BlockList.Any(b => name.Contains(b));
    }

    private static long SafeLength(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch
        {
            return 0;
        }
    }

    private static IEnumerable<string> SafeEnumerateFiles(string dir, string pattern, SearchOption option)
    {
        try
        {
            return Directory.EnumerateFiles(dir, pattern, option);
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static IEnumerable<string> SafeEnumerateDirectories(string dir)
    {
        try
        {
            return Directory.EnumerateDirectories(dir);
        }
        catch
        {
            return Array.Empty<string>();
        }
    }
}
