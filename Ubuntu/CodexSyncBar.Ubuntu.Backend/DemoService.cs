using System.Text.Json;
using CodexSyncBar.Windows.Core;

namespace CodexSyncBar.Ubuntu.Backend;

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
    private bool _fiveHour = true;
    private bool _weekly = true;
    private bool _autostart;
    private string? _disabledDevice;
    private OperationView? _operation;
    public bool ShutdownRequested { get; private set; }

    public DemoService()
    {
        DashboardUsage Usage(double session, double week, int credits) => new(
            new(session, DateTimeOffset.Parse("2026-10-03T07:00:00Z"), 18000),
            new(week, DateTimeOffset.Parse("2026-10-06T00:00:00Z"), 604800), credits,
            [DateTimeOffset.Parse("2026-10-10T00:00:00Z")], DateTimeOffset.Parse("2026-10-03T03:40:00Z"));
        _accounts =
        [
            new(1, "p***@example.com", "개인 계정", false, false, Usage(38, 54, 1)),
            new(2, "w***@example.com", "업무 계정", false, false, Usage(28, 42, 3)),
        ];
    }

    private string Revision => "demo-" + _revision;
    private object Settings => new { fiveHour = _fiveHour, codexWeekly = _weekly, launchAtLogin = _autostart, weekly = new { preferences = new { }, records = new { } }, isDemo = true };
    private string Label(int id) => _accounts.FirstOrDefault(a => a.Id == id)?.Alias ?? "알 수 없음";
    private IReadOnlyList<DeviceView> Devices => new DeviceView[]
    {
        new("local", "이 Ubuntu PC", "local", true, true, _local, Label(_local), DateTimeOffset.UtcNow, "ready"),
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
                _fiveHour = Wire.Flag(p, "fiveHour", _fiveHour);
                _weekly = Wire.Flag(p, "codexWeekly", _weekly);
                _autostart = Wire.Flag(p, "launchAtLogin", _autostart);
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
            case "tokens": return new
            {
                devices = new[]
                {
                    new { id = "local", displayName = "이 Ubuntu PC", isReachable = true, summary = new { totalTokens = 1284000, inputTokens = 1060000, outputTokens = 224000, requests = 286, buckets = Array.Empty<object>() }, estimatedCostUsd = 12.45m, error = (string?)null },
                    new { id = "ssh:dev", displayName = "개발 서버", isReachable = !_offline, summary = new { totalTokens = 3648000, inputTokens = 3080000, outputTokens = 568000, requests = 712, buckets = Array.Empty<object>() }, estimatedCostUsd = 34.18m, error = (string?)null },
                },
                collectedAt = DateTimeOffset.UtcNow, estimatedCostUsd = 46.63m,
            };
            case "cli.status": return new { version = "demo", path = "/demo/codex", manager = "demo", canUpdate = false, notice = "격리된 데모에서는 실제 CLI를 실행하지 않습니다." };
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
