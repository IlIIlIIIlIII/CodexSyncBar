namespace ClaudeSyncBar.Windows.Core;

/// <summary>
/// One account as the week's plan takes it. <paramref name="Share"/> is the part of its weekly quota, in whole
/// percent, the user may use. <paramref name="Available"/> is false while it is pending or needs a login. The use
/// and the reset are the weekly quota as last read, null when not known.
/// </summary>
public sealed record WeekTimelineAccount(
    int ProfileId, string Name, int Share, bool Available, double? UsedPercent, DateTimeOffset? ResetsAt,
    bool IsCurrent, bool SmartCandidate);

/// <summary>
/// <paramref name="Week"/> 0 is the week that contains <paramref name="Now"/>, -1 the one before, down to
/// <see cref="WeekTimelineCalculator.MinimumWeek"/>. The accounts come in list order.
/// </summary>
public sealed record WeekTimelineRequest(
    DateTimeOffset Now, int Week, IReadOnlyList<WeekTimelineAccount> Accounts, IReadOnlyList<QuotaHistoryEntry> History,
    string LocalDeviceId = "local");

/// <summary>
/// One week of the plan beside what happened in it. State: "ok", or "empty" when no account can be planned, and
/// then only <see cref="Accounts"/> says why. Every amount is in percent of one account's weekly quota; every
/// <c>At</c> and <c>Width</c> is a fraction of the shown week.
/// </summary>
public sealed record WeekTimelineView(
    string State, int Week, bool HasEarlier, DateTimeOffset? RecordStartsAt,
    DateTimeOffset StartsAt, DateTimeOffset EndsAt, DateTimeOffset Now, double? NowAt,
    double TotalShare, double DailyShare,
    IReadOnlyList<WeekTimelineDay> Days, IReadOnlyList<WeekTimelineSegment> Segments,
    IReadOnlyList<WeekTimelineHold> Holds, IReadOnlyList<WeekTimelineReset> Resets,
    WeekTimelineSummary Summary, IReadOnlyList<WeekTimelineRow> Accounts);

/// <summary>A day of the shown week; <paramref name="Used"/> is the user's own use in it, null before the day begins.</summary>
public sealed record WeekTimelineDay(DateTimeOffset StartsAt, DateTimeOffset EndsAt, double? Used, double Planned, bool IsToday);

/// <summary>The stretch of the week planned for one account, which ends at or before that account's reset.</summary>
public sealed record WeekTimelineSegment(
    int ProfileId, string Name, int Share, DateTimeOffset StartsAt, DateTimeOffset EndsAt, double At, double Width,
    DateTimeOffset ResetsAt, double SlackHours);

/// <summary>A stretch this PC held one account, with what was left of the whole account at both ends.</summary>
public sealed record WeekTimelineHold(
    int ProfileId, string Name, DateTimeOffset StartsAt, DateTimeOffset EndsAt, double At, double Width,
    double? RemainingFrom, double? RemainingTo, bool IsOpen);

/// <summary>
/// An account's reset in the shown week. For a past one, <paramref name="Left"/> is what of the user's share went
/// unused and <paramref name="Over"/> what was used beyond it; both are null when no reading tells.
/// </summary>
public sealed record WeekTimelineReset(
    int ProfileId, string Name, DateTimeOffset ResetsAt, double At, bool Upcoming, bool Projected,
    double? Left, double? Over, bool Spent);

public sealed record WeekTimelineSummary(
    double Used, double PlannedSoFar, double Lost, WeekTimelineTurn? Turn, WeekTimelineTurn? Next);

/// <summary>
/// The account to use now, or the one after it, which starts when the first resets. A reset that is only projected
/// is not given, and nothing is forecast from it.
/// </summary>
public sealed record WeekTimelineTurn(
    int ProfileId, string Name, bool IsCurrent, DateTimeOffset? ResetsAt, DateTimeOffset? StartsAt,
    double? MineLeft, double? ForecastLost);

/// <summary>
/// An account as it stands now, with its segment in the shown week. <paramref name="Excluded"/> says why it is not
/// planned: "login", "share" or "reset". <paramref name="ResetsAt"/> is its next reset, projected when it was not read.
/// </summary>
public sealed record WeekTimelineRow(
    int ProfileId, string Name, int Share, bool Planned, string? Excluded,
    DateTimeOffset? ResetsAt, bool ResetProjected, DateTimeOffset? SegmentStartsAt, DateTimeOffset? SegmentEndsAt,
    double? SlackHours, double? Remaining, double? MineUsed, double? MineLeft, bool IsCurrent, bool SmartCandidate);

/// <summary>
/// Splits the week between the accounts by their shares, each segment placed as late as its account's reset allows,
/// and sets beside that plan what the history shows: which account this PC held, how much the user used a day, and
/// what was left at each reset. It only measures; no switch follows it. Every time is an instant, read from the
/// request: there is no clock and no time zone here.
/// </summary>
public static class WeekTimelineCalculator
{
    public const int MinimumWeek = -4;
    private static readonly TimeSpan Week = TimeSpan.FromHours(168);
    private static readonly TimeSpan Day = TimeSpan.FromHours(24);
    // The service gives the same reset a few seconds apart from look to look: 07:59 and 08:00 are one reset.
    private static readonly TimeSpan ResetStep = TimeSpan.FromMinutes(5);
    // As TokenRateCalculator.ResetDrift: two readings of one window name its reset no further apart.
    private static readonly TimeSpan ResetDrift = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan ShortHold = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan OpenWithin = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan NearReading = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan NearReset = TimeSpan.FromMinutes(10);
    private const double SpentFrom = 99.5;
    // What the smart switch asks of a candidate: at least 1% left.
    private const double WorthATurn = 1;

    public static WeekTimelineView Calculate(WeekTimelineRequest request)
    {
        var now = request.Now;
        var week = Math.Clamp(request.Week, MinimumWeek, 0);
        var record = new UsageRecord(request);
        var plans = request.Accounts.Select((account, order) => Plan(account, order, record, now)).ToList();
        // Rule B: in reset order. The sort is stable, so equal resets keep the list's order.
        var planned = plans.Where(plan => plan.Excluded is null).OrderBy(plan => plan.NextReset).ToList();
        DateTimeOffset? recordStartsAt = request.History.Count > 0 ? request.History.Min(entry => entry.At) : null;
        bool HasEarlier(DateTimeOffset startsAt) => week > MinimumWeek && request.History.Any(entry => entry.At < startsAt);

        if (planned.Count == 0)
        {
            // Rule C: with no account to plan there is no reset to end the week at, so the last seven days stand in.
            var from = now - Week;
            return new("empty", week, HasEarlier(from), recordStartsAt, from, now, now, null, 0, 0, [], [], [], [],
                new(0, 0, 0, null, null), plans.Select(plan => Row(plan, null)).ToList());
        }

        var slots = Layout(planned, week);
        var (startsAt, endsAt) = (slots[0].StartsAt, slots[^1].EndsAt);
        var total = planned.Sum(plan => (double)plan.Account.Share);
        var days = Days(record, startsAt, now, total / 7);
        var resets = slots.OrderBy(slot => slot.ResetsAt).Select(slot => Reset(slot, record, startsAt, now)).ToList();
        // Rule I: the plan spends the total share evenly over the week, so this much of it is due by now.
        var plannedSoFar = total * Along(Earliest(now, endsAt), startsAt);
        var (turn, next) = week == 0 ? Turns(planned, total, now) : default;
        var summary = new WeekTimelineSummary(days.Sum(day => day.Used ?? 0), plannedSoFar, resets.Sum(reset => reset.Left ?? 0), turn, next);
        // Rule J: the planned accounts as the week runs, then the rest as the list has them.
        var rows = slots.Select(slot => Row(slot.Plan, slot))
            .Concat(plans.Where(plan => plan.Excluded is not null).Select(plan => Row(plan, null))).ToList();
        double? nowAt = now >= startsAt && now < endsAt ? (now - startsAt) / Week : null;
        return new("ok", week, HasEarlier(startsAt), recordStartsAt, startsAt, endsAt, now, nowAt, total, total / 7, days,
            slots.Select(slot => Segment(slot, startsAt)).ToList(), Holds(request, record, startsAt, endsAt, week), resets, summary, rows);
    }

    private sealed record AccountPlan(
        WeekTimelineAccount Account, int Order, string? Excluded, DateTimeOffset? NextReset, bool Projected,
        double? Remaining, double? MineUsed, double? MineLeft);

    private sealed record Slot(AccountPlan Plan, DateTimeOffset StartsAt, DateTimeOffset EndsAt, DateTimeOffset ResetsAt);

    // Rule A: a reset comes round every 168 hours, so one reset time, even a past one or the last the history
    // saw, tells every later one. Only a reset read from the service and still ahead is not a projection.
    private static AccountPlan Plan(WeekTimelineAccount account, int order, UsageRecord record, DateTimeOffset now)
    {
        DateTimeOffset? read = account.ResetsAt is { } given ? Round(given) : null;
        var phase = read ?? record.LastReset(account.ProfileId);
        DateTimeOffset? next = phase is { } known ? Following(known, now) : null;
        var projected = next is not null && !(read > now);
        var excluded = !account.Available ? "login" : account.Share <= 0 ? "share" : next is null ? "reset" : null;
        var (remaining, mineUsed, mineLeft) = State(account, read, next, record, now);
        return new(account, order, excluded, next, projected, remaining, mineUsed, mineLeft);
    }

    // Rule G: what is left of the account now, and how much of the user's share of it. What is read is of one
    // account's week, so it is kept within 0 and 100, and the user's part of it is never less than nothing.
    private static (double? Remaining, double? MineUsed, double? MineLeft) State(
        WeekTimelineAccount account, DateTimeOffset? resetsAt, DateTimeOffset? nextReset, UsageRecord record, DateTimeOffset now)
    {
        if (account.UsedPercent is not { } read || !double.IsFinite(read)) return (null, null, null);
        var used = Math.Clamp(read, 0, 100);
        // A reset already past means the window lapsed and nothing was used since: the account is whole again.
        if (resetsAt <= now) return (100, 0, Math.Clamp(account.Share, 0, 100));
        // Other people's use is taken off only where the history shows it, so use from before the history
        // starts counts as the user's. The window that is running ends at the reset read or, when none was
        // read, at the next one the history tells; with neither there is no window to look in.
        var others = (resetsAt ?? nextReset) is { } ends ? record.OthersIn(account.ProfileId, ends - Week, now) : 0;
        var mine = Math.Max(0, used - others);
        return (100 - used, mine, Math.Max(0, Math.Min(account.Share - mine, 100 - used)));
    }

    // Rules B and C: the segments back to back in reset order, slid as late as every reset allows, and the
    // week that ends where the account with no room left resets.
    private static List<Slot> Layout(List<AccountPlan> planned, int week)
    {
        long total = planned.Sum(plan => (long)plan.Account.Share);
        // Where each segment ends when the first begins at zero, in whole ticks, so that equal shares tie exactly.
        var ends = new long[planned.Count];
        long shares = 0;
        for (var i = 0; i < planned.Count; i++)
        {
            shares += planned[i].Account.Share;
            ends[i] = (long)((Int128)Week.Ticks * shares / total);
        }

        // The ring can move later only by the least room any segment has before its account's reset; the
        // account with that room is the binding one, the earlier in order when two have the same.
        long Room(int i) => planned[i].NextReset!.Value.UtcTicks - ends[i];
        var binding = 0;
        for (var i = 1; i < planned.Count; i++)
            if (Room(i) < Room(binding)) binding = i;

        var endsAt = planned[binding].NextReset!.Value + TimeSpan.FromTicks(Week.Ticks * week);
        var slots = new List<Slot>(planned.Count);
        var from = endsAt - Week;
        for (var step = 1; step <= planned.Count; step++)
        {
            var i = (binding + step) % planned.Count;
            var to = from + TimeSpan.FromTicks(ends[i] - (i == 0 ? 0 : ends[i - 1]));
            slots.Add(new(planned[i], from, to, InWeek(planned[i].NextReset!.Value, endsAt)));
            from = to;
        }

        return slots;
    }

    private static WeekTimelineSegment Segment(Slot slot, DateTimeOffset startsAt)
    {
        var account = slot.Plan.Account;
        var (at, width) = Place(slot.StartsAt, slot.EndsAt, startsAt);
        return new(account.ProfileId, account.Name, account.Share, slot.StartsAt, slot.EndsAt, at, width, slot.ResetsAt, Slack(slot));
    }

    private static double Slack(Slot slot) => Math.Max(0, (slot.ResetsAt - slot.EndsAt).TotalHours);

    // Rule D: the plan spends the same every day; the use is the user's own, and a day not begun has none to show.
    private static List<WeekTimelineDay> Days(UsageRecord record, DateTimeOffset startsAt, DateTimeOffset now, double planned)
    {
        var days = new List<WeekTimelineDay>(7);
        for (var i = 0; i < 7; i++)
        {
            var (from, to) = (startsAt + i * Day, startsAt + (i + 1) * Day);
            double? used = from > now ? null : record.MineIn(from, Earliest(to, now));
            days.Add(new(from, to, used, planned, now >= from && now < to));
        }

        return days;
    }

    private sealed record Stay(int ProfileId, DateTimeOffset From, DateTimeOffset To, bool EndsAtReset = false);

    // Rule E: which registered account this PC held, as the token rates read it, cut to the shown week.
    private static List<WeekTimelineHold> Holds(WeekTimelineRequest request, UsageRecord record, DateTimeOffset startsAt, DateTimeOffset endsAt, int week)
    {
        var now = request.Now;
        var until = Earliest(now, endsAt);
        var names = new Dictionary<int, string>();
        foreach (var account in request.Accounts) names.TryAdd(account.ProfileId, account.Name);

        var stays = record.Local
            .Where(hold => hold.ProfileId != TokenRateCalculator.UnregisteredAccount && names.ContainsKey(hold.ProfileId))
            .Select(hold => new Stay(hold.ProfileId, Latest(hold.From, startsAt), Earliest(hold.To, until)))
            .Where(stay => stay.To > stay.From).OrderBy(stay => stay.From);
        // Every look at the device ends one hold and begins the next, so they are joined first. A short visit
        // to another account is then dropped, and the long hold it interrupted joins up across it. The hold
        // that reaches now stays however short: right after a switch it is the account in use.
        bool ReachesNow(Stay stay) => week == 0 && now - stay.To <= OpenWithin;
        List<Stay> Long(List<Stay> list)
        {
            var newest = list.Count > 0 ? list[^1] : null;
            return list.Where(stay => stay.To - stay.From >= ShortHold || (ReferenceEquals(stay, newest) && ReachesNow(stay))).ToList();
        }

        var kept = Join(Long(Join(stays)));
        // A hold that runs across its account's reset spans two windows: what was left before the reset and the
        // whole account after it are two holds. A switch made a few minutes after a reset leaves a piece too short
        // to show, as any short hold.
        var shown = Long(kept.SelectMany(stay => Split(stay, record)).ToList());

        return shown.Select((stay, index) =>
        {
            var (at, width) = Place(stay.From, stay.To, startsAt);
            // A hold cut at a reset tells what was left just before it, what the reset found, not the whole account
            // it left behind. Only the newest hold can still be going on.
            return new WeekTimelineHold(stay.ProfileId, names[stay.ProfileId], stay.From, stay.To, at, width,
                record.RemainingAt(stay.ProfileId, stay.From), record.RemainingAt(stay.ProfileId, stay.EndsAtReset ? stay.To.AddTicks(-1) : stay.To),
                index == shown.Count - 1 && ReachesNow(stay));
        }).ToList();
    }

    private static IEnumerable<Stay> Split(Stay stay, UsageRecord record)
    {
        var from = stay.From;
        foreach (var reset in record.ResetsWithin(stay.ProfileId, stay.From, stay.To))
        {
            yield return stay with { From = from, To = reset, EndsAtReset = true };
            from = reset;
        }

        yield return stay with { From = from, EndsAtReset = false };
    }

    private static List<Stay> Join(IEnumerable<Stay> stays)
    {
        var joined = new List<Stay>();
        foreach (var stay in stays)
        {
            if (joined.Count > 0 && joined[^1] is var last && last.ProfileId == stay.ProfileId && stay.From - last.To <= ShortHold)
                joined[^1] = last with { To = Latest(last.To, stay.To) };
            else joined.Add(stay);
        }

        return joined;
    }

    // Rule H: what a reset took away. Only a reading of the window that ended there says what was left;
    // without one nothing is claimed.
    private static WeekTimelineReset Reset(Slot slot, UsageRecord record, DateTimeOffset startsAt, DateTimeOffset now)
    {
        var account = slot.Plan.Account;
        var resetsAt = slot.ResetsAt;
        var at = Along(resetsAt, startsAt);
        if (resetsAt > now) return new(account.ProfileId, account.Name, resetsAt, at, true, slot.Plan.Projected, null, null, false);
        if (record.UsedBefore(account.ProfileId, resetsAt) is not { } total)
            return new(account.ProfileId, account.Name, resetsAt, at, false, false, null, null, false);

        var mine = Math.Max(0, total - record.OthersIn(account.ProfileId, resetsAt - Week, resetsAt));
        var left = Math.Max(0, Math.Min(account.Share - mine, 100 - total));
        var over = account.Share < 100 ? Math.Max(0, mine - account.Share) : 0;
        return new(account.ProfileId, account.Name, resetsAt, at, false, false, left, over, total >= SpentFrom);
    }

    // Rule I: the order the smart switch would take the accounts in. It knows no reset but one read and still
    // ahead, so the projected ones come last and are all alike to it: among them what is left decides. What the
    // first cannot use at the plan's pace before it resets is forecast as lost; a projected reset is too unsure
    // to forecast from.
    private static (WeekTimelineTurn? Turn, WeekTimelineTurn? Next) Turns(List<AccountPlan> planned, double total, DateTimeOffset now)
    {
        var queue = planned.Where(plan => plan.MineLeft >= WorthATurn)
            .OrderBy(plan => plan.Projected ? DateTimeOffset.MaxValue : plan.NextReset!.Value)
            .ThenByDescending(plan => plan.MineLeft).ThenBy(plan => plan.Order)
            .ToList();
        if (queue.Count == 0) return (null, null);

        var first = queue[0];
        var resetsAt = Known(first);
        double? lost = resetsAt is { } at ? Math.Max(0, first.MineLeft!.Value - total / Week.TotalHours * (at - now).TotalHours) : null;
        var turn = new WeekTimelineTurn(first.Account.ProfileId, first.Account.Name, first.Account.IsCurrent, resetsAt, null, first.MineLeft, lost);
        if (queue.Count == 1) return (turn, null);

        var second = queue[1];
        return (turn, new(second.Account.ProfileId, second.Account.Name, second.Account.IsCurrent, Known(second), resetsAt, second.MineLeft, null));

        static DateTimeOffset? Known(AccountPlan plan) => plan.Projected ? null : plan.NextReset;
    }

    private static WeekTimelineRow Row(AccountPlan plan, Slot? slot)
    {
        var account = plan.Account;
        return new(account.ProfileId, account.Name, account.Share, plan.Excluded is null, plan.Excluded, plan.NextReset, plan.Projected,
            slot?.StartsAt, slot?.EndsAt, slot is null ? null : Slack(slot), plan.Remaining, plan.MineUsed, plan.MineLeft,
            account.IsCurrent, account.SmartCandidate);
    }

    private sealed record Reading(DateTimeOffset At, double Used, DateTimeOffset? ReadReset, DateTimeOffset? Reset);

    /// <summary>A rise of an account's weekly use, spread evenly over the time between the two readings that show it.</summary>
    private sealed record Use(DateTimeOffset From, DateTimeOffset To, double Moved, bool Mine)
    {
        // The part inside (from, to]; a rise with no length counts wholly at its instant.
        public double Part(DateTimeOffset from, DateTimeOffset to)
        {
            if (To == From) return From > from && From <= to ? Moved : 0;
            var overlap = Earliest(To, to) - Latest(From, from);
            return overlap > TimeSpan.Zero ? Moved * (overlap / (To - From)) : 0;
        }
    }

    /// <summary>The history as the rules read it: each account's readings, the use between them and whose it was.</summary>
    private sealed class UsageRecord
    {
        private readonly Dictionary<int, List<Reading>> _readings = [];
        private readonly Dictionary<int, List<Use>> _uses = [];
        private readonly Dictionary<int, DateTimeOffset> _lastReset = [];

        public UsageRecord(WeekTimelineRequest request)
        {
            var samples = request.History.Where(entry => entry.Kind == QuotaHistoryEntry.Poll && entry.Samples is not null)
                .SelectMany(entry => entry.Samples!).GroupBy(sample => sample.ProfileId);
            foreach (var group in samples)
            {
                if (group.Where(sample => sample.WeeklyResetsAt is not null).MaxBy(sample => sample.SampledAt) is { } newest)
                    _lastReset[group.Key] = Round(newest.WeeklyResetsAt!.Value);
                _readings[group.Key] = group.Where(sample => sample.WeeklyUsed is { } used && double.IsFinite(used))
                    .DistinctBy(sample => sample.SampledAt).OrderBy(sample => sample.SampledAt)
                    .Select(sample => new Reading(sample.SampledAt, Math.Clamp(sample.WeeklyUsed!.Value, 0, 100), sample.WeeklyResetsAt,
                        sample.WeeklyResetsAt is { } reset ? Round(reset) : null))
                    .ToList();
            }

            var observations = TokenRateCalculator.Observations(request.History);
            Local = TokenRateCalculator.Holders(observations.GetValueOrDefault(request.LocalDeviceId) ?? [], request.Now);
            var held = Held(observations.Values.SelectMany(seen => TokenRateCalculator.Holders(seen, request.Now)));
            foreach (var account in request.Accounts)
            {
                if (_uses.ContainsKey(account.ProfileId) || !_readings.TryGetValue(account.ProfileId, out var readings)) continue;
                _uses[account.ProfileId] = Uses(readings, account.Share >= 100, held.GetValueOrDefault(account.ProfileId) ?? []);
            }
        }

        /// <summary>The holds of the device this app runs on, in time order.</summary>
        public IReadOnlyList<TokenRateCalculator.Hold> Local { get; }

        /// <summary>The reset the newest reading of the account named, or null when none named one.</summary>
        public DateTimeOffset? LastReset(int profileId) => _lastReset.TryGetValue(profileId, out var reset) ? reset : null;

        /// <summary>The user's own use of every account in (from, to].</summary>
        public double MineIn(DateTimeOffset from, DateTimeOffset to) =>
            _uses.Values.Sum(uses => uses.Where(use => use.Mine).Sum(use => use.Part(from, to)));

        /// <summary>The resets of one account that its readings named, strictly inside (from, to), in time order.</summary>
        public IEnumerable<DateTimeOffset> ResetsWithin(int profileId, DateTimeOffset from, DateTimeOffset to) =>
            _readings.TryGetValue(profileId, out var readings)
                ? readings.Select(reading => reading.Reset).OfType<DateTimeOffset>().Where(reset => reset > from && reset < to).Distinct().Order()
                : [];

        /// <summary>Other people's use of one account in (from, to].</summary>
        public double OthersIn(int profileId, DateTimeOffset from, DateTimeOffset to) =>
            _uses.TryGetValue(profileId, out var uses) ? uses.Where(use => !use.Mine).Sum(use => use.Part(from, to)) : 0;

        // Rule G: the newest reading tells what was left, and a full account once its window lapsed. Before the
        // first reading, one taken within half an hour after stands in.
        public double? RemainingAt(int profileId, DateTimeOffset at)
        {
            if (!_readings.TryGetValue(profileId, out var readings) || readings.Count == 0) return null;
            var index = LastAtOrBefore(readings, at);
            if (index < 0 && readings[0].At - at > NearReading) return null;
            var reading = readings[Math.Max(0, index)];
            return reading.Reset <= at ? 100 : Math.Clamp(100 - reading.Used, 0, 100);
        }

        // Rule H: the use the newest reading of the window that ended at this reset showed.
        public double? UsedBefore(int profileId, DateTimeOffset reset)
        {
            if (!_readings.TryGetValue(profileId, out var readings)) return null;
            for (var i = LastAtOrBefore(readings, reset); i >= 0 && readings[i].At > reset - Week; i--)
                if (readings[i].ReadReset is { } named && (named - reset).Duration() <= NearReset) return readings[i].Used;
            return null;
        }

        private static int LastAtOrBefore(List<Reading> readings, DateTimeOffset at)
        {
            int low = 0, high = readings.Count - 1;
            while (low <= high)
            {
                var middle = (low + high) / 2;
                if (readings[middle].At <= at) low = middle + 1;
                else high = middle - 1;
            }

            return high;
        }

        // Rule F: when any registered device held each account, the devices' holds joined into stretches in time order.
        private static Dictionary<int, List<(DateTimeOffset From, DateTimeOffset To)>> Held(IEnumerable<TokenRateCalculator.Hold> holds)
        {
            var held = new Dictionary<int, List<(DateTimeOffset From, DateTimeOffset To)>>();
            foreach (var group in holds.GroupBy(hold => hold.ProfileId))
            {
                var stretches = new List<(DateTimeOffset From, DateTimeOffset To)>();
                foreach (var hold in group.OrderBy(hold => hold.From))
                {
                    if (stretches.Count > 0 && hold.From <= stretches[^1].To) stretches[^1] = (stretches[^1].From, Latest(stretches[^1].To, hold.To));
                    else stretches.Add((hold.From, hold.To));
                }

                held[group.Key] = stretches;
            }

            return held;
        }

        // Rule F: the rise between neighbouring readings, however far apart: a rise across a gap is real use.
        private static List<Use> Uses(List<Reading> readings, bool allMine, List<(DateTimeOffset From, DateTimeOffset To)> held)
        {
            var uses = new List<Use>();
            Reading? before = null;
            foreach (var after in readings)
            {
                // Before its first use a window has no reset time yet and reads 0. A reading that shows use and
                // names no reset tells no window to count it in: the readings either side are compared across it.
                if (after.ReadReset is null && after.Used != 0) continue;
                if (before is null)
                {
                    before = after;
                    continue;
                }

                // Two readings that name one reset are of one window, even when the later was taken after that
                // reset: a clock a few seconds ahead of the service, or a service slow to turn the week over,
                // shows the old window's use again, and that is not use of a new one. A reading kept without a
                // reset reads 0, the window not yet used that the next reading is of.
                var sameWindow = before.ReadReset is not { } r0 || (after.ReadReset is { } r1 && (r1 - r0).Duration() <= ResetDrift);
                var from = before.At;
                double moved;
                if (sameWindow)
                {
                    moved = after.Used - before.Used;
                    // A reading lower than the one before in the same window is wrong. It is left out and the
                    // next is compared with the one before it, so the climb back from it does not count as use
                    // and what a window's readings count is what they rose by from first to last.
                    if (moved < 0) continue;
                }
                else
                {
                    // A new window began in between: all it shows was used since the old one lapsed, and since
                    // it began itself, a week before its own reset, when nothing was read for longer than that.
                    moved = after.Used;
                    if (before.Reset is { } lapsed && lapsed > from && lapsed <= after.At) from = lapsed;
                    if (after.Reset is { } ends && ends - from > Week)
                    {
                        var began = ends.ToUniversalTime() - Week;
                        if (began <= after.At) from = began;
                    }
                }

                before = after;
                if (moved <= 0) continue;
                // An account shared with other people moves without the user; the rise is the user's only when
                // one of the registered devices held the account at some moment of it.
                uses.Add(new(from, after.At, moved, allMine || HeldDuring(held, from, after.At)));
            }

            return uses;
        }

        private static bool HeldDuring(List<(DateTimeOffset From, DateTimeOffset To)> held, DateTimeOffset from, DateTimeOffset to)
        {
            int low = 0, high = held.Count - 1;
            while (low <= high)
            {
                var middle = (low + high) / 2;
                if (held[middle].To < from) low = middle + 1;
                else high = middle - 1;
            }

            // The stretches are apart and in order: only the first few that end at or after the start can touch it.
            // A rise takes in the instants of both its readings; a device holds from the look that finds it on the
            // account up to, not at, the instant that ends the hold. The same holds for a rise with no length.
            for (var i = low; i < held.Count && held[i].From <= to; i++)
                if (held[i].To > from) return true;
            return false;
        }
    }

    private static DateTimeOffset Round(DateTimeOffset time)
    {
        var ticks = time.UtcTicks;
        var shift = (ticks + ResetStep.Ticks / 2) / ResetStep.Ticks * ResetStep.Ticks - ticks;
        // At either end of the calendar there may be no five minutes to round to: such a time stands as it was read.
        var (first, last) = (Math.Min(ticks, time.Ticks) + shift, Math.Max(ticks, time.Ticks) + shift);
        return first >= DateTime.MinValue.Ticks && last <= DateTime.MaxValue.Ticks ? time.AddTicks(shift) : time;
    }

    // The first instant after 'now' a whole number of weeks from 'phase'.
    private static DateTimeOffset Following(DateTimeOffset phase, DateTimeOffset now)
    {
        var ahead = (phase - now).Ticks % Week.Ticks;
        return now.AddTicks(ahead > 0 ? ahead : ahead + Week.Ticks);
    }

    // The instant a whole number of weeks from 'time' inside the week that ends at 'endsAt', the end included.
    private static DateTimeOffset InWeek(DateTimeOffset time, DateTimeOffset endsAt)
    {
        var behind = (endsAt - time).Ticks % Week.Ticks;
        return endsAt.AddTicks(-(behind >= 0 ? behind : behind + Week.Ticks));
    }

    private static double Along(DateTimeOffset time, DateTimeOffset startsAt) => Math.Clamp((time - startsAt) / Week, 0, 1);

    private static (double At, double Width) Place(DateTimeOffset from, DateTimeOffset to, DateTimeOffset startsAt)
    {
        var at = Along(from, startsAt);
        return (at, Math.Min(Math.Max(0, Along(to, startsAt) - at), 1 - at));
    }

    private static DateTimeOffset Earliest(DateTimeOffset first, DateTimeOffset second) => first <= second ? first : second;
    private static DateTimeOffset Latest(DateTimeOffset first, DateTimeOffset second) => first >= second ? first : second;
}
