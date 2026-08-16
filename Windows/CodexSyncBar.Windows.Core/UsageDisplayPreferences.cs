using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodexSyncBar.Windows.Core;

public enum UsageDisplayItem
{
    FiveHour,
    CodexWeekly,
    SparkFiveHour,
    SparkWeekly,
}

public static class UsageDisplayItemExtensions
{
    public static string StorageName(this UsageDisplayItem item) => item switch
    {
        UsageDisplayItem.FiveHour => "fiveHour",
        UsageDisplayItem.CodexWeekly => "codexWeekly",
        UsageDisplayItem.SparkFiveHour => "sparkFiveHour",
        UsageDisplayItem.SparkWeekly => "sparkWeekly",
        _ => throw new ArgumentOutOfRangeException(nameof(item)),
    };

    public static string Title(this UsageDisplayItem item) => item switch
    {
        UsageDisplayItem.FiveHour => "5시간",
        UsageDisplayItem.CodexWeekly => "주간",
        UsageDisplayItem.SparkFiveHour => "Spark 5시간",
        UsageDisplayItem.SparkWeekly => "Spark 주간",
        _ => item.ToString(),
    };

    public static int? RemainingPercent(this UsageDisplayItem item, UsageSnapshot snapshot) => item switch
    {
        UsageDisplayItem.FiveHour => snapshot.Session is null ? null : (int)Math.Round(snapshot.Session.RemainingPercent),
        UsageDisplayItem.CodexWeekly => snapshot.Weekly is null ? null : (int)Math.Round(snapshot.Weekly.RemainingPercent),
        UsageDisplayItem.SparkFiveHour => snapshot.SparkSession is null ? null : (int)Math.Round(snapshot.SparkSession.RemainingPercent),
        UsageDisplayItem.SparkWeekly => snapshot.SparkWeekly is null ? null : (int)Math.Round(snapshot.SparkWeekly.RemainingPercent),
        _ => null,
    };
}

public sealed class UsageDisplayPreferences
{
    public bool FiveHour { get; set; } = true;
    public bool CodexWeekly { get; set; } = true;
    public bool SparkFiveHour { get; set; } = true;
    public bool SparkWeekly { get; set; } = true;

    public bool IsVisible(UsageDisplayItem item) => item switch
    {
        UsageDisplayItem.FiveHour => FiveHour,
        UsageDisplayItem.CodexWeekly => CodexWeekly,
        UsageDisplayItem.SparkFiveHour => SparkFiveHour,
        UsageDisplayItem.SparkWeekly => SparkWeekly,
        _ => false,
    };

    public void SetVisible(UsageDisplayItem item, bool visible)
    {
        switch (item)
        {
            case UsageDisplayItem.FiveHour: FiveHour = visible; break;
            case UsageDisplayItem.CodexWeekly: CodexWeekly = visible; break;
            case UsageDisplayItem.SparkFiveHour: SparkFiveHour = visible; break;
            case UsageDisplayItem.SparkWeekly: SparkWeekly = visible; break;
        }
    }
}

public sealed class MenuBarUsagePreferences
{
    public const int MaximumItemCount = 2;

    public List<string> Items { get; set; } =
        ["codexWeekly", "sparkWeekly"];

    public IReadOnlyList<UsageDisplayItem> NormalizedItems()
    {
        var result = new List<UsageDisplayItem>();
        foreach (var raw in Items ?? [])
        {
            if (!Enum.TryParse<UsageDisplayItem>(raw, ignoreCase: true, out var parsed))
            {
                parsed = raw switch
                {
                    "fiveHour" => UsageDisplayItem.FiveHour,
                    "codexWeekly" => UsageDisplayItem.CodexWeekly,
                    "sparkFiveHour" => UsageDisplayItem.SparkFiveHour,
                    "sparkWeekly" => UsageDisplayItem.SparkWeekly,
                    _ => (UsageDisplayItem)(-1),
                };
            }

            if (Enum.IsDefined(parsed) && !result.Contains(parsed))
            {
                result.Add(parsed);
                if (result.Count == MaximumItemCount)
                {
                    break;
                }
            }
        }

        return result;
    }

    public void SetItems(IEnumerable<UsageDisplayItem> items) =>
        Items = items.Distinct().Take(MaximumItemCount).Select(item => item.StorageName()).ToList();
}

public sealed class UsageDisplayPreferencesStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
    private readonly WindowsPaths _paths;

    public UsageDisplayPreferencesStore(WindowsPaths paths)
    {
        _paths = paths;
    }

    public UsageDisplayPreferences LoadUsagePreferences() =>
        Read(_paths.UsageDisplayPreferencesFile, new UsageDisplayPreferences());

    public MenuBarUsagePreferences LoadMenuPreferences() =>
        Read(_paths.MenuBarUsagePreferencesFile, new MenuBarUsagePreferences());

    public void SaveUsagePreferences(UsageDisplayPreferences preferences) =>
        Write(_paths.UsageDisplayPreferencesFile, preferences);

    public void SaveMenuPreferences(MenuBarUsagePreferences preferences)
    {
        preferences.SetItems(preferences.NormalizedItems());
        Write(_paths.MenuBarUsagePreferencesFile, preferences);
    }

    private static T Read<T>(string path, T fallback)
    {
        WindowsPathSafety.EnsureFile(path, "사용량 표시 설정 파일");
        if (!File.Exists(path))
        {
            return fallback;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions) ?? fallback;
        }
        catch (JsonException)
        {
            return fallback;
        }
    }

    private static void Write<T>(string path, T value)
    {
        WindowsPathSafety.EnsureFile(path, "사용량 표시 설정 파일");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = Path.Combine(
            Path.GetDirectoryName(path)!,
            $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        File.WriteAllText(temporary, JsonSerializer.Serialize(value, JsonOptions));
        try
        {
            File.Move(temporary, path, overwrite: true);
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

public sealed class SelectedProfileStore
{
    private static readonly JsonSerializerOptions JsonOptions = new();

    private readonly WindowsPaths _paths;

    public SelectedProfileStore(WindowsPaths paths)
    {
        _paths = paths;
    }

    public int? Load()
    {
        WindowsPathSafety.EnsureFile(_paths.SelectedProfileFile, "선택 계정 설정 파일");
        if (!File.Exists(_paths.SelectedProfileFile))
        {
            return null;
        }

        try
        {
            var profileId = JsonSerializer.Deserialize<int>(
                File.ReadAllText(_paths.SelectedProfileFile),
                JsonOptions);
            return profileId > 0 ? profileId : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void Save(int profileId)
    {
        if (profileId <= 0)
        {
            return;
        }

        WindowsPathSafety.EnsureFile(_paths.SelectedProfileFile, "선택 계정 설정 파일");
        Directory.CreateDirectory(Path.GetDirectoryName(_paths.SelectedProfileFile)!);
        var temporary = Path.Combine(
            Path.GetDirectoryName(_paths.SelectedProfileFile)!,
            $".{Path.GetFileName(_paths.SelectedProfileFile)}.{Guid.NewGuid():N}.tmp");
        File.WriteAllText(temporary, JsonSerializer.Serialize(profileId, JsonOptions));
        try
        {
            File.Move(temporary, _paths.SelectedProfileFile, overwrite: true);
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
