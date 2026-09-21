using System.Net;
using System.Net.Http;
using System.Windows;
using GameBuddy.Services;
using GameBuddy.Services.Diagnostics;
using GameBuddy.Services.Metadata;
using GameBuddy.Services.Scanning;
using GameBuddy.Services.Storage;
using GameBuddy.Services.Theming;
using GameBuddy.ViewModels;
using GameBuddy.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace GameBuddy;

public partial class App : Application
{
    private IHost? _host;

    public static IServiceProvider Services { get; private set; } = null!;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            AppLog.Error("未处理的 UI 异常", args.Exception);
            args.Handled = true;
        };

        AppPaths.EnsureCreated();

        _host = Host.CreateDefaultBuilder()
            .ConfigureServices(ConfigureServices)
            .Build();

        Services = _host.Services;
        await _host.StartAsync();

        // 先读库，再决定主题
        var repository = Services.GetRequiredService<ILibraryRepository>();
        await repository.LoadAsync();

        var themes = Services.GetRequiredService<IThemeService>();
        themes.Initialize();
        themes.Apply(repository.Settings.ThemeId);

        var tracker = Services.GetRequiredService<IPlaySessionTracker>();
        tracker.RestoreUnfinishedSessions();

        var window = Services.GetRequiredService<MainWindow>();
        MainWindow = window;
        window.Show();

        if (window.DataContext is MainViewModel vm)
        {
            await vm.InitializeAsync();
        }
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton(_ => new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        })
        {
            Timeout = TimeSpan.FromSeconds(30),
            DefaultRequestHeaders =
            {
                { "User-Agent", "GameBuddy/0.1 (+https://github.com/local/gamebuddy)" }
            }
        });

        // 存储与业务
        services.AddSingleton<ILibraryRepository, JsonLibraryRepository>();
        services.AddSingleton<IGameScanner, SteamScanner>();
        services.AddSingleton<IGameScanner, EpicScanner>();
        services.AddSingleton<IGameScanner, FolderScanner>();
        services.AddSingleton<IMetadataProvider, SteamStoreMetadataProvider>();
        services.AddSingleton<IImageCache, ImageCacheService>();
        services.AddSingleton<ILibraryService, LibraryService>();
        services.AddSingleton<IGameLauncher, GameLauncher>();
        services.AddSingleton<IPlaySessionTracker, PlaySessionTracker>();
        services.AddSingleton<IThemeService, ThemeService>();

        // ViewModels / Views
        services.AddSingleton<MainViewModel>();
        services.AddTransient<MainWindow>();
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        try
        {
            if (Services is not null)
            {
                Services.GetRequiredService<IPlaySessionTracker>().StopAll();
                await Services.GetRequiredService<ILibraryRepository>().SaveAsync();
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("退出时保存失败", ex);
        }

        if (_host is not null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }

        base.OnExit(e);
    }
}
