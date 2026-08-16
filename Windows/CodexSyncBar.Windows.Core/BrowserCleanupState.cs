using System.Text.Json;

namespace CodexSyncBar.Windows.Core;

public sealed class BrowserCleanupStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    private readonly WindowsPaths _paths;

    public BrowserCleanupStateStore(WindowsPaths paths)
    {
        _paths = paths;
    }

    public IReadOnlySet<int> Load()
    {
        WindowsPathSafety.EnsureFile(_paths.BrowserCleanupFile, "Chrome 정리 상태 파일");
        if (!File.Exists(_paths.BrowserCleanupFile))
        {
            return new HashSet<int>();
        }

        try
        {
            var values = JsonSerializer.Deserialize<int[]>(
                File.ReadAllText(_paths.BrowserCleanupFile),
                JsonOptions) ?? [];
            return values.Where(id => id > 0).ToHashSet();
        }
        catch (JsonException)
        {
            return new HashSet<int>();
        }
    }

    public void Save(IEnumerable<int> profileIds)
    {
        WindowsPathSafety.EnsureFile(_paths.BrowserCleanupFile, "Chrome 정리 상태 파일");
        Directory.CreateDirectory(Path.GetDirectoryName(_paths.BrowserCleanupFile)!);
        var temporary = Path.Combine(
            Path.GetDirectoryName(_paths.BrowserCleanupFile)!,
            $".browser-cleanup.{Guid.NewGuid():N}.tmp");
        File.WriteAllText(
            temporary,
            JsonSerializer.Serialize(profileIds.Where(id => id > 0).Distinct().Order(), JsonOptions));
        try
        {
            File.Move(temporary, _paths.BrowserCleanupFile, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }
}
