using System.IO;
using System.Text.Json;
using GameBuddy.Models;

namespace GameBuddy.Services.Scanning;

/// <summary>
/// Epic Games 扫描：解析 %ProgramData%\Epic\EpicGamesLauncher\Data\Manifests\*.item（JSON 清单）。
/// </summary>
public sealed class EpicScanner : IGameScanner
{
    public GameSourceKind Source => GameSourceKind.Epic;

    public Task<IReadOnlyList<ScannedGame>> ScanAsync(ScanOptions options, CancellationToken ct = default)
    {
        var results = new List<ScannedGame>();
        var manifestDir = !string.IsNullOrWhiteSpace(options.EpicManifestsPathOverride)
            ? options.EpicManifestsPathOverride
            : DefaultManifestDirectory();

        if (string.IsNullOrWhiteSpace(manifestDir) || !Directory.Exists(manifestDir))
        {
            return Task.FromResult<IReadOnlyList<ScannedGame>>(results);
        }

        foreach (var file in Directory.EnumerateFiles(manifestDir, "*.item"))
        {
            ct.ThrowIfCancellationRequested();
            var game = ParseManifest(file);
            if (game is not null && !results.Any(r => r.ExternalId == game.ExternalId))
            {
                results.Add(game);
            }
        }

        return Task.FromResult<IReadOnlyList<ScannedGame>>(results);
    }

    public static string? DefaultManifestDirectory()
    {
        var programData = Environment.GetEnvironmentVariable("ProgramData");
        if (string.IsNullOrWhiteSpace(programData)) return null;
        var dir = Path.Combine(programData, "Epic", "EpicGamesLauncher", "Data", "Manifests");
        return Directory.Exists(dir) ? dir : null;
    }

    private static ScannedGame? ParseManifest(string path)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;

            string? appName = GetString(root, "AppName");
            string? displayName = GetString(root, "DisplayName") ?? appName;
            string? installLocation = GetString(root, "InstallLocation");
            string? launchExe = GetString(root, "LaunchExecutable");
            string? category = GetString(root, "AppCategory");

            if (string.IsNullOrWhiteSpace(appName) || string.IsNullOrWhiteSpace(displayName)) return null;
            if (!string.IsNullOrWhiteSpace(category) &&
                !category.Equals("games", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            if (installLocation is not null && !Directory.Exists(installLocation)) return null;

            string? exe = null;
            if (!string.IsNullOrWhiteSpace(installLocation) && !string.IsNullOrWhiteSpace(launchExe))
            {
                var candidate = Path.Combine(installLocation!, launchExe!.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(candidate)) exe = candidate;
            }

            var ns = GetString(root, "CatalogNamespace");
            var itemId = GetString(root, "CatalogItemId");
            var launchUri = !string.IsNullOrWhiteSpace(ns) && !string.IsNullOrWhiteSpace(itemId)
                ? $"com.epicgames.launcher://apps/{Uri.EscapeDataString(ns!)}%3A{Uri.EscapeDataString(itemId!)}%3A{Uri.EscapeDataString(appName!)}?action=launch&silent=true"
                : $"com.epicgames.launcher://apps/{Uri.EscapeDataString(appName!)}?action=launch&silent=true";

            return new ScannedGame
            {
                Source = GameSourceKind.Epic,
                Name = displayName!,
                ExternalId = appName!,
                InstallDirectory = installLocation,
                ExecutablePath = exe,
                LaunchUri = launchUri
            };
        }
        catch
        {
            return null;
        }
    }

    private static string? GetString(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
