using System.IO;

namespace GameBuddy.Services.Storage;

/// <summary>所有本地数据落地位置，统一放在 %LOCALAPPDATA%\GameBuddy 下。</summary>
public static class AppPaths
{
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GameBuddy");

    public static string CacheDir { get; } = Path.Combine(Root, "cache");

    public static string PosterCacheDir { get; } = Path.Combine(CacheDir, "posters");

    public static string ThemesDir { get; } = Path.Combine(Root, "Themes");

    public static string LibraryFile { get; } = Path.Combine(Root, "library.json");

    public static string LogFile { get; } = Path.Combine(Root, "gamebuddy.log");

    public static void EnsureCreated()
    {
        foreach (var dir in new[] { Root, CacheDir, PosterCacheDir, ThemesDir })
        {
            Directory.CreateDirectory(dir);
        }
    }
}
