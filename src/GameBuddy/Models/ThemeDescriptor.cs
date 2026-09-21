using CommunityToolkit.Mvvm.ComponentModel;

namespace GameBuddy.Models;

/// <summary>主题插件描述。内置主题编译进程序集，外部主题从 Themes 目录加载。</summary>
public partial class ThemeDescriptor : ObservableObject
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public bool IsBuiltIn { get; set; }
    public string XamlFile { get; set; } = string.Empty;
    public string? DirectoryPath { get; set; }
    public string? PreviewColorHex { get; set; }

    [ObservableProperty] private bool _isApplied;
}
