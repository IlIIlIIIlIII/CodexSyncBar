namespace ClaudeSyncBar.Windows.Core;

/// <summary>What one million tokens means in a quota rate.</summary>
public static class TokenBurnBasis
{
    /// <summary>Uncached input, cache writes and output: cache reads are nine tenths of every prompt and are left out.</summary>
    public const string Uncached = "uncached";
    public const string Total = "total";
    /// <summary>Tokens counted by their API price, Claude Opus 5.5 input being one.</summary>
    public const string Weighted = "weighted";

    // Claude Opus 5.5's input rate in USD per million tokens, the unit of the weighted count.
    internal const decimal ReferenceRate = 4m;

    public static string Normalize(string? value) => value is Total or Weighted ? value : Uncached;
}

public sealed record TokenDeviceInput(string Id, string DisplayName, string Kind, bool IsReachable, string? Error, DeviceTokenUsageSummary? Summary);

public sealed record TokenAccountInput(int ProfileId, string Name, string? Plan);

public sealed record TokenRateRequest(
    DateTimeOffset CollectedAt, int Hours, string Basis, IReadOnlyList<TokenDeviceInput> Devices,
    IReadOnlyList<QuotaHistoryEntry> History, DateTimeOffset? HistoryStartedAt, IReadOnlyList<TokenAccountInput> Accounts,
    int? ActiveProfileId, bool ShowFiveHour = true, bool ShowWeekly = true);

/// <summary>Tokens and quota rates over the period that ends when the tokens were collected.</summary>
public sealed record TokenPeriodView(
    int Hours, string Basis, DateTimeOffset StartedAt, DateTimeOffset EndedAt, double ElapsedHours, bool Clipped,
    DateTimeOffset? RecordStartedAt, int ActiveHours, TokenThroughputView Throughput, TokenTotalsView Totals,
    IReadOnlyList<TokenDeviceView> Devices, TokenBurnView Burn);

/// <summary>Tokens per hour over the period; the active figures count only hours with a request.</summary>
public sealed record TokenThroughputView(
    double Total, double Input, double Output, double Uncached, double CacheRead, double CacheWrite, double UncachedInput,
    double Reasoning, double ActiveTotal, double ActiveOutput);

public sealed record TokenTotalsView(
    long TotalTokens, long InputTokens, long CachedInputTokens, long CacheWriteInputTokens, long UncachedInputTokens,
    long OutputTokens, long ReasoningOutputTokens, long Requests, decimal EstimatedCostUsd, long UnpricedTokens);

public sealed record TokenModelView(string Model, string ServiceTiers, long TotalTokens, decimal EstimatedCostUsd, bool IsPriced);

public sealed record TokenDeviceView(
    string Id, string DisplayName, string Kind, bool IsReachable, string? Error, bool HasMinutes, long TotalTokens,
    decimal EstimatedCostUsd, IReadOnlyList<TokenModelView> Models);

/// <summary>
/// The rates of the accounts used in the period. Of the basis tokens, those of accounts not registered here and,
/// counted by price, those of models with no price are named apart: neither belongs to a rate.
/// </summary>
public sealed record TokenBurnView(
    string Basis, DateTimeOffset? HistoryStartedAt, double BasisTokens, double AttributedTokens,
    IReadOnlyList<TokenAccountBurnView> Accounts, IReadOnlyList<string> IdleAccounts, double UnregisteredTokens,
    double UnpricedTokens, bool ShowFiveHour, bool ShowWeekly);

public sealed record TokenAccountBurnView(
    int ProfileId, string Name, string? Plan, bool IsCurrent, double AttributedTokens, IReadOnlyList<string> Devices,
    QuotaBurnView FiveHour, QuotaBurnView Weekly);

/// <summary>
/// One quota's use per million tokens. State: "ok"; "low" when the whole-percent readings make it rough;
/// "collecting" until the use moved 3 points over enough tokens; "hidden" when that quota is not shown.
/// </summary>
public sealed record QuotaBurnView(
    string State, double? RatePerMillion, double DeltaPercent, double Tokens, int Windows, int Runs, double UntrackedPercent,
    bool PlanChanged);

/// <summary>
/// Cuts the minutes every device's logs give into the chosen period, and divides how far each account's
/// five-hour and weekly use moved by the tokens that account used meanwhile. Only neighbouring readings of the
/// same window, close together, with the devices' accounts known, are compared; a reset, a gap, a lower reading,
/// a weekly message or tokens whose account is unknown leave that stretch out.
/// </summary>
public static class TokenRateCalculator
{
    public const int MaximumHours = 720;
    private const double MinimumDelta = 3;
    private const double MinimumTokens = 100_000;
    private const double RoughShare = 0.2;
    private static readonly TimeSpan MaximumGap = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan ResetDrift = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan LookBack = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan MaximumHold = TimeSpan.FromDays(7);
    // A login that is none of the registered accounts, held like an account but counted for none.
    private const int UnregisteredAccount = -1;

    public static TokenPeriodView Calculate(TokenRateRequest request)
    {
        var hours = Math.Clamp(request.Hours, 1, MaximumHours);
        var basis = TokenBurnBasis.Normalize(request.Basis);
        var end = request.CollectedAt;
        var start = end - TimeSpan.FromHours(hours);
        var firstMinute = Minute(start);
        var lastMinute = Minute(end);
        var devices = request.Devices.Select(device => Prepare(device, basis)).ToList();
        var counted = devices.Where(device => device.Minutes is not null).ToList();

        var recordStart = counted.SelectMany(device => device.Minutes!).Select(minute => minute.Minute).DefaultIfEmpty(long.MaxValue).Min();
        DateTimeOffset? recordStartedAt = recordStart == long.MaxValue ? null : FromMinute(recordStart);
        var clipped = recordStartedAt is { } first && first > start;
        var elapsed = clipped ? Math.Max(1 / 60d, (end - recordStartedAt!.Value).TotalHours) : hours;

        var inPeriod = counted.SelectMany(device => device.Minutes!.Where(minute => minute.Minute >= firstMinute && minute.Minute <= lastMinute)
            .Select(minute => (device, minute))).ToList();
        long Sum(Func<TokenMinute, long> part) => inPeriod.Sum(entry => part(entry.minute.Source));
        var cost = inPeriod.Sum(entry => entry.minute.Cost);
        var totals = new TokenTotalsView(
            Sum(m => m.TotalTokens), Sum(m => m.InputTokens), Sum(m => m.CachedInputTokens), Sum(m => m.CacheWriteInputTokens),
            Sum(m => Math.Max(0, m.InputTokens - m.CachedInputTokens - m.CacheWriteInputTokens)), Sum(m => m.OutputTokens),
            Sum(m => m.ReasoningOutputTokens), Sum(m => m.Requests), cost,
            inPeriod.Where(entry => !entry.minute.Priced).Sum(entry => entry.minute.Source.TotalTokens));
        // Hours counted back from the end: the last 60 minutes are the first hour.
        var activeHours = inPeriod.Where(entry => entry.minute.Source.TotalTokens > 0)
            .Select(entry => Math.Max(0, lastMinute - entry.minute.Minute - 1) / 60).Distinct().Count();
        double PerHour(long value) => value / elapsed;
        double PerActive(long value) => activeHours == 0 ? 0 : value / (double)activeHours;
        var throughput = new TokenThroughputView(
            PerHour(totals.TotalTokens), PerHour(totals.InputTokens), PerHour(totals.OutputTokens),
            PerHour(totals.UncachedInputTokens + totals.CacheWriteInputTokens + totals.OutputTokens), PerHour(totals.CachedInputTokens),
            PerHour(totals.CacheWriteInputTokens), PerHour(totals.UncachedInputTokens), PerHour(totals.ReasoningOutputTokens),
            PerActive(totals.TotalTokens), PerActive(totals.OutputTokens));

        var deviceViews = devices.Select(device => DeviceView(device, firstMinute, lastMinute)).ToList();
        var burn = Burn(request, basis, devices, counted, start, end, firstMinute, lastMinute);
        return new TokenPeriodView(hours, basis, start, end, elapsed, clipped, recordStartedAt, activeHours, throughput, totals, deviceViews, burn);
    }

    private static TokenDeviceView DeviceView(Device device, long firstMinute, long lastMinute)
    {
        var input = device.Input;
        if (device.Minutes is null)
            return new(input.Id, input.DisplayName, input.Kind, input.IsReachable, input.Error, false, 0, 0m, []);
        var minutes = device.Minutes.Where(minute => minute.Minute >= firstMinute && minute.Minute <= lastMinute).ToList();
        var models = minutes.GroupBy(minute => minute.Source.Model, StringComparer.Ordinal)
            .Select(group => new TokenModelView(group.Key,
                string.Join(", ", group.Select(minute => minute.Source.ServiceTier).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)),
                group.Sum(minute => minute.Source.TotalTokens), group.Sum(minute => minute.Cost), group.All(minute => minute.Priced)))
            .Where(model => model.TotalTokens > 0)
            .OrderByDescending(model => model.TotalTokens).ToList();
        return new(input.Id, input.DisplayName, input.Kind, input.IsReachable, input.Error, true,
            models.Sum(model => model.TotalTokens), models.Sum(model => model.EstimatedCostUsd), models);
    }

    private static TokenBurnView Burn(TokenRateRequest request, string basis, List<Device> devices, List<Device> counted,
        DateTimeOffset start, DateTimeOffset end, long firstMinute, long lastMinute)
    {
        var observations = Observations(request.History);
        var holders = devices.ToDictionary(device => device.Input.Id, device => Holders(observations.GetValueOrDefault(device.Input.Id) ?? [], end), StringComparer.Ordinal);
        var lookFrom = firstMinute - (long)LookBack.TotalMinutes;

        // Every counted minute with tokens goes to the account its device used then, to "unknown", or to an
        // account not registered here, whose use moves none of these quotas.
        var byAccount = new Dictionary<int, List<(long Minute, double Tokens)>>();
        var unpricedByAccount = new Dictionary<int, List<(long Minute, double Tokens)>>();
        var unknown = new List<(long Minute, double Tokens)>();
        var accountDevices = new Dictionary<int, HashSet<string>>();
        double basisTokens = 0, attributed = 0, unregistered = 0, unpriced = 0;
        var attributedByAccount = new Dictionary<int, double>();
        foreach (var device in counted)
        {
            foreach (var minute in device.Minutes!)
            {
                if (minute.Minute < lookFrom || minute.Minute > lastMinute || minute.Source.TotalTokens <= 0) continue;
                var holder = Holder(holders[device.Input.Id], minute.Minute);
                var inPeriod = minute.Minute >= firstMinute;
                if (inPeriod) basisTokens += minute.Basis;
                if (holder == UnregisteredAccount)
                {
                    if (inPeriod) unregistered += minute.Basis;
                    continue;
                }

                if (holder is not { } account)
                {
                    // Counted by its real size even where a weighted count has no price for it.
                    unknown.Add((minute.Minute, Math.Max(minute.Basis, minute.Source.TotalTokens)));
                    continue;
                }

                if (basis == TokenBurnBasis.Weighted && !minute.Priced)
                {
                    // Use with no price cannot be weighed: the stretches holding it stay out of the rate.
                    (unpricedByAccount.TryGetValue(account, out var without) ? without : unpricedByAccount[account] = []).Add((minute.Minute, minute.Source.TotalTokens));
                    if (inPeriod) unpriced += minute.Source.TotalTokens;
                    continue;
                }

                (byAccount.TryGetValue(account, out var list) ? list : byAccount[account] = []).Add((minute.Minute, minute.Basis));
                if (!inPeriod) continue;
                attributed += minute.Basis;
                attributedByAccount[account] = attributedByAccount.GetValueOrDefault(account) + minute.Basis;
                (accountDevices.TryGetValue(account, out var names) ? names : accountDevices[account] = []).Add(device.Input.DisplayName);
            }
        }

        var unknownSums = new RangeSums(unknown);
        var sums = byAccount.ToDictionary(pair => pair.Key, pair => new RangeSums(pair.Value));
        // A device whose logs were not read may have used an account meanwhile: its stretches stay out of that account's rate.
        var unread = devices.Where(device => device.Minutes is null).Select(device => holders[device.Input.Id]).ToList();
        var anchors = request.History.Where(entry => entry.Kind == QuotaHistoryEntry.Anchor && entry.ProfileId is not null)
            .GroupBy(entry => entry.ProfileId!.Value).ToDictionary(group => group.Key, group => group.Select(entry => entry.At).ToList());
        var samples = request.History.Where(entry => entry.Kind == QuotaHistoryEntry.Poll && entry.Samples is not null)
            .SelectMany(entry => entry.Samples!).GroupBy(sample => sample.ProfileId)
            .ToDictionary(group => group.Key, group => group.DistinctBy(sample => sample.SampledAt).OrderBy(sample => sample.SampledAt).ToList());

        var rows = new List<TokenAccountBurnView>();
        foreach (var account in request.Accounts)
        {
            var own = sums.GetValueOrDefault(account.ProfileId) ?? RangeSums.Empty;
            var readings = samples.GetValueOrDefault(account.ProfileId) ?? [];
            var without = unpricedByAccount.TryGetValue(account.ProfileId, out var list) ? new RangeSums(list) : RangeSums.Empty;
            var context = new PairContext(account.ProfileId, own, unknownSums, without, unread, anchors.GetValueOrDefault(account.ProfileId) ?? [], start, end);
            var five = Quota(readings, context, request.ShowFiveHour, sample => sample.FiveHourUsed, sample => sample.FiveHourResetsAt);
            var week = Quota(readings, context, request.ShowWeekly, sample => sample.WeeklyUsed, sample => sample.WeeklyResetsAt);
            var used = attributedByAccount.GetValueOrDefault(account.ProfileId);
            if (used <= 0 && five.UntrackedPercent <= 0 && week.UntrackedPercent <= 0) continue;
            rows.Add(new(account.ProfileId, account.Name, account.Plan, account.ProfileId == request.ActiveProfileId, used,
                (accountDevices.GetValueOrDefault(account.ProfileId) ?? []).Order(StringComparer.Ordinal).ToList(), five, week));
        }

        rows = rows.OrderByDescending(row => row.IsCurrent).ThenByDescending(row => row.AttributedTokens).ToList();
        var idle = request.Accounts.Where(account => rows.All(row => row.ProfileId != account.ProfileId)).Select(account => account.Name).ToList();
        return new TokenBurnView(basis, request.HistoryStartedAt, basisTokens, attributed, rows, idle, unregistered, unpriced,
            request.ShowFiveHour, request.ShowWeekly);
    }

    private sealed record PairContext(
        int ProfileId, RangeSums Own, RangeSums Unknown, RangeSums Unpriced, IReadOnlyList<List<Hold>> Unread,
        IReadOnlyList<DateTimeOffset> Anchors, DateTimeOffset Start, DateTimeOffset End);

    private static QuotaBurnView Quota(IReadOnlyList<QuotaSample> readings, PairContext context, bool shown,
        Func<QuotaSample, double?> used, Func<QuotaSample, DateTimeOffset?> resets)
    {
        if (!shown) return new("hidden", null, 0, 0, 0, 0, 0, false);
        var inside = readings.Where(sample => sample.SampledAt >= context.Start && sample.SampledAt <= context.End).ToList();
        // The rate holds for the plan the account has now; a 1% of another plan is another size.
        var plan = inside.LastOrDefault(sample => sample.Plan is not null)?.Plan;
        var planChanged = inside.Any(sample => sample.Plan is not null && sample.Plan != plan);
        double delta = 0, tokens = 0, untracked = 0;
        var runs = 0;
        var whole = true;
        var windows = new HashSet<long>();
        var lastEnd = -1;
        var dropped = -1;
        var running = false;
        for (var i = 1; i < inside.Count; i++)
        {
            var (before, after) = (inside[i - 1], inside[i]);
            if (used(before) is not { } from || used(after) is not { } to) continue;
            if (after.SampledAt - before.SampledAt > MaximumGap) continue;
            if (before.Plan is not null && after.Plan is not null && before.Plan != after.Plan) continue;
            if (plan is not null && after.Plan is not null && after.Plan != plan) continue;
            var (resetBefore, resetAfter) = (resets(before), resets(after));
            // Before its first use a five-hour window has no reset time yet and reads 0.
            var sameWindow = resetBefore is { } r0 ? resetAfter is { } r1 && (r1 - r0).Duration() <= ResetDrift && r0 > after.SampledAt : from == 0;
            if (!sameWindow) continue;
            // A reading lower than the one before in the same window is wrong: neither side of it is compared.
            if (to < from) { dropped = i; continue; }
            if (i - 1 == dropped) continue;
            if (context.Anchors.Any(at => at > before.SampledAt && at <= after.SampledAt)) continue;
            var (first, last) = (Minute(before.SampledAt), Minute(after.SampledAt));
            if (context.Unknown.Sum(first, last) > 0 || context.Unpriced.Sum(first, last) > 0) continue;
            if (context.Unread.Any(holds => holds.Any(hold => hold.ProfileId == context.ProfileId && hold.From < after.SampledAt && hold.To > before.SampledAt))) continue;
            var spent = context.Own.Sum(first, last);
            var moved = to - from;
            if (moved > 0 && spent <= 0 && context.Own.Sum(first - (long)LookBack.TotalMinutes, last) <= 0)
            {
                // Use with no tokens near it came from somewhere these logs do not see.
                untracked += moved;
                continue;
            }

            delta += moved;
            tokens += spent;
            // Rounding error comes once per unbroken stretch that used something; idle pairs only join stretches.
            if (lastEnd != i - 1) running = false;
            lastEnd = i;
            if (moved <= 0 && spent <= 0) continue;
            if (!running) { runs++; running = true; }
            whole &= from == Math.Floor(from) && to == Math.Floor(to);
            if ((resetAfter ?? resetBefore) is { } reset) windows.Add(reset.ToUnixTimeSeconds() / 600);
        }

        var resolution = whole ? 1 : 0.1;
        string state;
        if (delta < MinimumDelta || tokens < MinimumTokens) state = "collecting";
        else state = runs * resolution / delta > RoughShare ? "low" : "ok";
        double? rate = tokens > 0 && delta > 0 ? delta / (tokens / 1_000_000) : null;
        return new(state, state == "collecting" ? null : rate, delta, tokens, windows.Count, runs, untracked, planChanged);
    }

    // Which account each device used, from looks at it and from switches that put an account on it.
    private static Dictionary<string, List<(DateTimeOffset At, int? ProfileId, bool Switched)>> Observations(IReadOnlyList<QuotaHistoryEntry> history)
    {
        var result = new Dictionary<string, List<(DateTimeOffset, int?, bool)>>(StringComparer.Ordinal);
        foreach (var entry in history)
        {
            if (entry.Kind == QuotaHistoryEntry.Poll)
                foreach (var device in entry.Devices ?? [])
                    (result.TryGetValue(device.DeviceId, out var list) ? list : result[device.DeviceId] = [])
                        .Add((device.ObservedAt, device.Unregistered ? UnregisteredAccount : device.ProfileId, false));
            else if (entry.Kind == QuotaHistoryEntry.Switch && entry.ProfileId is { } profile)
                foreach (var id in entry.DeviceIds ?? [])
                    (result.TryGetValue(id, out var list) ? list : result[id] = []).Add((entry.At, profile, true));
        }

        foreach (var list in result.Values) list.Sort((first, second) => first.Item1.CompareTo(second.Item1));
        return result;
    }

    private sealed record Hold(DateTimeOffset From, DateTimeOffset To, int ProfileId);

    // Between two looks that agree, or up to a switch, the device kept its account; when a look finds another
    // account with no switch in between, the change happened at a time unknown, so that stretch has no account.
    private static List<Hold> Holders(List<(DateTimeOffset At, int? ProfileId, bool Switched)> seen, DateTimeOffset end)
    {
        var holds = new List<Hold>();
        for (var i = 0; i < seen.Count; i++)
        {
            if (seen[i].ProfileId is not { } profile) continue;
            var until = i + 1 < seen.Count ? seen[i + 1].At : end;
            var next = i + 1 < seen.Count ? seen[i + 1] : default;
            var kept = i + 1 >= seen.Count || next.Switched || next.ProfileId == profile;
            if (!kept || until - seen[i].At > MaximumHold || until <= seen[i].At) continue;
            holds.Add(new(seen[i].At, until, profile));
        }

        return holds;
    }

    private static int? Holder(List<Hold> holds, long minute)
    {
        var at = FromMinute(minute);
        int low = 0, high = holds.Count - 1;
        while (low <= high)
        {
            var middle = (low + high) / 2;
            if (holds[middle].To <= at) low = middle + 1;
            else if (holds[middle].From > at) high = middle - 1;
            else return holds[middle].ProfileId;
        }

        return null;
    }

    private sealed record Device(TokenDeviceInput Input, List<PreparedMinute>? Minutes);

    private sealed record PreparedMinute(long Minute, TokenMinute Source, double Basis, decimal Cost, bool Priced);

    private static Device Prepare(TokenDeviceInput input, string basis)
    {
        if (input.Summary is not { SchemaVersion: >= 7 } summary || input.Error is not null) return new(input, null);
        var minutes = summary.MinuteSeries().Select(minute =>
        {
            var estimate = TokenUsagePricing.EstimateUsd(minute.Usage);
            var weighted = (double)(estimate.PricedUsd / TokenBurnBasis.ReferenceRate * 1_000_000m);
            var amount = basis switch { TokenBurnBasis.Total => minute.TotalTokens, TokenBurnBasis.Weighted => weighted, _ => minute.UncachedTokens };
            return new PreparedMinute(minute.Minute, minute, amount, estimate.PricedUsd, estimate.IsPriced);
        }).OrderBy(minute => minute.Minute).ToList();
        return new(input, minutes);
    }

    /// <summary>Token sums over minute ranges [first, last).</summary>
    private sealed class RangeSums
    {
        public static readonly RangeSums Empty = new([]);
        private readonly long[] _minutes;
        private readonly double[] _prefix;

        public RangeSums(List<(long Minute, double Tokens)> entries)
        {
            entries.Sort((first, second) => first.Minute.CompareTo(second.Minute));
            _minutes = entries.Select(entry => entry.Minute).ToArray();
            _prefix = new double[entries.Count + 1];
            for (var i = 0; i < entries.Count; i++) _prefix[i + 1] = _prefix[i] + entries[i].Tokens;
        }

        public double Sum(long first, long last) => last <= first ? 0 : _prefix[Index(last)] - _prefix[Index(first)];

        private int Index(long minute)
        {
            var index = Array.BinarySearch(_minutes, minute);
            if (index < 0) return ~index;
            while (index > 0 && _minutes[index - 1] == minute) index--;
            return index;
        }
    }

    private static long Minute(DateTimeOffset time) => (long)Math.Floor(time.ToUnixTimeMilliseconds() / 60_000d);
    private static DateTimeOffset FromMinute(long minute) => DateTimeOffset.FromUnixTimeMilliseconds(minute * 60_000);
}
