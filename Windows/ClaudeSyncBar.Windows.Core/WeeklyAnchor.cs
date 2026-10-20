using System.Text.Json;

namespace ClaudeSyncBar.Windows.Core;

public static class WeeklyAnchorConfiguration
{
    // The alias follows the current small model, so the cheapest request keeps working across releases.
    public const string Model = "haiku";
    public const string Id = Model;
}

public sealed class WeeklyAnchorPreferences
{
    public HashSet<int> EnabledProfileIds { get; set; } = [];

    public bool IsEnabled(int profileId) => EnabledProfileIds.Contains(profileId);

    public void SetEnabled(int profileId, bool enabled)
    {
        if (enabled)
        {
            EnabledProfileIds.Add(profileId);
        }
        else
        {
            EnabledProfileIds.Remove(profileId);
        }
    }
}

public sealed class WeeklyAnchorRecord
{
    public DateTimeOffset? NextResetAt { get; set; }
    public DateTimeOffset? LastHandledResetAt { get; set; }
    public DateTimeOffset? LastAttemptAt { get; set; }
    public string? LastAttemptConfigurationId { get; set; }
    public DateTimeOffset? LastSuccessAt { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset? ResetDriftCandidateAt { get; set; }
    public int ResetDriftObservationCount { get; set; }
}

public sealed class WeeklyAnchorState
{
    public WeeklyAnchorPreferences Preferences { get; set; } = new();
    public Dictionary<int, WeeklyAnchorRecord> Records { get; set; } = [];
}

public enum WeeklyAnchorDecision
{
    None,
    Observe,
    ConfirmResetDrift,
    Trigger,
    AlreadyActive,
}

public static class WeeklyAnchorDecisionEngine
{
    public const double UnusedThreshold = 99.5;
    public static readonly TimeSpan RetryInterval = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan NoScheduleSuccessGrace = TimeSpan.FromHours(6);
    public static readonly TimeSpan ResetDriftTolerance = TimeSpan.FromMinutes(2);
    public const int RequiredResetDriftObservations = 2;

    public static WeeklyAnchorDecision Decide(
        bool enabled,
        UsageWindow? window,
        WeeklyAnchorRecord record,
        DateTimeOffset now)
    {
        if (!enabled)
        {
            return WeeklyAnchorDecision.None;
        }

        var currentResetAt = window?.ResetsAt;
        if (record.NextResetAt is { } scheduled)
        {
            if (scheduled > now)
            {
                if (currentResetAt is { } current && current > now)
                {
                    var shift = current - scheduled;
                    if (shift > ResetDriftTolerance)
                    {
                        if (window?.RemainingPercent >= UnusedThreshold)
                        {
                            var observations = record.ResetDriftObservationCount + 1;
                            if (observations >= RequiredResetDriftObservations
                                && RetryIsCoolingDown(record, now))
                            {
                                return WeeklyAnchorDecision.None;
                            }

                            return observations >= RequiredResetDriftObservations
                                ? WeeklyAnchorDecision.Trigger
                                : WeeklyAnchorDecision.ConfirmResetDrift;
                        }

                        return WeeklyAnchorDecision.Observe;
                    }

                    if (shift < -ResetDriftTolerance)
                    {
                        return WeeklyAnchorDecision.Observe;
                    }
                }

                return record.ResetDriftObservationCount > 0
                    ? WeeklyAnchorDecision.Observe
                    : WeeklyAnchorDecision.None;
            }

            if (record.LastHandledResetAt is { } handled
                && Math.Abs((handled - scheduled).TotalSeconds) <= 60)
            {
                return currentResetAt is { } current && current > now
                    ? WeeklyAnchorDecision.Observe
                    : WeeklyAnchorDecision.None;
            }

            if (window?.RemainingPercent < UnusedThreshold)
            {
                return WeeklyAnchorDecision.AlreadyActive;
            }

            if (RetryIsCoolingDown(record, now))
            {
                return WeeklyAnchorDecision.None;
            }

            return WeeklyAnchorDecision.Trigger;
        }

        if (window?.RemainingPercent is null)
        {
            return WeeklyAnchorDecision.None;
        }

        if (window.RemainingPercent < UnusedThreshold)
        {
            return currentResetAt is { } resetAt && resetAt > now
                ? WeeklyAnchorDecision.Observe
                : WeeklyAnchorDecision.None;
        }

        if (record.LastSuccessAt is { } lastSuccess
            && now - lastSuccess < NoScheduleSuccessGrace)
        {
            return currentResetAt is { } resetAt && resetAt > now
                ? WeeklyAnchorDecision.Observe
                : WeeklyAnchorDecision.None;
        }

        if (RetryIsCoolingDown(record, now))
        {
            return WeeklyAnchorDecision.None;
        }

        return WeeklyAnchorDecision.Trigger;
    }

    public static bool RetryIsCoolingDown(WeeklyAnchorRecord record, DateTimeOffset now)
    {
        if (record.LastAttemptAt is not { } lastAttempt
            || now - lastAttempt >= RetryInterval)
        {
            return false;
        }

        return record.LastError is null || record.LastAttemptConfigurationId == WeeklyAnchorConfiguration.Id;
    }
}

public sealed class WeeklyAnchorStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };
    private readonly WindowsPaths _paths;

    public WeeklyAnchorStore(WindowsPaths paths)
    {
        _paths = paths;
    }

    public WeeklyAnchorState Load()
    {
        WindowsPathSafety.EnsureFile(_paths.WeeklyAnchorFile, "주간 anchor 설정 파일");
        if (!File.Exists(_paths.WeeklyAnchorFile))
        {
            return new WeeklyAnchorState();
        }

        try
        {
            return JsonSerializer.Deserialize<WeeklyAnchorState>(
                       File.ReadAllText(_paths.WeeklyAnchorFile),
                       JsonOptions)
                ?? new WeeklyAnchorState();
        }
        catch (JsonException)
        {
            return new WeeklyAnchorState();
        }
    }

    public void Save(WeeklyAnchorState state)
    {
        WindowsPathSafety.EnsureFile(_paths.WeeklyAnchorFile, "주간 anchor 설정 파일");
        Directory.CreateDirectory(Path.GetDirectoryName(_paths.WeeklyAnchorFile)!);
        var temporary = Path.Combine(
            Path.GetDirectoryName(_paths.WeeklyAnchorFile)!,
            $".weekly-anchor.{Guid.NewGuid():N}.tmp");
        File.WriteAllText(temporary, JsonSerializer.Serialize(state, JsonOptions));
        try
        {
            File.Move(temporary, _paths.WeeklyAnchorFile, overwrite: true);
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

public sealed class WeeklyAnchorService
{
    private const string Prompt = "Claude SyncBar 주간 주기 시작 확인입니다. 도구를 사용하지 말고 ‘확인’만 답해주세요.";
    private readonly WindowsPaths _paths;
    private readonly AuthStore _authStore;
    private readonly ClaudeAuthMaintenanceService? _authMaintenanceService;
    internal Func<string, string[], IReadOnlyDictionary<string, string?>, CancellationToken, Task<ProcessResult>>? RunOverride { get; init; }
    internal string? ExecutableOverride { get; init; }

    public WeeklyAnchorService(
        WindowsPaths paths,
        AuthStore authStore,
        ClaudeAuthMaintenanceService? authMaintenanceService = null)
    {
        _paths = paths;
        _authStore = authStore;
        _authMaintenanceService = authMaintenanceService;
    }

    public async Task<string> SendAsync(int profileId, CancellationToken cancellationToken = default)
    {
        var credentials = _authStore.ReadCredentials(profileId);
        try
        {
            return await SendOnceAsync(credentials, cancellationToken);
        }
        catch (Exception error) when (
            _authMaintenanceService is not null
            && RequiresAuthenticationRefresh(error))
        {
            var refreshed = await _authMaintenanceService.RefreshAsync(
                profileId,
                cancellationToken);
            if (refreshed.DidDefer)
            {
                throw new ClaudeSyncBarException(
                    "Claude Code가 실행 중이어서 인증 갱신을 미뤘습니다. Claude Code를 닫은 뒤 다시 시도해 주세요.");
            }

            credentials = _authStore.ReadCredentials(profileId);
            return await SendOnceAsync(credentials, cancellationToken);
        }
    }

    internal async Task<string> SendOnceAsync(
        ProfileCredentials credentials,
        CancellationToken cancellationToken)
    {
        var claude = ExecutableOverride ?? FindClaude()
            ?? throw new ClaudeSyncBarException("Claude Code CLI를 찾지 못했습니다. 주간 anchor를 보낼 수 없습니다.");
        var runtime = Path.Combine(
            _paths.ExternalRuntimeDirectory,
            $"ClaudeSyncBarWeeklyAnchor-{Guid.NewGuid():N}");
        var claudeHome = Path.Combine(runtime, "claude");
        var workspace = Path.Combine(runtime, "workspace");
        WindowsPathSafety.EnsurePrivateDirectory(_paths.ExternalRuntimeDirectory, "주간 메시지 실행 디렉터리");
        WindowsPathSafety.EnsurePrivateDirectory(runtime, "주간 메시지 임시 디렉터리");
        try
        {
            WindowsPathSafety.EnsurePrivateDirectory(claudeHome, "주간 메시지 Claude 설정 디렉터리");
            WindowsPathSafety.EnsurePrivateDirectory(workspace, "주간 메시지 작업 공간");
            // An empty config directory, no tools and no MCP servers: the one
            // request cannot read the user's settings or touch the machine.
            var arguments = new[]
            {
                "--print",
                "--model", WeeklyAnchorConfiguration.Model,
                "--tools", "",
                "--strict-mcp-config",
                "--disable-slash-commands",
                "--no-session-persistence",
                "--output-format", "text",
                Prompt,
            };
            // The token goes through the environment so no credential file,
            // and therefore no refresh token, exists in the temporary home.
            var environment = ClaudeCliEnvironment.Isolated(claudeHome);
            environment["CLAUDE_CODE_OAUTH_TOKEN"] = credentials.AccessToken;
            var result = RunOverride is { } run
                ? await run(claude, arguments, environment, cancellationToken)
                : await ProcessRunner.RunAsync(claude, arguments, standardInput: "",
                    cancellationToken: cancellationToken, timeout: TimeSpan.FromMinutes(2), environment: environment,
                    workingDirectory: workspace);
            var output = result.CombinedOutput.Trim();
            if (result.ExitCode != 0)
            {
                // CLI diagnostics must never persist access tokens in status records.
                output = output.Replace(credentials.AccessToken, "[redacted]", StringComparison.Ordinal);
                throw new ClaudeSyncBarException(
                    output.Length == 0 ? "주간 주기 시작 메시지 전송에 실패했습니다." : output.Length > 900 ? output[..900] : output);
            }

            if (string.IsNullOrWhiteSpace(result.StandardOutput))
                throw new ClaudeSyncBarException("Claude Code가 응답 완료 결과를 반환하지 않았습니다.");
            return result.StandardOutput.Trim();
        }
        finally
        {
            if (Directory.Exists(runtime))
            {
                Directory.Delete(runtime, recursive: true);
            }
        }
    }

    private static bool RequiresAuthenticationRefresh(Exception error)
    {
        if (error is AuthenticationRequiredException)
        {
            return true;
        }

        var message = error.Message.ToLowerInvariant();
        return new[]
        {
            "401",
            "authentication_error",
            "oauth token has expired",
            "invalid bearer token",
            "invalid api key",
            "please run /login",
        }.Any(message.Contains);
    }

    private static string? FindClaude()
    {
        return ClaudeCliLocator.Find();
    }
}
