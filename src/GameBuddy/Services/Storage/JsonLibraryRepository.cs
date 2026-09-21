using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GameBuddy.Models;

namespace GameBuddy.Services.Storage;

/// <summary>库的持久化：整库 JSON 快照 + 防抖保存。</summary>
public interface ILibraryRepository
{
    LibraryData Data { get; }
    LibrarySettings Settings { get; }
    Task LoadAsync(CancellationToken ct = default);
    Task SaveAsync();
    void Save();
}

public sealed class JsonLibraryRepository : ILibraryRepository
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly SemaphoreSlim _gate = new(1, 1);

    public LibraryData Data { get; private set; } = new();

    public LibrarySettings Settings => Data.Settings;

    public async Task LoadAsync(CancellationToken ct = default)
    {
        AppPaths.EnsureCreated();
        if (!File.Exists(AppPaths.LibraryFile)) return;

        try
        {
            var json = await File.ReadAllTextAsync(AppPaths.LibraryFile, ct).ConfigureAwait(false);
            var data = JsonSerializer.Deserialize<LibraryData>(json, Options);
            if (data is not null) Data = data;
        }
        catch (Exception ex)
        {
            Services.Diagnostics.AppLog.Error("读取库文件失败，将使用空白库", ex);
            var backup = AppPaths.LibraryFile + ".bad." + DateTime.Now.ToString("yyyyMMddHHmmss");
            try
            {
                File.Move(AppPaths.LibraryFile, backup, true);
            }
            catch
            {
                // ignored
            }
        }
    }

    public async Task SaveAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            AppPaths.EnsureCreated();
            var json = JsonSerializer.Serialize(Data, Options);
            var tmp = AppPaths.LibraryFile + ".tmp";
            await File.WriteAllTextAsync(tmp, json).ConfigureAwait(false);
            File.Move(tmp, AppPaths.LibraryFile, true);
        }
        catch (Exception ex)
        {
            Services.Diagnostics.AppLog.Error("保存库文件失败", ex);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Save() => _ = SaveAsync();

    public static string Hash(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes)[..16].ToLowerInvariant();
    }
}
