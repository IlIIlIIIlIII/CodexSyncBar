using System.Text.Json;
using ClaudeSyncBar.Windows.Core;

namespace ClaudeSyncBar.Ubuntu.Backend;

// Explicit opt-in fixture. Never constructs production paths, auth stores,
// browsers, SSH clients or the controller's background network work.
public sealed class DemoService : IManagementService
{
    private readonly object _gate = new();
    private readonly List<AccountView> _accounts;
    private readonly Dictionary<string, SwitchPreview> _previews = [];
    private readonly Dictionary<string, (string PreviewId, string OperationId)> _requests = [];
    private readonly Dictionary<string, OperationView> _operations = [];
    private readonly List<SshDeviceConfiguration> _configuration =
    [
        new() { Id = "dev", DisplayName = "개발 서버", Host = "dev.example.invalid", Username = "demo", Port = 22, Enabled = true },
        new() { Id = "build", DisplayName = "빌드 서버", Host = "build.example.invalid", Username = "demo", Port = 22, Enabled = true },
    ];
    private int _local = 1;
    private readonly Dictionary<string, int> _remoteAccounts = new() { ["ssh:dev"] = 1, ["ssh:build"] = 1 };
    private Dictionary<string, int>? _recoveryBefore;
    private int _revision = 1;
    private bool _offline;
    private bool _failApply;
    private bool _failRecovery;
    private bool _busy;
    private bool _weekly = true;
    private bool _fiveHour = true;
    private bool _taskbar = true;
    private bool _autoSwitch;
    private bool _autoResume;
    private bool _autostart;
    private string? _disabledDevice;
    private OperationView? _operation;
    private int _tokenHours = TokenRateCalculator.MaximumHours;
    private string _tokenBasis = TokenBurnBasis.Uncached;
    private DemoTokens? _tokens;
    public bool ShutdownRequested { get; private set; }

    public DemoService()
    {
        DashboardUsage Usage(double week, double? fiveHour, string? fiveHourReset) => new(
            new(week, DateTimeOffset.Parse("2026-10-06T00:00:00Z"), 604800), DateTimeOffset.Parse("2026-10-03T03:40:00Z"),
            fiveHour is null ? null : new(fiveHour.Value, fiveHourReset is null ? null : DateTimeOffset.Parse(fiveHourReset), 18000));
        _accounts =
        [
            new(1, "p***@example.com", "개인 계정", false, false, Usage(54, 81, "2026-10-03T06:10:00Z")),
            new(2, "w***@example.com", "업무 계정", false, false, Usage(42, 0, null)),
        ];
    }

    /// <summary>The same fixture with other accounts; the Windows app shows fresh values relative to now.</summary>
    public DemoService(IEnumerable<AccountView> accounts, int activeProfileId)
    {
        _accounts = [.. accounts];
        _local = activeProfileId;
        foreach (var device in _remoteAccounts.Keys.ToArray()) _remoteAccounts[device] = activeProfileId;
    }

    private string Revision => "demo-" + _revision;
    private object Settings => new { claudeFiveHour = _fiveHour, claudeWeekly = _weekly, taskbarUsage = _taskbar, tokenPeriodHours = _tokenHours, tokenRateBasis = _tokenBasis, autoSwitchEnabled = _autoSwitch, autoSwitchLast = (object?)null, autoResumeEnabled = _autoResume, autoResumeLast = (object?)null, launchAtLogin = _autostart, weekly = new { preferences = new { }, records = new { } }, isDemo = true };
    private string Label(int id) => _accounts.FirstOrDefault(a => a.Id == id)?.Alias ?? "알 수 없음";
    private IReadOnlyList<DeviceView> Devices => new DeviceView[]
    {
        new("local", BackendPlatform.LocalDeviceName, "local", true, true, _local, Label(_local), DateTimeOffset.UtcNow, "ready"),
    }.Concat(_configuration.Select(d => new DeviceView("ssh:" + d.Id, d.DisplayName, "ssh", _disabledDevice != "ssh:" + d.Id,
        !_offline && _disabledDevice != "ssh:" + d.Id, _offline ? null : _remoteAccounts["ssh:" + d.Id],
        _offline ? "알 수 없음" : Label(_remoteAccounts["ssh:" + d.Id]), DateTimeOffset.UtcNow,
        _disabledDevice == "ssh:" + d.Id ? "disabled" : _offline ? "unreachable" : "ready",
        _disabledDevice == "ssh:" + d.Id ? "대상 제외" : _offline ? "연결을 확인하지 못했습니다." : null))).ToArray();

    public Task<object?> DispatchAsync(string method, JsonElement p, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock (_gate) return Task.FromResult(Dispatch(method, p));
    }

    private object? Dispatch(string method, JsonElement p)
    {
        switch (method)
        {
            case "snapshot": return new ManagementSnapshot(1, _accounts.ToArray(), Devices, _local, _busy, _operation,
                Revision, DateTimeOffset.UtcNow, null, Settings, _accounts.ToDictionary(a => a.Id.ToString(), a => a.Usage), true);
            case "smart.preview":
            {
                // Every demo account counts as a $200 Max account, and every demo value as fresh.
                var now = DateTimeOffset.UtcNow;
                DateTimeOffset? Reset(AccountView a) => a.Usage?.Weekly?.ResetsAt is { } reset && reset > now ? reset : null;
                var best = _accounts.Where(a => !a.NeedsLogin && a.Usage is { Weekly.RemainingPercent: > 0, FiveHour.RemainingPercent: > SmartSwitch.FiveHourMinimum })
                    .OrderBy(a => Reset(a) ?? DateTimeOffset.MaxValue).ThenByDescending(a => a.Usage!.Weekly!.RemainingPercent).FirstOrDefault();
                if (best is null) return new { found = false, message = SmartSwitch.NoneMessage };
                var reason = SmartSwitch.Reason($"{best.Usage!.Weekly!.RemainingPercent:0}%", $"{best.Usage.FiveHour!.RemainingPercent:0}%", Reset(best));
                if (best.Id == _local) return new { found = true, alreadyActive = true, profileId = best.Id, accountLabel = best.Alias, reason, message = $"이미 조건에 가장 맞는 {best.Alias}을(를) 쓰고 있습니다. ({reason})" };
                using var document = JsonDocument.Parse($"{{\"profileId\":{best.Id}}}");
                return new { found = true, alreadyActive = false, profileId = best.Id, accountLabel = best.Alias, reason, preview = Dispatch("preview", document.RootElement.Clone()), message = $"{best.Alias}(으)로 전환합니다. ({reason})" };
            }
            case "preview":
            {
                if (_busy) throw new RpcException("busy", "작업이 진행 중입니다.");
                var account = _accounts.FirstOrDefault(a => a.Id == Wire.Number(p, "profileId") && !a.NeedsLogin && !a.IsPending)
                    ?? throw new RpcException("account_unavailable", "로그인된 계정을 선택해 주세요.");
                var selectedDevice = Wire.Text(p, "deviceId");
                if (selectedDevice.Length > 0 && selectedDevice != "local" && !selectedDevice.StartsWith("ssh:", StringComparison.Ordinal)) selectedDevice = "ssh:" + selectedDevice;
                if (selectedDevice.Length > 0 && !Devices.Any(d => d.Id == selectedDevice && d.Enabled))
                    throw new RpcException("device_unavailable", "활성 장치를 선택해 주세요.");
                var targets = Devices.Select(d => new PreviewTarget(d, account.Id, account.Alias,
                    !d.Enabled || selectedDevice.Length > 0 && d.Id != selectedDevice ? "excluded" : d.CurrentProfileId == account.Id ? "reapply" : "switch",
                    d.Enabled && (selectedDevice.Length == 0 || d.Id == selectedDevice))).ToArray();
                var ready = targets.Where(t => t.Included).All(t => t.IsReachable);
                var preview = new SwitchPreview(Guid.NewGuid().ToString("N"), account.Id, Revision,
                    DateTimeOffset.UtcNow, ready, ready ? null : "모든 활성 장치의 연결을 확인해 주세요.", targets);
                _previews[preview.PreviewId] = preview;
                return preview;
            }
            case "apply":
            {
                var id = Wire.Text(p, "requestId");
                var previewId = Wire.Text(p, "previewId");
                if (id.Length is < 1 or > 128) throw new RpcException("invalid_request", "요청 식별자가 필요합니다.");
                if (_requests.TryGetValue(id, out var prior))
                {
                    if (prior.PreviewId != previewId) throw new RpcException("duplicate_request", "다른 작업에 사용된 요청 식별자입니다.");
                    return _operations[prior.OperationId];
                }
                if (_busy) throw new RpcException("busy", "작업이 진행 중입니다.");
                if (_operation?.State == "recoveryRequired") throw new RpcException("recovery_required", "이전 작업을 먼저 복구해 주세요.");
                if (!_previews.TryGetValue(previewId, out var preview)) throw new RpcException("stale_preview", "미리보기를 다시 확인해 주세요.");
                if (!preview.CanApply) throw new RpcException("devices_unavailable", preview.BlockingReason!);
                if (preview.ConfigurationRevision != Revision || DateTimeOffset.UtcNow - preview.CreatedAt > TimeSpan.FromMinutes(5)
                    || preview.Targets.Where(t => t.Included).Any(t => Devices.First(d => d.Id == t.Id) is var current
                        && (current.CurrentProfileId != t.CurrentProfileId || current.IsReachable != t.IsReachable || current.Enabled != t.Enabled)))
                    throw new RpcException("stale_preview", "현재 계정 또는 설정이 변경되었습니다. 다시 확인해 주세요.");
                var op = new OperationView(Guid.NewGuid().ToString("N"), "switch", preview.ProfileId, "queued",
                    "모든 활성 장치의 계정을 확인하고 있습니다.", preview.Targets.Where(t => t.Included).Select(t => new SwitchTargetResult(t.Id, t.DisplayName, "pending")).ToArray(), DateTimeOffset.UtcNow);
                _operation = op;
                _operations[op.Id] = op;
                _requests[id] = (previewId, op.Id);
                _busy = true;
                _ = CompleteSwitchAsync(op);
                return op;
            }
            case "operation": return _operations.GetValueOrDefault(Wire.Text(p, "operationId")) ?? _operation;
            case "settings.get": return Settings;
            case "settings.save":
                _weekly = Wire.Flag(p, "claudeWeekly", _weekly);
                _fiveHour = Wire.Flag(p, "claudeFiveHour", _fiveHour);
                _taskbar = Wire.Flag(p, "taskbarUsage", _taskbar);
                _autoSwitch = Wire.Flag(p, "autoSwitchEnabled", _autoSwitch);
                _autoResume = Wire.Flag(p, "autoResumeEnabled", _autoResume);
                _autostart = Wire.Flag(p, "launchAtLogin", _autostart);
                _tokenHours = Math.Clamp(Wire.Number(p, "tokenPeriodHours", _tokenHours), 1, TokenRateCalculator.MaximumHours);
                _tokenBasis = TokenBurnBasis.Normalize(Wire.Text(p, "tokenRateBasis", _tokenBasis));
                return Settings;
            case "device.list": return _configuration;
            case "device.test": return new { isReachable = !_offline, message = "데모 장치 연결을 확인했습니다.", hostKey = (object?)null };
            case "device.trust": return new { trusted = true, hostKey = (object?)null };
            case "account.rename":
            {
                var index = _accounts.FindIndex(a => a.Id == Wire.Number(p, "profileId"));
                if (index < 0) throw new RpcException("account_unavailable", "계정을 찾지 못했습니다.");
                var alias = Wire.Text(p, "alias").Trim();
                if (alias.Length > 5) throw new RpcException("invalid_alias", "별칭은 5글자 이하로 입력해 주세요.");
                _accounts[index] = _accounts[index] with { Alias = alias.Length == 0 ? _accounts[index].Email : alias };
                _revision++;
                return new { saved = true };
            }
            case "account.move":
            {
                var index = _accounts.FindIndex(a => a.Id == Wire.Number(p, "profileId"));
                if (index < 0) throw new RpcException("account_unavailable", "계정을 찾지 못했습니다.");
                var next = Math.Clamp(index + Wire.Number(p, "offset"), 0, _accounts.Count - 1);
                (_accounts[index], _accounts[next]) = (_accounts[next], _accounts[index]);
                _revision++;
                return new { saved = true };
            }
            case "refresh": return Completed("refresh", "데모 상태를 새로고침했습니다.");
            case "tokens":
            case "tokens.view":
            {
                _tokens ??= new DemoTokens(DateTimeOffset.UtcNow, _accounts, _local);
                _tokenHours = Math.Clamp(Wire.Number(p, "hours", _tokenHours), 1, TokenRateCalculator.MaximumHours);
                _tokenBasis = TokenBurnBasis.Normalize(Wire.Text(p, "basis", _tokenBasis));
                var devices = _tokens.Devices(_offline);
                var view = TokenRateCalculator.Calculate(new(_tokens.CollectedAt, _tokenHours, _tokenBasis, devices, _tokens.History,
                    _tokens.History.FirstOrDefault()?.At, _accounts.Where(a => !a.IsPending).Select(a => new TokenAccountInput(a.Id, a.Alias, a.Plan)).ToArray(),
                    _local, _fiveHour, _weekly));
                if (method == "tokens.view") return new { collectedAt = _tokens.CollectedAt, view };
                return new
                {
                    devices = devices.Select(d => new { id = d.Id, displayName = d.DisplayName, isReachable = d.IsReachable, summary = d.Summary?.WithoutMinutes(),
                        estimatedCostUsd = d.Summary?.Buckets.Sum(b => TokenUsagePricing.EstimateUsd(b).PricedUsd) ?? 0m, error = d.Error }),
                    collectedAt = _tokens.CollectedAt, estimatedCostUsd = devices.Sum(d => d.Summary?.Buckets.Sum(b => TokenUsagePricing.EstimateUsd(b).PricedUsd) ?? 0m),
                    view,
                };
            }
            case "cli.status": return new { version = "demo", path = "/demo/claude", manager = "demo", canUpdate = false, notice = "격리된 데모에서는 실제 CLI를 실행하지 않습니다." };
            case "recovery.retry":
                _failRecovery = false;
                if (_recoveryBefore is not null)
                {
                    _local = _recoveryBefore["local"];
                    foreach (var id in _remoteAccounts.Keys.ToArray()) _remoteAccounts[id] = _recoveryBefore[id];
                    _recoveryBefore = null;
                }
                return Completed("recovery", "이전 계정으로 복구했습니다.");
            case "demo.configure":
                if (_busy) throw new RpcException("busy", "작업이 진행 중입니다.");
                _offline = Wire.Flag(p, "offline", _offline);
                _failApply = Wire.Flag(p, "failApply", _failApply);
                _failRecovery = Wire.Flag(p, "failRecovery", _failRecovery);
                _local = Wire.Number(p, "localProfileId", _local);
                foreach (var id in _remoteAccounts.Keys.ToArray()) _remoteAccounts[id] = Wire.Number(p, "remoteProfileId", _remoteAccounts[id]);
                if (Wire.Item(p, "disabledDeviceId").ValueKind == JsonValueKind.String) { _disabledDevice = Wire.Text(p, "disabledDeviceId"); _revision++; }
                return new { configured = true };
            case "quit":
                if (_busy) throw new RpcException("busy", "작업이 진행 중입니다.");
                ShutdownRequested = true;
                return new { stopped = true };
            default: throw new RpcException("demo_readonly", "이 동작은 실제 관리 모드에서 사용할 수 있습니다. 데모는 인증·SSH·브라우저를 변경하지 않습니다.");
        }
    }

    private OperationView Completed(string kind, string message)
    {
        _operation = new(Guid.NewGuid().ToString("N"), kind, null, "completed", message, [], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        _operations[_operation.Id] = _operation;
        return _operation;
    }

    private async Task CompleteSwitchAsync(OperationView op)
    {
        await Task.Delay(120);
        lock (_gate)
        {
            _operation = op with { State = "applying", Message = "계정을 적용하고 검증하고 있습니다.", Targets = op.Targets.Select(t => t with { State = "applying" }).ToArray() };
            _operations[op.Id] = _operation;
        }
        await Task.Delay(160);
        lock (_gate)
        {
            if (!_failApply)
            {
                if (op.Targets.Any(t => t.Id == "local")) _local = op.ProfileId!.Value;
                foreach (var target in op.Targets.Where(t => t.Id.StartsWith("ssh:", StringComparison.Ordinal))) _remoteAccounts[target.Id] = op.ProfileId!.Value;
                _operation = op with { State = "completed", Message = "모든 활성 장치에 계정을 적용했습니다.", Targets = op.Targets.Select(t => t with { State = "verified" }).ToArray(), FinishedAt = DateTimeOffset.UtcNow };
            }
            else
            {
                if (_failRecovery)
                {
                    _recoveryBefore = new(_remoteAccounts) { ["local"] = _local };
                    foreach (var target in op.Targets.Where(t => t.Id.StartsWith("ssh:", StringComparison.Ordinal))) _remoteAccounts[target.Id] = op.ProfileId!.Value;
                }
                _operation = op with { State = _failRecovery ? "recoveryRequired" : "failed", Message = _failRecovery ? "일부 장치의 복구를 확인하지 못했습니다." : "전환 실패 · 이전 상태로 복구했습니다.", Targets = op.Targets.Select(t => t with { State = _failRecovery && t.Id.StartsWith("ssh:") ? "recoveryRequired" : "restored" }).ToArray(), FinishedAt = DateTimeOffset.UtcNow };
            }
            _operations[op.Id] = _operation;
            _busy = false;
        }
    }
    public Task StopAsync() => Task.CompletedTask;
    public void Dispose() { }
}

/// <summary>
/// Five days of made-up session logs and quota readings, shaped like heavy real use: about 81M tokens an hour,
/// nineteen in twenty of them cache reads. The account in use moves to the next once its five hours run low,
/// the app is off for a night (no readings) and for an afternoon nobody knows which account the desktop used.
/// </summary>
internal sealed class DemoTokens
{
    private const int Hours = 120;
    private static readonly double[] Weights = [0.55, 0.35, 0.15, 0.08, 0, 0, 0, 0.05, 0.25, 0.6, 0.85, 1, 0.8, 0.9, 1, 1.1, 1, 0.95, 0.7, 0.8, 1, 1.1, 1, 0.8];
    private static readonly TimeSpan Slot = TimeSpan.FromMinutes(5);
    private readonly DeviceTokenUsageSummary _local;
    private readonly DeviceTokenUsageSummary _remote;

    public DateTimeOffset CollectedAt { get; }
    public IReadOnlyList<QuotaHistoryEntry> History { get; }

    public DemoTokens(DateTimeOffset now, IReadOnlyList<AccountView> accounts, int active)
    {
        CollectedAt = new DateTimeOffset(now.UtcTicks - now.UtcTicks % TimeSpan.TicksPerMinute, TimeSpan.Zero);
        var start = CollectedAt - TimeSpan.FromHours(Hours) + TimeSpan.FromMinutes(20);
        var off = (From: CollectedAt - TimeSpan.FromHours(71), To: CollectedAt - TimeSpan.FromHours(62));
        var unknown = (From: CollectedAt - TimeSpan.FromHours(81), To: CollectedAt - TimeSpan.FromHours(73));
        var sampled = accounts.Where(a => !a.NeedsLogin && !a.IsPending).ToList();
        var order = sampled.OrderBy(a => a.Id == active ? 0 : 1).ThenBy(a => a.Id).Take(3).ToList();
        var quota = sampled.ToDictionary(a => a.Id, a => new Quota(a.Plan, order.IndexOf(a) switch
        {
            0 => (40, CollectedAt - TimeSpan.FromHours(40)),
            1 => (22, CollectedAt + TimeSpan.FromDays(3)),
            2 => (8, CollectedAt + TimeSpan.FromDays(5)),
            _ => (a.Usage?.Weekly?.UsedPercent ?? 10, a.Usage?.Weekly?.ResetsAt ?? CollectedAt + TimeSpan.FromDays(4)),
        }));

        // Tokens per hour, from the hour of day, then spread over five-minute slots.
        var hourly = new double[Hours];
        var output = new double[Hours];
        for (var k = 0; k < Hours; k++)
        {
            // The hour of day in Korea, where the real logs these follow were written.
            var clock = CollectedAt - TimeSpan.FromHours(k) - TimeSpan.FromMinutes(30) + TimeSpan.FromHours(9);
            var weight = Weights[clock.Hour] * (clock.Hour == 3 && clock.Day % 2 == 1 ? 0 : 1);
            hourly[k] = weight * (0.7 + 0.6 * Noise(k, 1.3));
            output[k] = 0.0049 * (0.75 + 0.5 * Noise(k, 7.1));
        }

        var scale = 9.72e9 / hourly.Sum();
        var local = new List<long[]>();
        var remote = new List<long[]>();
        var history = new List<QuotaHistoryEntry>();
        int? holder = null;
        var chatted = false;
        var quiet = 0;
        for (var time = start; time < CollectedAt; time += Slot)
        {
            var k = (int)((CollectedAt - time).TotalHours);
            var slot = (int)((CollectedAt - time).TotalMinutes / 5);
            var total = hourly[Math.Min(k, Hours - 1)] * scale / 12 * (0.6 + 0.8 * Noise(slot, 9.7));
            foreach (var state in quota.Values) state.Expire(time);
            var user = order.FirstOrDefault(a => quota[a.Id] is { FiveHour: < 97, Weekly: < 97 }) ?? order[0];
            if (user.Id != holder && !(time >= off.From && time < off.To))
                history.Add(new QuotaHistoryEntry { Kind = QuotaHistoryEntry.Switch, At = time, ProfileId = user.Id, DeviceIds = ["local", "ssh:dev"] });
            holder = user.Id;
            // One night, well after the last tokens, a chat in the desktop app used the account with no Claude Code log.
            quiet = total > 0 ? 0 : quiet + 1;
            if (quiet >= 5 && !chatted && k is > 20 and < 48)
            {
                chatted = true;
                quota[order[0].Id].Chat(time);
            }

            if (total > 0)
            {
                var minute = (long)Math.Floor((time + TimeSpan.FromMinutes(2)).ToUnixTimeMilliseconds() / 60_000d);
                var uncached = 0d;
                foreach (var (model, index, share, longContext) in new[] { ("claude-opus-5-5", 0, 0.88, 1), ("claude-sonnet-5-5", 1, 0.12, 0) })
                {
                    foreach (var (list, part) in new[] { (local, 0.914), (remote, 0.086) })
                    {
                        var tokens = total * share * part;
                        var outTokens = tokens * output[Math.Min(k, Hours - 1)];
                        var input = tokens - outTokens;
                        var write = input * 0.0381;
                        list.Add([minute, index, 0, longContext, (long)input, (long)(input * 0.9619), (long)write, (long)(write * 0.16),
                            (long)outTokens, (long)(outTokens * 0.433), Math.Max(1, (long)Math.Round(tokens / 136_000))]);
                        uncached += input * 0.0381 + outTokens;
                    }
                }

                quota[user.Id].Use(uncached, time, Noise(slot, 3.7), Noise(slot, 5.3));
            }

            // The app looks every five minutes while it runs; for an afternoon the desktop's account is unknown.
            var look = time + TimeSpan.FromMinutes(4);
            if (look >= off.From && look < off.To) continue;
            history.Add(new QuotaHistoryEntry
            {
                Kind = QuotaHistoryEntry.Poll, At = look,
                Samples = sampled.Select(a => quota[a.Id].Sample(a.Id, look)).ToArray(),
                Devices = [new("local", look >= unknown.From && look < unknown.To ? null : user.Id, look)],
            });
        }

        History = history;
        _local = Summary(local);
        _remote = Summary(remote);
    }

    public IReadOnlyList<TokenDeviceInput> Devices(bool offline) =>
    [
        new("local", BackendPlatform.LocalDeviceName, "local", true, null, _local),
        new("ssh:dev", "개발 서버", "ssh", !offline, offline ? "사용량 수집을 완료하지 못했습니다." : null, offline ? null : _remote),
    ];

    private static double Noise(int index, double salt)
    {
        var value = Math.Sin(index * 78.233 + salt) * 43758.5453;
        return value - Math.Floor(value);
    }

    private DeviceTokenUsageSummary Summary(List<long[]> minutes)
    {
        string[] models = ["claude-opus-5-5", "claude-sonnet-5-5"];
        var buckets = minutes.GroupBy(row => row[1]).Select(group => new ModelTokenUsage
        {
            Model = models[group.Key], ServiceTier = "default", IsLongContext = group.Key == 0, Requests = group.Sum(row => row[10]),
            InputTokens = group.Sum(row => row[4]), CachedInputTokens = group.Sum(row => row[5]), CacheWriteInputTokens = group.Sum(row => row[6]),
            CacheWriteOneHourInputTokens = group.Sum(row => row[7]), OutputTokens = group.Sum(row => row[8]),
            ReasoningOutputTokens = group.Sum(row => row[9]), TotalTokens = group.Sum(row => row[4] + row[8]),
        }).ToList();
        return new DeviceTokenUsageSummary
        {
            SchemaVersion = 7, GeneratedAt = CollectedAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"), ScannedFiles = 42,
            Requests = buckets.Sum(b => b.Requests), InputTokens = buckets.Sum(b => b.InputTokens), CachedInputTokens = buckets.Sum(b => b.CachedInputTokens),
            CacheWriteInputTokens = buckets.Sum(b => b.CacheWriteInputTokens), CacheWriteOneHourInputTokens = buckets.Sum(b => b.CacheWriteOneHourInputTokens),
            OutputTokens = buckets.Sum(b => b.OutputTokens), ReasoningOutputTokens = buckets.Sum(b => b.ReasoningOutputTokens),
            TotalTokens = buckets.Sum(b => b.TotalTokens), Buckets = buckets, Models = [.. models], Tiers = ["default"], Minutes = minutes,
            WindowDays = 30, FirstRecordAt = minutes.Count == 0 ? null
                : DateTimeOffset.FromUnixTimeMilliseconds(minutes[0][0] * 60_000).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"),
        };
    }

    /// <summary>One account's five-hour and weekly use as the service would report it, in whole percent.</summary>
    private sealed class Quota(string? plan, (double Used, DateTimeOffset ResetsAt) weekly)
    {
        // Percent of each quota per million uncached tokens, by plan; a smaller plan's 1% is a smaller amount.
        private readonly (double FiveHour, double Weekly) _rate = plan switch
        {
            "Max 5x" => (18.4, 1.04),
            "Pro" => (92, 5.2),
            _ => (4.6, 0.26),
        };
        private DateTimeOffset? _fiveHourReset;
        private DateTimeOffset _weeklyReset = weekly.ResetsAt;
        public double FiveHour { get; private set; }
        public double Weekly { get; private set; } = weekly.Used;

        public void Expire(DateTimeOffset time)
        {
            if (_fiveHourReset is { } reset && time >= reset) { FiveHour = 0; _fiveHourReset = null; }
            while (time >= _weeklyReset) { Weekly = 0; _weeklyReset += TimeSpan.FromDays(7); }
        }

        public void Use(double uncached, DateTimeOffset time, double noiseFive, double noiseWeek)
        {
            _fiveHourReset ??= time + TimeSpan.FromHours(5);
            FiveHour = Math.Min(100, FiveHour + uncached / 1e6 * _rate.FiveHour * (0.85 + 0.3 * noiseFive));
            Weekly = Math.Min(100, Weekly + uncached / 1e6 * _rate.Weekly * (0.85 + 0.3 * noiseWeek));
        }

        public void Chat(DateTimeOffset time)
        {
            _fiveHourReset ??= time + TimeSpan.FromHours(5);
            FiveHour = Math.Min(100, FiveHour + 4);
            Weekly = Math.Min(100, Weekly + 0.4);
        }

        public QuotaSample Sample(int profileId, DateTimeOffset at) =>
            new(profileId, at, plan, Math.Floor(FiveHour), _fiveHourReset, Math.Floor(Weekly), _weeklyReset);
    }
}
