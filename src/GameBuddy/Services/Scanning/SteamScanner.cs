using System.Globalization;
using System.IO;
using GameBuddy.Models;
using Microsoft.Win32;

namespace GameBuddy.Services.Scanning;

/// <summary>
/// Steam 扫描：读注册表定位 Steam，解析 libraryfolders.vdf 得到所有库目录，
/// 再解析每个目录下的 appmanifest_*.acf 得到已安装游戏。
/// </summary>
public sealed class SteamScanner : IGameScanner
{
    private static readonly HashSet<int> IgnoredAppIds = new()
    {
        228980,  // Steamworks Common Redistributables
        1070560, // Steam Linux Runtime - Soldier
        1391110, // Steam Linux Runtime - Sniper
        1628350, // Steam Linux Runtime - Sniper (new)
        1493710, // Proton Experimental
        2807960, // Proton 9.0
    };

    private static readonly string[] IgnoredNameParts =
    {
        "Steamworks", "Common Redistributables", "Proton", "Steam Linux Runtime",
        "Dedicated Server", "SDK", "Redist"
    };

    public GameSourceKind Source => GameSourceKind.Steam;

    public Task<IReadOnlyList<ScannedGame>> ScanAsync(ScanOptions options, CancellationToken ct = default)
    {
        var results = new List<ScannedGame>();
        var steamPath = !string.IsNullOrWhiteSpace(options.SteamPathOverride)
            ? options.SteamPathOverride
            : FindSteamPath();

        if (string.IsNullOrWhiteSpace(steamPath) || !Directory.Exists(steamPath))
        {
            return Task.FromResult<IReadOnlyList<ScannedGame>>(results);
        }

        foreach (var library in EnumerateLibraryFolders(steamPath))
        {
            ct.ThrowIfCancellationRequested();
            var steamApps = Path.Combine(library, "steamapps");
            if (!Directory.Exists(steamApps)) continue;

            foreach (var acf in Directory.EnumerateFiles(steamApps, "appmanifest_*.acf"))
            {
                ct.ThrowIfCancellationRequested();
                var game = ParseManifest(acf, steamApps);
                if (game is not null) results.Add(game);
            }
        }

        return Task.FromResult<IReadOnlyList<ScannedGame>>(results);
    }

    private static ScannedGame? ParseManifest(string acfPath, string steamApps)
    {
        try
        {
            var root = VdfParser.Parse(File.ReadAllText(acfPath));
            var state = VdfParser.GetObject(root, "AppState") ?? root;

            var appIdText = VdfParser.GetString(state, "appid");
            var name = VdfParser.GetString(state, "name");
            var installDir = VdfParser.GetString(state, "installdir");
            var sizeText = VdfParser.GetString(state, "SizeOnDisk");

            if (appIdText is null || string.IsNullOrWhiteSpace(name)) return null;
            if (!int.TryParse(appIdText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var appId)) return null;
            if (IgnoredAppIds.Contains(appId)) return null;
            if (IgnoredNameParts.Any(p => name.Contains(p, StringComparison.OrdinalIgnoreCase))) return null;

            var fullInstallDir = string.IsNullOrWhiteSpace(installDir)
                ? null
                : Path.GetFullPath(Path.Combine(steamApps, "common", installDir));

            if (fullInstallDir is not null && !Directory.Exists(fullInstallDir)) return null;

            long? size = long.TryParse(sizeText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var s)
                ? s
                : null;

            return new ScannedGame
            {
                Source = GameSourceKind.Steam,
                Name = name,
                ExternalId = appId.ToString(CultureInfo.InvariantCulture),
                SteamAppId = appId,
                InstallDirectory = fullInstallDir,
                InstallSizeBytes = size,
                LaunchUri = $"steam://rungameid/{appId}"
            };
        }
        catch
        {
            return null;
        }
    }

    private static IEnumerable<string> EnumerateLibraryFolders(string steamPath)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Path.GetFullPath(steamPath) };
        var list = new List<string> { Path.GetFullPath(steamPath) };

        var vdfPath = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(vdfPath))
        {
            return list;
        }

        try
        {
            // 兼容不同 Steam 版本：优先取 libraryfolders 节点，否则直接用根
            var root = VdfParser.Parse(File.ReadAllText(vdfPath));
            var lf = VdfParser.GetObject(root, "libraryfolders") ?? root;

            foreach (var kv in lf)
            {
                if (kv.Value is not Dictionary<string, object> entry) continue;
                var path = VdfParser.GetString(entry, "path");
                if (string.IsNullOrWhiteSpace(path)) continue;

                var full = Path.GetFullPath(path.Replace("\\\\", "\\"));
                if (Directory.Exists(full) && seen.Add(full))
                {
                    list.Add(full);
                }
            }
        }
        catch
        {
            // 忽略解析失败，至少保留主库
        }

        return list;
    }

    public static string? FindSteamPath()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            var value = key?.GetValue("SteamPath") as string;
            if (!string.IsNullOrWhiteSpace(value) && Directory.Exists(value)) return value;
        }
        catch
        {
            // ignored
        }

        try
        {
            var candidates = new[]
            {
                @"SOFTWARE\WOW6432Node\Valve\Steam",
                @"SOFTWARE\Valve\Steam"
            };

            foreach (var c in candidates)
            {
                using var key = Registry.LocalMachine.OpenSubKey(c);
                var value = key?.GetValue("InstallPath") as string;
                if (!string.IsNullOrWhiteSpace(value) && Directory.Exists(value)) return value;
            }
        }
        catch
        {
            // ignored
        }

        foreach (var guess in new[]
                 {
                     @"C:\Program Files (x86)\Steam",
                     @"C:\Program Files\Steam"
                 })
        {
            if (Directory.Exists(guess)) return guess;
        }

        return null;
    }
}
