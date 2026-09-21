using System.Diagnostics;
using GameBuddy.Models;
using GameBuddy.Services.Diagnostics;
using GameBuddy.Services.Storage;

namespace GameBuddy.Services;

/// <summary>
/// 游玩计时：启动游戏即开始计时，后台轮询检测进程是否还在；进程消失则自动结束会话。
/// 对于 Steam/Epic 这类"通过协议启动、拿不到子进程"的情况，用安装目录匹配进程路径兜底。
/// </summary>
public interface IPlaySessionTracker
{
    event EventHandler? Changed;

    bool IsPlaying(string gameId);
    TimeSpan GetElapsed(string gameId);
    PlaySession? GetActive(string gameId);
    IReadOnlyCollection<string> ActiveGameIds { get; }

    PlaySession Start(Game game);
    void Stop(string gameId);
    void StopAll();
    void RestoreUnfinishedSessions();
}

public sealed class PlaySessionTracker : IPlaySessionTracker, IDisposable
{
    private readonly ILibraryRepository _repository;
    private readonly Dictionary<string, PlaySession> _active = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTime> _seenUntil = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> _confirmed = new(StringComparer.Ordinal);
    private readonly object _sync = new();
    private readonly System.Timers.Timer? _timer;

    public event EventHandler? Changed;

    public PlaySessionTracker(ILibraryRepository repository)
    {
        _repository = repository;

        _timer = new System.Timers.Timer(4000);
        _timer.Elapsed += (_, _) =>
        {
            try
            {
                Tick();
            }
            catch (Exception ex)
            {
                AppLog.Error("计时轮询异常", ex);
            }
        };
        _timer.Start();
    }

    public IReadOnlyCollection<string> ActiveGameIds
    {
        get
        {
            lock (_sync) return _active.Keys.ToList();
        }
    }

    public bool IsPlaying(string gameId)
    {
        lock (_sync) return _active.ContainsKey(gameId);
    }

    public TimeSpan GetElapsed(string gameId)
    {
        lock (_sync)
        {
            return _active.TryGetValue(gameId, out var s) ? s.Duration : TimeSpan.Zero;
        }
    }

    public PlaySession? GetActive(string gameId)
    {
        lock (_sync)
        {
            return _active.TryGetValue(gameId, out var s) ? s : null;
        }
    }

    public PlaySession Start(Game game)
    {
        PlaySession session;
        lock (_sync)
        {
            if (_active.TryGetValue(game.Id, out var existing)) return existing;

            session = new PlaySession { GameId = game.Id, StartedAt = DateTime.Now };
            _repository.Data.Sessions.Add(session);
            _active[game.Id] = session;
            _confirmed[game.Id] = false;
            _seenUntil[game.Id] = DateTime.Now.AddSeconds(90); // 给启动器一点时间拉起进程
        }

        game.LastPlayedAt = session.StartedAt;
        _repository.Save();
        Changed?.Invoke(this, EventArgs.Empty);
        return session;
    }

    public void Stop(string gameId) => EndSession(gameId, completed: true);

    public void StopAll()
    {
        List<string> ids;
        lock (_sync) ids = _active.Keys.ToList();
        foreach (var id in ids) EndSession(id, completed: true);
    }

    /// <summary>上次异常退出留下的未结束会话，按"到退出为止"补全。</summary>
    public void RestoreUnfinishedSessions()
    {
        var changed = false;
        foreach (var session in _repository.Data.Sessions.Where(s => s.EndedAt is null))
        {
            session.EndedAt = session.StartedAt.AddSeconds(Math.Min(
                (DateTime.Now - session.StartedAt).TotalSeconds, 60 * 60 * 12));
            session.Completed = false;
            changed = true;
        }

        if (changed) _repository.Save();
    }

    private void Tick()
    {
        List<(string GameId, PlaySession Session)> snapshot;
        lock (_sync) snapshot = _active.Select(kv => (kv.Key, kv.Value)).ToList();
        if (snapshot.Count == 0) return;

        foreach (var (gameId, session) in snapshot)
        {
            var game = _repository.Data.Games.FirstOrDefault(g => g.Id == gameId);
            if (game is null)
            {
                EndSession(gameId, completed: false);
                continue;
            }

            if (IsRunning(game))
            {
                lock (_sync)
                {
                    _confirmed[gameId] = true;
                    _seenUntil[gameId] = DateTime.Now.AddSeconds(20);
                }
            }
            else
            {
                bool expired;
                bool confirmed;
                lock (_sync)
                {
                    confirmed = _confirmed.TryGetValue(gameId, out var c) && c;
                    expired = _seenUntil.TryGetValue(gameId, out var until) && DateTime.Now > until;
                }

                if (confirmed && expired)
                {
                    EndSession(gameId, completed: true);
                }
                else if (!confirmed && expired && session.Duration > TimeSpan.FromMinutes(15))
                {
                    // 长时间都没检测到进程，认为这次没有真正启动
                    EndSession(gameId, completed: false);
                }
            }
        }
    }

    private void EndSession(string gameId, bool completed)
    {
        PlaySession? session;
        lock (_sync)
        {
            if (!_active.Remove(gameId, out session)) return;
            _confirmed.Remove(gameId);
            _seenUntil.Remove(gameId);
        }

        session.EndedAt = DateTime.Now;
        session.Completed = completed;

        var game = _repository.Data.Games.FirstOrDefault(g => g.Id == gameId);
        if (game is not null) game.LastPlayedAt = session.EndedAt;

        _repository.Save();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static bool IsRunning(Game game)
    {
        var hasPathHint = !string.IsNullOrWhiteSpace(game.ExecutablePath) ||
                          !string.IsNullOrWhiteSpace(game.InstallDirectory);

        foreach (var process in Process.GetProcesses())
        {
            try
            {
                string? path;
                try
                {
                    path = process.MainModule?.FileName;
                }
                catch
                {
                    continue;
                }

                if (string.IsNullOrEmpty(path)) continue;

                if (hasPathHint)
                {
                    if (!string.IsNullOrWhiteSpace(game.ExecutablePath) &&
                        string.Equals(path, game.ExecutablePath, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }

                    if (!string.IsNullOrWhiteSpace(game.InstallDirectory) &&
                        path.StartsWith(game.InstallDirectory.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
                else if (process.ProcessName.Equals(game.DisplayTitle, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            catch
            {
                // 忽略无权访问的进程
            }
        }

        return false;
    }

    public void Dispose()
    {
        _timer?.Stop();
        _timer?.Dispose();
    }
}
