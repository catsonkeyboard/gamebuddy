using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameBuddy.Models;
using GameBuddy.Services;
using GameBuddy.Services.Diagnostics;
using GameBuddy.Services.Storage;
using GameBuddy.Services.Theming;
using Microsoft.Win32;

namespace GameBuddy.ViewModels;

public enum SortMode
{
    按名称,
    最近游玩,
    累计时长,
    最近加入
}

public enum FilterKind
{
    All,
    Favorite,
    Group,
    Source
}

/// <summary>给排序下拉框用的枚举集合。</summary>
public static class SortModes
{
    public static IReadOnlyList<SortMode> All { get; } = Enum.GetValues<SortMode>();
}

public sealed class FilterItem : ObservableObject
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Emoji { get; init; } = "🎮";
    public string? ColorHex { get; init; }
    public FilterKind Kind { get; init; }
    public GameSourceKind? Source { get; init; }

    private int _count;
    public int Count
    {
        get => _count;
        set => SetProperty(ref _count, value);
    }
}

public partial class MainViewModel : ObservableObject
{
    private readonly ILibraryService _library;
    private readonly ILibraryRepository _repository;
    private readonly IPlaySessionTracker _tracker;
    private readonly IGameLauncher _launcher;
    private readonly IThemeService _themes;
    private readonly DispatcherTimer _tick;
    private CancellationTokenSource _cts = new();

    public ObservableCollection<GameItemViewModel> Games { get; } = new();
    public ObservableCollection<FilterItem> Filters { get; } = new();
    public ObservableCollection<GroupCheckItem> SelectedGameGroups { get; } = new();
    public List<GameGroup> Groups => _repository.Data.Groups;

    public ICollectionView GamesView { get; }

    public LibrarySettings Settings => _repository.Settings;
    public IReadOnlyList<ThemeDescriptor> Themes => _themes.Themes;

    [ObservableProperty] private GameItemViewModel? _selectedGame;
    [ObservableProperty] private FilterItem? _selectedFilter;
    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private SortMode _sortMode = SortMode.按名称;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusText = "就绪";
    [ObservableProperty] private double _progressValue;
    [ObservableProperty] private bool _isIndeterminate = true;
    [ObservableProperty] private bool _isSettingsOpen;
    [ObservableProperty] private bool _isDetailsOpen = true;
    [ObservableProperty] private ThemeDescriptor? _currentTheme;

    public MainViewModel(
        ILibraryService library,
        ILibraryRepository repository,
        IPlaySessionTracker tracker,
        IGameLauncher launcher,
        IThemeService themes)
    {
        _library = library;
        _repository = repository;
        _tracker = tracker;
        _launcher = launcher;
        _themes = themes;

        GamesView = CollectionViewSource.GetDefaultView(Games);
        GamesView.Filter = FilterGame;
        ApplySort();

        _tracker.Changed += (_, _) => OnUi(() =>
        {
            RefreshPlayState();
            RefreshStats();
        });

        _tick = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _tick.Tick += (_, _) =>
        {
            if (_tracker.ActiveGameIds.Count > 0) RefreshPlayState();
        };
        _tick.Start();

        CurrentTheme = _themes.Current;
        MarkCurrentTheme();
    }

    private void MarkCurrentTheme()
    {
        foreach (var theme in _themes.Themes) theme.IsApplied = theme.Id == _themes.Current.Id;
    }

    public async Task InitializeAsync()
    {
        RebuildGames();
        RebuildFilters();
        OnPropertyChanged(nameof(Groups));

        if (Games.Count == 0)
        {
            StatusText = "首次使用：正在扫描本机游戏...";
            await ScanAsync();
        }
        else if (Settings.AutoFetchMetadata &&
                 Games.Any(g => g.Game.MetadataStatus is MetadataStatus.None or MetadataStatus.Failed))
        {
            _ = RefreshMetadataAsync();
        }
    }

    // ---------------- 集合构建 ----------------

    private void RebuildGames()
    {
        var selectedId = SelectedGame?.Id;
        Games.Clear();

        foreach (var game in _repository.Data.Games.OrderBy(g => g.DisplayTitle, StringComparer.CurrentCultureIgnoreCase))
        {
            Games.Add(new GameItemViewModel(game, _tracker, _launcher, _library, this));
        }

        foreach (var item in Games) item.RefreshStats();

        SelectedGame = selectedId is null ? null : Games.FirstOrDefault(g => g.Id == selectedId);
        GamesView.Refresh();
        RebuildSelectedGroups();
        UpdateFilterCounts();
    }

    private void RebuildFilters()
    {
        var selected = SelectedFilter?.Id;
        Filters.Clear();

        Filters.Add(new FilterItem { Id = "__all__", Name = "全部游戏", Emoji = "🗂", Kind = FilterKind.All });
        Filters.Add(new FilterItem { Id = "__fav__", Name = "我的收藏", Emoji = "⭐", Kind = FilterKind.Favorite });

        foreach (var group in _repository.Data.Groups.OrderBy(g => g.SortOrder).ThenBy(g => g.Name))
        {
            Filters.Add(new FilterItem
            {
                Id = group.Id,
                Name = group.Name,
                Emoji = group.Emoji,
                ColorHex = group.ColorHex,
                Kind = FilterKind.Group
            });
        }

        foreach (var source in Enum.GetValues<GameSourceKind>())
        {
            Filters.Add(new FilterItem
            {
                Id = $"__src_{source}__",
                Name = SourceLabel(source),
                Emoji = SourceEmoji(source),
                Kind = FilterKind.Source,
                Source = source
            });
        }

        SelectedFilter = Filters.FirstOrDefault(f => f.Id == selected) ?? Filters[0];
        RebuildSelectedGroups();
        UpdateFilterCounts();
    }

    public static string SourceLabel(GameSourceKind source) => source switch
    {
        GameSourceKind.Steam => "Steam",
        GameSourceKind.Epic => "Epic Games",
        GameSourceKind.Folder => "本地目录",
        _ => "手动添加"
    };

    public static string SourceEmoji(GameSourceKind source) => source switch
    {
        GameSourceKind.Steam => "🟦",
        GameSourceKind.Epic => "⬛",
        GameSourceKind.Folder => "📁",
        _ => "✋"
    };

    private void UpdateFilterCounts()
    {
        foreach (var filter in Filters)
        {
            filter.Count = Games.Count(g => InFilter(g, filter));
        }
    }

    private static bool InFilter(GameItemViewModel item, FilterItem filter) => filter.Kind switch
    {
        FilterKind.All => true,
        FilterKind.Favorite => item.Game.IsFavorite,
        FilterKind.Group => item.Game.GroupIds.Contains(filter.Id),
        FilterKind.Source => item.Game.Source == filter.Source,
        _ => true
    };

    private bool FilterGame(object obj)
    {
        if (obj is not GameItemViewModel item) return false;
        if (item.Game.IsHidden && !Settings.ShowHiddenGames) return false;
        if (SelectedFilter is { } filter && !InFilter(item, filter)) return false;

        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            var term = SearchText.Trim();
            var hay = $"{item.Title} {string.Join(' ', item.Game.Tags)} {string.Join(' ', item.Game.Genres)}";
            if (!hay.Contains(term, StringComparison.CurrentCultureIgnoreCase)) return false;
        }

        return true;
    }

    private void ApplySort()
    {
        GamesView.SortDescriptions.Clear();
        GamesView.SortDescriptions.Add(_sortMode switch
        {
            SortMode.最近游玩 => new SortDescription(nameof(GameItemViewModel.LastPlayedSortKey), ListSortDirection.Descending),
            SortMode.累计时长 => new SortDescription(nameof(GameItemViewModel.TotalMinutes), ListSortDirection.Descending),
            SortMode.最近加入 => new SortDescription(nameof(GameItemViewModel.AddedAt), ListSortDirection.Descending),
            _ => new SortDescription(nameof(GameItemViewModel.Title), ListSortDirection.Ascending)
        });
    }

    private void RefreshStats()
    {
        foreach (var item in Games) item.RefreshStats();
    }

    private void RefreshPlayState()
    {
        foreach (var item in Games) item.RefreshPlayState();
    }

    partial void OnSearchTextChanged(string value)
    {
        GamesView.Refresh();
        UpdateFilterCounts();
    }

    partial void OnSelectedFilterChanged(FilterItem? value) => GamesView.Refresh();

    partial void OnSortModeChanged(SortMode value)
    {
        ApplySort();
        GamesView.Refresh();
    }

    partial void OnSelectedGameChanged(GameItemViewModel? value)
    {
        if (value is not null) IsDetailsOpen = true;
        RebuildSelectedGroups();
    }

    /// <summary>详情面板勾选分组时由 GroupCheckItem 回调。</summary>
    public void SetMembership(string groupId, bool member)
    {
        if (SelectedGame is null) return;

        if (member)
        {
            if (!SelectedGame.Game.GroupIds.Contains(groupId)) SelectedGame.Game.GroupIds.Add(groupId);
        }
        else
        {
            SelectedGame.Game.GroupIds.Remove(groupId);
        }

        SelectedGame.RaiseGroupsChanged();
        _repository.Save();
        UpdateFilterCounts();
    }

    private void RebuildSelectedGroups()
    {
        SelectedGameGroups.Clear();
        if (SelectedGame is null) return;

        foreach (var group in _repository.Data.Groups.OrderBy(g => g.SortOrder).ThenBy(g => g.Name))
        {
            SelectedGameGroups.Add(new GroupCheckItem(this, group, SelectedGame.Game.GroupIds.Contains(group.Id)));
        }
    }

    // ---------------- 命令 ----------------

    [RelayCommand]
    private void SelectFilter(FilterItem? filter)
    {
        SelectedFilter = filter ?? Filters[0];
        IsSettingsOpen = false;
    }

    [RelayCommand]
    private void OpenSettings() => IsSettingsOpen = true;

    [RelayCommand]
    private void CloseSettings() => IsSettingsOpen = false;

    [RelayCommand]
    private void ToggleDetails() => IsDetailsOpen = !IsDetailsOpen;

    [RelayCommand(CanExecute = nameof(CanOperate))]
    private async Task ScanAsync()
    {
        await RunBusyAsync("扫描中", async (progress, ct) =>
        {
            var added = await _library.ScanAsync(progress, ct);
            StatusText = $"扫描完成，新增 {added} 个游戏";

            await OnUiAsync(() =>
            {
                RebuildGames();
                RebuildFilters();
            });

            if (Settings.AutoFetchMetadata)
            {
                await _library.RefreshMetadataAsync(false, progress, ct);
                await OnUiAsync(() =>
                {
                    foreach (var item in Games) item.RaiseMetadataChanged();
                    GamesView.Refresh();
                });
                StatusText = "扫描与元数据抓取完成";
            }

            await _repository.SaveAsync();
        });
    }

    [RelayCommand(CanExecute = nameof(CanOperate))]
    private async Task RefreshMetadataAsync() => await RefreshMetadataCoreAsync(force: false);

    [RelayCommand(CanExecute = nameof(CanOperate))]
    private async Task ForceRefreshMetadataAsync() => await RefreshMetadataCoreAsync(force: true);

    private async Task RefreshMetadataCoreAsync(bool force)
    {
        await RunBusyAsync("抓取元数据", async (progress, ct) =>
        {
            await _library.RefreshMetadataAsync(force, progress, ct);
            await OnUiAsync(() =>
            {
                foreach (var item in Games) item.RaiseMetadataChanged();
                GamesView.Refresh();
            });
            StatusText = "元数据已更新";
            await _repository.SaveAsync();
        });
    }

    [RelayCommand(CanExecute = nameof(CanOperate))]
    private async Task RefreshSelectedMetadataAsync()
    {
        if (SelectedGame is null) return;
        var item = SelectedGame;
        var appId = int.TryParse(item.MetadataAppIdText, out var v) ? v : (int?)null;

        await RunBusyAsync($"抓取 {item.Title}", async (_, ct) =>
        {
            var result = await _library.RefreshGameMetadataAsync(item.Game, appId, ct);
            await OnUiAsync(() =>
            {
                item.RaiseMetadataChanged();
                item.RefreshStats();
            });
            StatusText = result == 0 ? $"已更新 {item.Title}" : "没找到匹配的 Steam 条目";
            await _repository.SaveAsync();
        });
    }

    private bool CanOperate() => !IsBusy;

    [RelayCommand]
    private void Launch(GameItemViewModel? item)
    {
        if (item is null) return;
        if (_tracker.IsPlaying(item.Id))
        {
            StatusText = $"{item.Title} 正在游玩中";
            return;
        }

        if (!_launcher.Launch(item.Game))
        {
            StatusText = $"启动失败：{item.Title}（缺少启动方式）";
            return;
        }

        _tracker.Start(item.Game);
        item.RefreshPlayState();
        item.RefreshStats();
        StatusText = $"已启动 {item.Title}，开始计时";
    }

    [RelayCommand]
    private void Stop(GameItemViewModel? item)
    {
        if (item is null) return;
        _tracker.Stop(item.Id);
        item.RefreshPlayState();
        item.RefreshStats();
        StatusText = $"已停止计时：{item.Title}";
    }

    [RelayCommand]
    private void ToggleFavorite(GameItemViewModel? item)
    {
        if (item is null) return;
        item.Game.IsFavorite = !item.Game.IsFavorite;
        item.RaiseFlagsChanged();
        _repository.Save();
        UpdateFilterCounts();
    }

    [RelayCommand]
    private void ToggleHidden(GameItemViewModel? item)
    {
        if (item is null) return;
        item.Game.IsHidden = !item.Game.IsHidden;
        item.RaiseFlagsChanged();
        _repository.Save();
        GamesView.Refresh();
    }

    [RelayCommand]
    private void OpenFolder(GameItemViewModel? item)
    {
        if (item is null) return;
        _launcher.OpenInstallFolder(item.Game);
    }

    [RelayCommand]
    private void OpenSteamDb(GameItemViewModel? item)
    {
        if (item is null) return;
        if (string.IsNullOrEmpty(item.Game.SteamDbUrl))
        {
            StatusText = "该游戏还没有关联 Steam AppId";
            return;
        }

        _launcher.OpenSteamDbPage(item.Game);
    }

    [RelayCommand]
    private void OpenStore(GameItemViewModel? item)
    {
        if (item is null) return;
        if (string.IsNullOrEmpty(item.Game.StoreUrl))
        {
            StatusText = "该游戏还没有关联 Steam AppId";
            return;
        }

        _launcher.OpenStorePage(item.Game);
    }

    [RelayCommand]
    private void RemoveGame(GameItemViewModel? item)
    {
        if (item is null) return;
        if (MessageBox.Show($"确定从库中移除「{item.Title}」吗？（不会删除游戏文件）",
                "移除游戏", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        _library.RemoveGame(item.Game);
        Games.Remove(item);
        if (SelectedGame == item) SelectedGame = null;
        _repository.Save();
        UpdateFilterCounts();
    }

    // ---------------- 分组 / 标签 ----------------

    [RelayCommand]
    private void AddGroup()
    {
        var palette = new[] { "#5B8CFF", "#4ADE80", "#FBBF24", "#FF8FC7", "#A78BFA", "#22D3EE" };
        var emojis = new[] { "🎮", "🏆", "🔥", "🧩", "🕹", "🎲" };
        var index = _repository.Data.Groups.Count;

        var group = new GameGroup
        {
            Name = $"新分组 {index + 1}",
            ColorHex = palette[index % palette.Length],
            Emoji = emojis[index % emojis.Length],
            SortOrder = index
        };

        _repository.Data.Groups.Add(group);
        OnPropertyChanged(nameof(Groups));
        RebuildFilters();
        _repository.Save();
    }

    [RelayCommand]
    private void DeleteGroup(GameGroup? group)
    {
        if (group is null) return;
        foreach (var game in _repository.Data.Games) game.GroupIds.Remove(group.Id);
        _repository.Data.Groups.Remove(group);
        OnPropertyChanged(nameof(Groups));
        RebuildFilters();
        GamesView.Refresh();
        _repository.Save();
    }

    /// <summary>把当前选中的游戏加入/移出指定分组。</summary>
    [RelayCommand]
    private void ToggleGroupMembership(string? groupId)
    {
        if (SelectedGame is null || string.IsNullOrWhiteSpace(groupId)) return;

        if (SelectedGame.Game.GroupIds.Contains(groupId)) SelectedGame.Game.GroupIds.Remove(groupId);
        else SelectedGame.Game.GroupIds.Add(groupId);

        SelectedGame.RaiseGroupsChanged();
        _repository.Save();
        UpdateFilterCounts();
    }

    public bool IsInGroup(string groupId) => SelectedGame?.Game.GroupIds.Contains(groupId) ?? false;

    [RelayCommand]
    private void AddTag(string? tag)
    {
        if (SelectedGame is null || string.IsNullOrWhiteSpace(tag)) return;
        var value = tag.Trim();
        if (SelectedGame.Game.Tags.Contains(value)) return;

        SelectedGame.Game.Tags.Add(value);
        SelectedGame.RaiseGroupsChanged();
        _repository.Save();
        GamesView.Refresh();
    }

    [RelayCommand]
    private void RemoveTag(string? tag)
    {
        if (SelectedGame is null || string.IsNullOrWhiteSpace(tag)) return;
        SelectedGame.Game.Tags.Remove(tag);
        SelectedGame.RaiseGroupsChanged();
        _repository.Save();
        GamesView.Refresh();
    }

    // ---------------- 手动添加 / 设置 ----------------

    [RelayCommand]
    private void AddManualGame()
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择游戏主程序",
            Filter = "可执行文件 (*.exe)|*.exe|所有文件 (*.*)|*.*"
        };

        if (dialog.ShowDialog() != true) return;

        var dir = Path.GetDirectoryName(dialog.FileName);
        var name = string.IsNullOrWhiteSpace(dir)
            ? Path.GetFileNameWithoutExtension(dialog.FileName)
            : new DirectoryInfo(dir).Name;

        var game = _library.AddManualGame(name, dialog.FileName);
        var item = new GameItemViewModel(game, _tracker, _launcher, _library, this);
        Games.Add(item);
        SelectedGame = item;
        _repository.Save();
        StatusText = $"已添加 {name}，可在右侧抓取元数据";
    }

    [RelayCommand]
    private void BrowseSteamPath()
    {
        var dialog = new OpenFolderDialog { Title = "选择 Steam 安装目录" };
        if (dialog.ShowDialog() == true)
        {
            Settings.SteamPathOverride = dialog.FolderName;
            OnPropertyChanged(nameof(Settings));
        }
    }

    [RelayCommand]
    private void BrowseEpicPath()
    {
        var dialog = new OpenFolderDialog { Title = "选择 Epic Manifests 目录" };
        if (dialog.ShowDialog() == true)
        {
            Settings.EpicManifestsPathOverride = dialog.FolderName;
            OnPropertyChanged(nameof(Settings));
        }
    }

    [RelayCommand]
    private void AddWatchFolder()
    {
        var dialog = new OpenFolderDialog { Title = "选择要扫描的游戏目录" };
        if (dialog.ShowDialog() != true) return;
        if (Settings.WatchFolders.Contains(dialog.FolderName)) return;

        Settings.WatchFolders.Add(dialog.FolderName);
        OnPropertyChanged(nameof(Settings));
        _repository.Save();
    }

    [RelayCommand]
    private void RemoveWatchFolder(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        Settings.WatchFolders.Remove(path);
        OnPropertyChanged(nameof(Settings));
        _repository.Save();
    }

    [RelayCommand]
    private void ApplyTheme(ThemeDescriptor? theme)
    {
        if (theme is null) return;
        if (!_themes.Apply(theme.Id)) return;

        CurrentTheme = theme;
        MarkCurrentTheme();
        Settings.ThemeId = theme.Id;
        _repository.Save();
        StatusText = $"已切换到主题：{theme.Name}";
    }

    [RelayCommand]
    private void OpenThemesFolder() => _themes.OpenThemesFolder();

    [RelayCommand]
    private void ReloadThemes()
    {
        _themes.ReloadExternalThemes();
        MarkCurrentTheme();
        OnPropertyChanged(nameof(Themes));
        StatusText = "已重新加载主题插件";
    }

    [RelayCommand]
    private void OpenDataFolder()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", AppPaths.Root)
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            AppLog.Error("打开数据目录失败", ex);
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        _cts.Cancel();
        StatusText = "已请求取消";
    }

    [RelayCommand]
    private void SaveSettings()
    {
        _repository.Save();
        StatusText = "设置已保存";
    }

    // ---------------- 内部工具 ----------------

    private static void OnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) action();
        else dispatcher.Invoke(action);
    }

    private static Task OnUiAsync(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            action();
            return Task.CompletedTask;
        }

        return dispatcher.InvokeAsync(action).Task;
    }

    private async Task RunBusyAsync(string title, Func<IProgress<ProgressReport>, CancellationToken, Task> action)
    {
        if (IsBusy) return;

        IsBusy = true;
        IsIndeterminate = true;
        StatusText = title + "...";
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        var progress = new Progress<ProgressReport>(report =>
        {
            StatusText = report.Message;
            if (report.Value is { } v)
            {
                IsIndeterminate = false;
                ProgressValue = v * 100;
            }
        });

        try
        {
            await action(progress, token);
            IsIndeterminate = true;
        }
        catch (OperationCanceledException)
        {
            StatusText = "已取消";
        }
        catch (Exception ex)
        {
            AppLog.Error(title + " 失败", ex);
            StatusText = title + "失败：" + ex.Message;
        }
        finally
        {
            IsBusy = false;
            IsIndeterminate = true;
            ProgressValue = 0;
            CommandManager.InvalidateRequerySuggested();
        }
    }
}
