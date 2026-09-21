using System.IO;
using System.Text.Json;
using System.Windows;
using GameBuddy.Models;
using GameBuddy.Services.Diagnostics;
using GameBuddy.Services.Storage;

namespace GameBuddy.Services.Theming;

public interface IThemeService
{
    IReadOnlyList<ThemeDescriptor> Themes { get; }
    ThemeDescriptor Current { get; }
    void Initialize();
    bool Apply(string themeId);
    void OpenThemesFolder();
    void ReloadExternalThemes();
}

public sealed class ThemeService : IThemeService
{
    private const string BuiltInPrefix = "pack://application:,,,/GameBuddy;component/Resources/Themes/";

    private static readonly ThemeDescriptor[] BuiltIns =
    {
        new()
        {
            Id = "dark", Name = "暗黑（默认）", Description = "低饱和深色，专注游戏内容",
            Author = "GameBuddy", IsBuiltIn = true, XamlFile = "Dark.xaml", PreviewColorHex = "#5B8CFF"
        },
        new()
        {
            Id = "pixel", Name = "像素风", Description = "8-bit 复古：硬边框、等宽字、无圆角",
            Author = "GameBuddy", IsBuiltIn = true, XamlFile = "Pixel.xaml", PreviewColorHex = "#FFD34E"
        },
        new()
        {
            Id = "cyberpunk", Name = "赛博朋克", Description = "霓虹青紫、发光描边、高对比",
            Author = "GameBuddy", IsBuiltIn = true, XamlFile = "Cyberpunk.xaml", PreviewColorHex = "#00F0FF"
        },
        new()
        {
            Id = "cute", Name = "可爱卡通", Description = "圆角糖果色、柔和阴影",
            Author = "GameBuddy", IsBuiltIn = true, XamlFile = "Cute.xaml", PreviewColorHex = "#FF8FC7"
        }
    };

    private static readonly JsonSerializerOptions ManifestOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private readonly List<ThemeDescriptor> _themes = new();
    private ResourceDictionary? _currentDictionary;

    public IReadOnlyList<ThemeDescriptor> Themes => _themes;

    public ThemeDescriptor Current { get; private set; } = BuiltIns[0];

    public void Initialize()
    {
        _themes.Clear();
        _themes.AddRange(BuiltIns);
        ReloadExternalThemes();
    }

    public void ReloadExternalThemes()
    {
        _themes.RemoveAll(t => !t.IsBuiltIn);
        try
        {
            AppPaths.EnsureCreated();
            foreach (var dir in Directory.EnumerateDirectories(AppPaths.ThemesDir))
            {
                var manifest = Path.Combine(dir, "theme.json");
                if (!File.Exists(manifest)) continue;

                var descriptor = JsonSerializer.Deserialize<ThemeDescriptor>(
                    File.ReadAllText(manifest), ManifestOptions);
                if (descriptor is null || string.IsNullOrWhiteSpace(descriptor.Id)) continue;

                descriptor.IsBuiltIn = false;
                descriptor.DirectoryPath = dir;
                descriptor.XamlFile = string.IsNullOrWhiteSpace(descriptor.XamlFile) ? "Theme.xaml" : descriptor.XamlFile;
                _themes.Add(descriptor);
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("加载外部主题失败", ex);
        }
    }

    public bool Apply(string themeId)
    {
        var descriptor = _themes.FirstOrDefault(t => t.Id == themeId) ?? BuiltIns[0];

        try
        {
            var source = descriptor.IsBuiltIn
                ? new Uri(BuiltInPrefix + descriptor.XamlFile, UriKind.Absolute)
                : new Uri(Path.Combine(descriptor.DirectoryPath!, descriptor.XamlFile), UriKind.Absolute);

            var dictionary = new ResourceDictionary { Source = source };
            var resources = Application.Current?.Resources;
            if (resources is null) return false;

            if (_currentDictionary is not null) resources.MergedDictionaries.Remove(_currentDictionary);
            resources.MergedDictionaries.Add(dictionary);
            _currentDictionary = dictionary;
            Current = descriptor;
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Error($"应用主题失败: {descriptor.Id}", ex);
            return false;
        }
    }

    public void OpenThemesFolder()
    {
        AppPaths.EnsureCreated();
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", AppPaths.ThemesDir)
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            AppLog.Error("打开主题目录失败", ex);
        }
    }
}
