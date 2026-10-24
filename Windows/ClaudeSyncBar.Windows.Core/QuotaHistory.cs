using System.Globalization;
using System.Text;
using System.Text.Json;

namespace ClaudeSyncBar.Windows.Core;

/// <summary>One account's five-hour and weekly usage as one look at the service saw it.</summary>
public sealed record QuotaSample(
    int ProfileId, DateTimeOffset SampledAt, string? Plan,
    double? FiveHourUsed, DateTimeOffset? FiveHourResetsAt, double? WeeklyUsed, DateTimeOffset? WeeklyResetsAt);

/// <summary>
/// Which account a device used, as last seen. A null profile is not known, unless <paramref name="Unregistered"/>
/// says the login was read and is none of the registered accounts, whose use moves none of their quotas.
/// </summary>
public sealed record DeviceAccountObservation(string DeviceId, int? ProfileId, DateTimeOffset ObservedAt, bool Unregistered = false);

/// <summary>
/// A line of the quota history: a look at every account's usage ("poll") with the account each device used,
/// a switch that put an account on devices ("switch"), or a weekly message the app sent itself ("anchor"),
/// which uses quota without leaving a log.
/// </summary>
public sealed record QuotaHistoryEntry
{
    public string Kind { get; init; } = Poll;
    public DateTimeOffset At { get; init; }
    public IReadOnlyList<QuotaSample>? Samples { get; init; }
    public IReadOnlyList<DeviceAccountObservation>? Devices { get; init; }
    public int? ProfileId { get; init; }
    public IReadOnlyList<string>? DeviceIds { get; init; }

    public const string Poll = "poll", Switch = "switch", Anchor = "anchor";
}

/// <summary>
/// Five-hour and weekly usage over time, which the usage service gives only as of now. One JSON line per entry
/// in the state folder, kept 31 days, so a rate over a chosen period can be read back from it.
/// </summary>
public sealed class QuotaHistoryStore(WindowsPaths paths)
{
    public static readonly TimeSpan Retention = TimeSpan.FromDays(31);
    private const long MaximumBytes = 64L * 1024 * 1024;
    private static readonly JsonSerializerOptions Format = new();
    private static readonly object Gate = new();
    private (string Path, long Length, DateTime Written, List<QuotaHistoryEntry> Entries)? _read;

    public string FilePath => Path.Combine(paths.StateRoot, "quota-history.jsonl");

    public void Append(QuotaHistoryEntry entry)
    {
        lock (Gate)
        {
            paths.EnsureDirectories();
            WindowsPathSafety.EnsureFile(FilePath, "한도 기록 파일");
            var line = JsonSerializer.Serialize(entry, Format) + "\n";
            if (!File.Exists(FilePath))
            {
                WindowsPathSafety.WritePrivateBytes(FilePath, Encoding.UTF8.GetBytes(line));
                return;
            }

            // Old lines are dropped once a day, when the first line is past the retention.
            if (OldestAt() is { } oldest && entry.At - oldest > Retention + TimeSpan.FromDays(1)) Rewrite(entry.At - Retention, line);
            else File.AppendAllText(FilePath, line);
        }
    }

    /// <summary>Entries from <paramref name="since"/> on, oldest first; lines that do not parse are skipped.</summary>
    public IReadOnlyList<QuotaHistoryEntry> Read(DateTimeOffset since)
    {
        lock (Gate)
        {
            if (!File.Exists(FilePath)) return [];
            WindowsPathSafety.EnsureFile(FilePath, "한도 기록 파일");
            var info = new FileInfo(FilePath);
            if (info.Length > MaximumBytes) return [];
            if (_read is not { } read || read.Path != FilePath || read.Length != info.Length || read.Written != info.LastWriteTimeUtc)
            {
                var entries = new List<QuotaHistoryEntry>();
                foreach (var text in File.ReadLines(FilePath))
                    if (Parse(text) is { } entry) entries.Add(entry);
                entries.Sort((first, second) => first.At.CompareTo(second.At));
                _read = read = (FilePath, info.Length, info.LastWriteTimeUtc, entries);
            }

            return read.Entries.Where(entry => entry.At >= since).ToList();
        }
    }

    /// <summary>When the first usage reading was kept, or null before any.</summary>
    public DateTimeOffset? FirstSampledAt() =>
        Read(DateTimeOffset.MinValue).FirstOrDefault(entry => entry.Kind == QuotaHistoryEntry.Poll && entry.Samples is { Count: > 0 })?.At;

    private static QuotaHistoryEntry? Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        try { return JsonSerializer.Deserialize<QuotaHistoryEntry>(text, Format); }
        catch (JsonException) { return null; }
    }

    // A broken first line, cut by a crash, must not stop the pruning: the first line that reads counts.
    private DateTimeOffset? OldestAt()
    {
        foreach (var text in File.ReadLines(FilePath))
            if (Parse(text) is { } entry) return entry.At;
        return null;
    }

    private void Rewrite(DateTimeOffset keepFrom, string newLine)
    {
        var kept = new StringBuilder();
        foreach (var text in File.ReadLines(FilePath))
            if (Parse(text) is { } entry && entry.At >= keepFrom) kept.Append(text.TrimEnd('\r')).Append('\n');
        kept.Append(newLine);
        var temporary = FilePath + "." + Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + ".tmp";
        try
        {
            WindowsPathSafety.WritePrivateBytes(temporary, Encoding.UTF8.GetBytes(kept.ToString()));
            File.Move(temporary, FilePath, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }
}
