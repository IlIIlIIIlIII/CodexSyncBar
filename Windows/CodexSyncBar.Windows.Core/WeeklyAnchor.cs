using System.Text.Json;

namespace CodexSyncBar.Windows.Core;

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

    private static bool RetryIsCoolingDown(WeeklyAnchorRecord record, DateTimeOffset now)
    {
        if (record.LastAttemptAt is not { } lastAttempt
            || now - lastAttempt >= RetryInterval)
        {
            return false;
        }

        var error = record.LastError;
        return string.IsNullOrWhiteSpace(error)
            || (!error.Contains("Reading additional input from stdin", StringComparison.Ordinal)
                && !error.Contains("no_biscuit_no_service", StringComparison.Ordinal));
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
    private const string Prompt = "Codex SyncBar 주간 주기 시작 확인입니다. 도구를 사용하지 말고 ‘확인’만 답해주세요.";
    private readonly WindowsPaths _paths;
    private readonly AuthStore _authStore;
    private readonly CodexAuthMaintenanceService? _authMaintenanceService;

    public WeeklyAnchorService(
        WindowsPaths paths,
        AuthStore authStore,
        CodexAuthMaintenanceService? authMaintenanceService = null)
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
                throw new CodexSyncBarException(
                    "Codex 프로세스가 실행 중이어서 인증 갱신을 미뤘습니다. Codex를 닫은 뒤 다시 시도해 주세요.");
            }

            credentials = _authStore.ReadCredentials(profileId);
            return await SendOnceAsync(credentials, cancellationToken);
        }
    }

    private async Task<string> SendOnceAsync(
        ProfileCredentials credentials,
        CancellationToken cancellationToken)
    {
        var codex = FindCodex()
            ?? throw new CodexSyncBarException("Codex CLI를 찾지 못했습니다. 주간 anchor를 보낼 수 없습니다.");
        var runtime = Path.Combine(
            Path.GetTempPath(),
            $"CodexSyncBarWeeklyAnchor-{Guid.NewGuid():N}");
        var codexHome = Path.Combine(runtime, "codex");
        var workspace = Path.Combine(runtime, "workspace");
        var response = Path.Combine(runtime, "response.txt");
        Directory.CreateDirectory(codexHome);
        Directory.CreateDirectory(workspace);
        try
        {
            var arguments = new[]
            {
                "exec",
                "--disable", "plugins",
                "--disable", "remote_plugin",
                "--disable", "apps",
                "--ephemeral",
                "--ignore-user-config",
                "--ignore-rules",
                "--skip-git-repo-check",
                "--sandbox", "read-only",
                "--color", "never",
                "--model", "gpt-5.4-mini",
                "--config", "model_provider=\"syncbar_chatgpt\"",
                "--config", "model_providers.syncbar_chatgpt.name=\"ChatGPT SyncBar\"",
                "--config", "model_providers.syncbar_chatgpt.base_url=\"https://chatgpt.com/backend-api/codex\"",
                "--config", "model_providers.syncbar_chatgpt.env_key=\"CODEX_SYNCBAR_ACCESS_TOKEN\"",
                "--config", "model_providers.syncbar_chatgpt.env_http_headers={\"ChatGPT-Account-Id\"=\"CODEX_SYNCBAR_ACCOUNT_ID\"}",
                "--config", "model_providers.syncbar_chatgpt.http_headers={\"originator\"=\"codex_cli_rs\"}",
                "--config", "model_providers.syncbar_chatgpt.wire_api=\"responses\"",
                "--config", "model_providers.syncbar_chatgpt.requires_openai_auth=false",
                "-C", workspace,
                "--output-last-message", response,
                Prompt,
            };
            var result = await ProcessRunner.RunAsync(
                codex,
                arguments,
                cancellationToken: cancellationToken,
                timeout: TimeSpan.FromMinutes(2),
                environment: new Dictionary<string, string?>
                {
                    ["CODEX_HOME"] = codexHome,
                    ["CODEX_SYNCBAR_ACCESS_TOKEN"] = credentials.AccessToken,
                    ["CODEX_SYNCBAR_ACCOUNT_ID"] = credentials.AccountId,
                    ["OPENAI_API_KEY"] = null,
                    ["AZURE_OPENAI_API_KEY"] = null,
                    ["NO_COLOR"] = "1",
                });
            var output = result.CombinedOutput.Trim();
            if (result.ExitCode != 0)
            {
                throw new CodexSyncBarException(
                    output.Length > 900 ? output[..900] : output);
            }

            return File.Exists(response)
                ? File.ReadAllText(response).Trim()
                : output;
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
            "401 unauthorized",
            "login required",
            "failed to refresh token",
            "invalid 'refresh_token'",
            "access token could not be refreshed",
            "chatgpt login did not make it to this service",
        }.Any(message.Contains);
    }

    private static string? FindCodex()
    {
        return CodexCliLocator.Find();
    }
}
