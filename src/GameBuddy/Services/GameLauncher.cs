using System.Diagnostics;
using System.IO;
using GameBuddy.Models;
using GameBuddy.Services.Diagnostics;

namespace GameBuddy.Services;

public interface IGameLauncher
{
    bool Launch(Game game);
    void OpenInstallFolder(Game game);
    void OpenStorePage(Game game);
    void OpenSteamDbPage(Game game);
    void OpenWeb(string? url);
}

public sealed class GameLauncher : IGameLauncher
{
    public bool Launch(Game game)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(game.LaunchUri))
            {
                Process.Start(new ProcessStartInfo(game.LaunchUri!) { UseShellExecute = true });
                return true;
            }

            if (!string.IsNullOrWhiteSpace(game.ExecutablePath) && File.Exists(game.ExecutablePath))
            {
                var dir = game.InstallDirectory ?? Path.GetDirectoryName(game.ExecutablePath);
                Process.Start(new ProcessStartInfo(game.ExecutablePath!)
                {
                    WorkingDirectory = dir ?? string.Empty,
                    UseShellExecute = true
                });
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            AppLog.Error($"启动失败: {game.DisplayTitle}", ex);
            return false;
        }
    }

    public void OpenInstallFolder(Game game)
    {
        if (string.IsNullOrWhiteSpace(game.InstallDirectory) || !Directory.Exists(game.InstallDirectory)) return;
        SafeStart("explorer.exe", game.InstallDirectory!);
    }

    public void OpenStorePage(Game game)
    {
        if (!string.IsNullOrWhiteSpace(game.StoreUrl)) OpenWeb(game.StoreUrl);
    }

    public void OpenSteamDbPage(Game game)
    {
        if (!string.IsNullOrWhiteSpace(game.SteamDbUrl)) OpenWeb(game.SteamDbUrl);
    }

    public void OpenWeb(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLog.Error($"打开链接失败: {url}", ex);
        }
    }

    private static void SafeStart(string file, string arg)
    {
        try
        {
            Process.Start(new ProcessStartInfo(file, arg) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLog.Error($"打开目录失败: {arg}", ex);
        }
    }
}
