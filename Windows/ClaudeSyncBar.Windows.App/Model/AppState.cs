using System.Text.Json.Nodes;

namespace ClaudeSyncBar.App.Model;

public enum NoticeKind { Info, Success, Warning, Error }

/// <summary>
/// What every screen shows: the service's latest snapshot, the account chosen
/// on the switch page and its preview. All state comes from the service; a
/// choice here changes nothing until it is applied.
/// </summary>
public sealed class AppState(ServiceClient client)
{
    public ServiceClient Client { get; } = client;
    public JsonObject Snapshot { get; private set; } = new();
    public JsonObject? Preview { get; private set; }
    public int? SelectedProfileId { get; private set; }
    public bool RefreshRequested { get; private set; }
    public string? ConnectionError { get; private set; }

    /// <summary>The SSH devices as configured: host, user, port, authentication. Null until first loaded.</summary>
    public JsonArray? DeviceList { get; private set; }
    private Task? _poll;
    private bool _pollAgain;
    private int _previewGeneration;
    private string? _previewRevision;
    private (string?, string?) _lastOperation;
    private string? _pendingRequestId;
    private List<int> _accountIds = [];
    // Jobs started from this app, whose outcome is reported when they end.
    private readonly HashSet<string> _started = [];

    /// <summary>Raised on the UI thread whenever anything shown may have changed.</summary>
    public event Action? Changed;

    /// <summary>A short message for the user: an action's result or a failure.</summary>
    public event Action<string, NoticeKind>? Notice;

    public IReadOnlyList<JsonObject> Accounts => Snapshot["accounts"] is JsonArray array ? array.OfType<JsonObject>().ToArray() : [];
    public IReadOnlyList<JsonObject> Devices => Snapshot["devices"] is JsonArray array ? array.OfType<JsonObject>().ToArray() : [];
    public int? ActiveProfileId => Int(Snapshot["activeProfileId"]);
    public bool Busy => QuotaFormat.Flag(Snapshot["isBusy"]);
    public JsonObject? Operation => Snapshot["operation"] as JsonObject;
    public JsonNode? Settings => Snapshot["settings"];
    public bool IsDemo => QuotaFormat.Flag(Snapshot["isDemo"]);
    public string? Error => ConnectionError ?? Str(Snapshot["error"]);
    public JsonObject? Active => Lookup(ActiveProfileId);
    public JsonObject? Selected => Lookup(SelectedProfileId);
    public IReadOnlyList<string> VisibleQuotas => QuotaFormat.Visible(Settings);

    public JsonObject? Lookup(int? id) => id is null ? null : Accounts.FirstOrDefault(account => Int(account["id"]) == id);

    public static string AccountName(JsonNode? account) =>
        Str(account?["alias"]) is { Length: > 0 } alias ? alias : Str(account?["email"]) is { Length: > 0 } email ? email : "계정";

    public static string? Str(JsonNode? value) => value is JsonValue v && v.TryGetValue<string>(out var text) ? text : null;

    public static int? Int(JsonNode? value) => value is JsonValue v && v.TryGetValue<int>(out var number) ? number : null;

    public void Say(string message, NoticeKind kind = NoticeKind.Info) => Notice?.Invoke(message, kind);

    /// <summary>
    /// Looks at the service again, after whatever the caller just changed. A look already under
    /// way may have read the state before that change, so another follows it.
    /// </summary>
    public Task PollAsync()
    {
        if (_poll is { IsCompleted: false })
        {
            _pollAgain = true;
            return _poll;
        }

        return _poll = PollLoopAsync();
    }

    /// <summary>The timer's look: none while another is under way.</summary>
    public void PollIfIdle()
    {
        if (_poll is not { IsCompleted: false }) _poll = PollLoopAsync();
    }

    private async Task PollLoopAsync()
    {
        do
        {
            _pollAgain = false;
            try
            {
                var snapshot = await Client.CallAsync("snapshot") as JsonObject ?? new JsonObject();
                ConnectionError = null;
                Apply(snapshot);
            }
            catch (ServiceError error)
            {
                ConnectionError = error.Message;
                Changed?.Invoke();
            }
        } while (_pollAgain);
    }

    private void Apply(JsonObject snapshot)
    {
        Snapshot = snapshot;
        var ids = Accounts.Select(account => Int(account["id"]) ?? 0).ToList();
        var accountsChanged = !ids.SequenceEqual(_accountIds);
        _accountIds = ids;
        if (accountsChanged || Lookup(SelectedProfileId) is null)
        {
            if (Lookup(SelectedProfileId) is null)
                SelectedProfileId = (ActiveProfileId is { } active ? FirstOther(active) ?? active : (int?)null) ?? (ids.Count > 0 ? ids[0] : null);
            _ = RequestPreviewAsync();
        }

        var operation = Operation;
        var key = (Str(operation?["id"]), Str(operation?["state"]));
        if (key != _lastOperation)
        {
            _lastOperation = key;
            if (key.Item2 is "completed" or "failed" or "recoveryRequired" or "cancelled")
            {
                _pendingRequestId = null;
                _ = RequestPreviewAsync();
                if (key.Item1 is { } id && _started.Remove(id)) Report(operation!);
            }
        }

        if (_previewRevision != Str(Snapshot["configurationRevision"]) && !Busy) _ = RequestPreviewAsync();
        Changed?.Invoke();
    }

    // How a job the user started ended. The service words a generic success the same for every kind.
    private void Report(JsonObject operation)
    {
        var state = Str(operation["state"]);
        var message = Str(operation["message"]) ?? "";
        if (state == "completed" && message is "" or "작업을 완료했습니다.")
            message = Str(operation["kind"]) switch
            {
                "login" => "로그인을 마쳤어요.",
                "import" => "인증 파일을 가져왔어요.",
                "logout" => "로그아웃했어요.",
                "auth-refresh" => "인증을 갱신했어요.",
                "auth-sync" => "인증을 모든 장치에 맞췄어요.",
                "weekly-settings" => "주간 자동 메시지 설정을 바꿨어요.",
                _ => "",
            };
        if (message.Length == 0) return;
        Say(message, state switch
        {
            "completed" => NoticeKind.Success,
            "cancelled" => NoticeKind.Info,
            "recoveryRequired" => NoticeKind.Warning,
            _ => NoticeKind.Error,
        });
    }

    private void Track(JsonNode? result)
    {
        if (result is JsonObject operation && Str(operation["id"]) is { } id && Str(operation["state"]) is not null && Str(operation["kind"]) is not null)
            _started.Add(id);
    }

    /// <summary>Looks up the SSH devices as configured again.</summary>
    public async Task LoadDeviceListAsync()
    {
        try { DeviceList = await Client.CallAsync("device.list") as JsonArray; }
        catch (ServiceError error) { Say(error.Message, NoticeKind.Warning); }
        Changed?.Invoke();
    }

    public JsonObject? DeviceConfiguration(string? id) => id is null ? null
        : DeviceList?.OfType<JsonObject>().FirstOrDefault(device => Str(device["id"]) == id);

    // The switch page starts on an account other than the one in use, the one worth switching to.
    private int? FirstOther(int active) => Accounts.Where(account => Int(account["id"]) != active && !NeedsLogin(account))
        .Select(account => Int(account["id"])).FirstOrDefault(id => id is not null);

    public static bool NeedsLogin(JsonNode? account) => QuotaFormat.Flag(account?["needsLogin"]) || QuotaFormat.Flag(account?["isPending"]);

    public void Select(int profileId)
    {
        if (SelectedProfileId == profileId) return;
        SelectedProfileId = profileId;
        _pendingRequestId = null;
        _ = RequestPreviewAsync();
        Changed?.Invoke();
    }

    public async Task RequestPreviewAsync()
    {
        var generation = ++_previewGeneration;
        Preview = null;
        if (SelectedProfileId is not { } id || Busy || NeedsLogin(Lookup(id))) { Changed?.Invoke(); return; }
        _previewRevision = Str(Snapshot["configurationRevision"]);
        try
        {
            var preview = await Client.CallAsync("preview", new { profileId = id }) as JsonObject;
            if (generation != _previewGeneration) return;
            Preview = preview;
        }
        catch (ServiceError error)
        {
            if (generation == _previewGeneration && error.Code != "busy") Say(error.Message, NoticeKind.Warning);
        }

        if (generation == _previewGeneration) Changed?.Invoke();
    }

    public bool CanApply => Preview is { } preview && QuotaFormat.Flag(preview["canApply"]) && !Busy
        && Str(Operation?["state"]) != "recoveryRequired";

    /// <summary>Applies the previewed account to every included device.</summary>
    public async Task ApplyAsync()
    {
        if (Preview is not { } preview || Busy) return;
        _pendingRequestId ??= Guid.NewGuid().ToString("N");
        await ApplyPreviewAsync(preview, _pendingRequestId);
    }

    public async Task ApplyPreviewAsync(JsonObject preview, string? requestId = null)
    {
        try
        {
            Track(await Client.CallAsync("apply", new { previewId = Str(preview["previewId"]), requestId = requestId ?? Guid.NewGuid().ToString("N") }));
        }
        catch (ServiceError error)
        {
            Say(error.Message, NoticeKind.Error);
            if (error.Code is "stale_preview" or "invalid_preview")
            {
                _pendingRequestId = null;
                await RequestPreviewAsync();
            }
        }

        await PollAsync();
    }

    /// <summary>Usage of every account and the state of every device, looked up again.</summary>
    public async Task RefreshAsync()
    {
        if (RefreshRequested || Busy) return;
        RefreshRequested = true;
        Changed?.Invoke();
        try { await Client.CallAsync("refresh", new { kind = "all" }); }
        catch (ServiceError error) { Say(error.Message, NoticeKind.Error); }
        // Until a look taken after the job started, the last one still says nothing runs:
        // the refresh would seem to stop for a moment and then go on.
        try { await PollAsync(); }
        finally { RefreshRequested = false; }
        Changed?.Invoke();
        await RequestPreviewAsync();
    }

    /// <summary>A refresh asked for or under way.</summary>
    public bool Refreshing => RefreshRequested || (Busy && Str(Operation?["kind"]) == "refresh");

    /// <summary>Runs a command and reports its outcome; returns its result, or null when it failed.</summary>
    public async Task<JsonNode?> CommandAsync(string method, object? parameters = null, string? success = null, TimeSpan? timeout = null)
    {
        try
        {
            var result = await Client.CallAsync(method, parameters, timeout ?? TimeSpan.FromSeconds(120));
            Track(result);
            if (success is not null) Say(success, NoticeKind.Success);
            return result ?? new JsonObject();
        }
        catch (ServiceError error)
        {
            Say(error.Message, NoticeKind.Error);
            return null;
        }
        finally
        {
            await PollAsync();
        }
    }

    /// <summary>The label of the operation under way, for the buttons it disables.</summary>
    public string BusyLabel => Str(Operation?["kind"]) switch
    {
        "login" => "로그인 대기 중",
        "refresh" => "새로고침 중…",
        "recovery" => "복구 중…",
        "switch" => "적용 중…",
        "auto-switch" => "자동 전환 중…",
        _ => "작업 중…",
    };
}
