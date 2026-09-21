using System.ComponentModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using GameBuddy.Models;
using GameBuddy.Services;

namespace GameBuddy.ViewModels;

/// <summary>库中一个游戏在界面上的表现模型：负责统计、游玩状态与"正在计时"的实时刷新。</summary>
public sealed class GameItemViewModel : ObservableObject
{
    private readonly IPlaySessionTracker _tracker;
    private readonly IGameLauncher _launcher;
    private readonly ILibraryService _library;
    private readonly MainViewModel _owner;

    private TimeSpan _totalPlayTime;
    private TimeSpan _currentSessionTime;
    private bool _isPlaying;
    private int _sessionCount;
    private string _posterPath;

    public Game Game { get; }
    public MainViewModel Owner => _owner;

    public GameItemViewModel(
        Game game,
        IPlaySessionTracker tracker,
        IGameLauncher launcher,
        ILibraryService library,
        MainViewModel owner)
    {
        Game = game;
        _tracker = tracker;
        _launcher = launcher;
        _library = library;
        _owner = owner;
        _posterPath = game.PosterPath ?? string.Empty;
    }

    public string Id => Game.Id;
    public string Title => Game.DisplayTitle;

    public string PosterPath
    {
        get => _posterPath;
        private set => SetProperty(ref _posterPath, value);
    }

    public bool HasPoster => !string.IsNullOrWhiteSpace(_posterPath) && File.Exists(_posterPath);

    public TimeSpan TotalPlayTime
    {
        get => _totalPlayTime;
        private set
        {
            if (SetProperty(ref _totalPlayTime, value))
            {
                OnPropertyChanged(nameof(TotalPlayTimeText));
                OnPropertyChanged(nameof(TotalMinutes));
            }
        }
    }

    public double TotalMinutes => _totalPlayTime.TotalMinutes;

    public string TotalPlayTimeText => FormatDuration(_totalPlayTime);

    public TimeSpan CurrentSessionTime
    {
        get => _currentSessionTime;
        private set
        {
            if (SetProperty(ref _currentSessionTime, value)) OnPropertyChanged(nameof(CurrentSessionText));
        }
    }

    public string CurrentSessionText => _isPlaying ? FormatDuration(_currentSessionTime) : string.Empty;

    public bool IsPlaying
    {
        get => _isPlaying;
        private set
        {
            if (SetProperty(ref _isPlaying, value)) OnPropertyChanged(nameof(CurrentSessionText));
        }
    }

    public int SessionCount
    {
        get => _sessionCount;
        private set => SetProperty(ref _sessionCount, value);
    }

    public string SourceLabel => MainViewModel.SourceLabel(Game.Source);
    public string SourceEmoji => MainViewModel.SourceEmoji(Game.Source);
    public DateTime AddedAt => Game.AddedAt;
    public DateTime LastPlayedSortKey => Game.LastPlayedAt ?? DateTime.MinValue;
    public string LastPlayedText => Game.LastPlayedAt is { } d ? d.ToString("yyyy-MM-dd HH:mm") : "从未游玩";
    public string SizeText => Game.InstallSizeBytes is { } b ? FormatBytes(b) : "—";
    public string GenresText => Game.Genres.Count == 0 ? "—" : string.Join(" · ", Game.Genres);
    public string InstallPathText => Game.InstallDirectory ?? Game.ExecutablePath ?? "—";

    /// <summary>右侧面板里可编辑的 Steam AppId。</summary>
    public string MetadataAppIdText
    {
        get => Game.SteamAppId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        set
        {
            if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
            {
                Game.SteamAppId = id;
            }
            else if (string.IsNullOrWhiteSpace(value))
            {
                Game.SteamAppId = null;
            }

            OnPropertyChanged();
        }
    }

    public string MetadataStatusText => Game.MetadataStatus switch
    {
        MetadataStatus.Ok => "已获取",
        MetadataStatus.Pending => "获取中",
        MetadataStatus.Failed => "未找到",
        _ => "待获取"
    };

    public void RefreshStats()
    {
        TotalPlayTime = _library.GetTotalPlayTime(Game.Id);
        SessionCount = _library.GetSessionCount(Game.Id);
        OnPropertyChanged(nameof(LastPlayedText));
        OnPropertyChanged(nameof(LastPlayedSortKey));
        OnPropertyChanged(nameof(MetadataStatusText));
    }

    public void RefreshPlayState()
    {
        IsPlaying = _tracker.IsPlaying(Game.Id);
        CurrentSessionTime = _tracker.GetElapsed(Game.Id);
    }

    public void RaiseMetadataChanged()
    {
        PosterPath = Game.PosterPath ?? string.Empty;
        OnPropertyChanged(nameof(HasPoster));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(MetadataAppIdText));
        OnPropertyChanged(nameof(MetadataStatusText));
        OnPropertyChanged(nameof(GenresText));
    }

    public void RaiseFlagsChanged()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Game));
    }

    public void RaiseGroupsChanged()
    {
        OnPropertyChanged(nameof(Game));
    }

    public void OpenFolder() => _launcher.OpenInstallFolder(Game);

    private static string FormatDuration(TimeSpan ts)
    {
        if (ts.TotalMinutes < 1) return ts.TotalSeconds < 5 ? "未游玩" : "<1 分钟";
        var hours = (int)ts.TotalHours;
        var minutes = ts.Minutes;
        return hours > 0 ? $"{hours} 小时 {minutes} 分钟" : $"{minutes} 分钟";
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double size = bytes;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return $"{size:0.##} {units[unit]}";
    }
}

/// <summary>详情面板里"属于哪些分组"的可勾选项。</summary>
public sealed class GroupCheckItem : ObservableObject
{
    private readonly MainViewModel _owner;
    private bool _isMember;

    public GroupCheckItem(MainViewModel owner, GameGroup group, bool isMember)
    {
        _owner = owner;
        Group = group;
        _isMember = isMember;
    }

    public GameGroup Group { get; }

    public bool IsMember
    {
        get => _isMember;
        set
        {
            if (SetProperty(ref _isMember, value)) _owner.SetMembership(Group.Id, value);
        }
    }
}
