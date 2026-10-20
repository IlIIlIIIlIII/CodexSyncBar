using System.Text.Json;

namespace ClaudeSyncBar.Windows.Core;

public sealed record AuthMaintenanceResult(
    bool DidRefresh,
    bool DidSync,
    bool DidDefer,
    bool IsPartial,
    string Output);

public sealed class AuthMaintenanceState
{
    public DateTimeOffset? LastFullSyncAt { get; set; }
}

public sealed class AuthMaintenanceStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private readonly WindowsPaths _paths;

    public AuthMaintenanceStateStore(WindowsPaths paths)
    {
        _paths = paths;
    }

    public AuthMaintenanceState Load()
    {
        WindowsPathSafety.EnsureFile(_paths.AuthMaintenanceStateFile, "인증 유지 상태 파일");
        if (!File.Exists(_paths.AuthMaintenanceStateFile))
        {
            return new AuthMaintenanceState();
        }

        try
        {
            return JsonSerializer.Deserialize<AuthMaintenanceState>(
                       File.ReadAllText(_paths.AuthMaintenanceStateFile),
                       JsonOptions)
                ?? new AuthMaintenanceState();
        }
        catch (JsonException)
        {
            return new AuthMaintenanceState();
        }
    }

    public void Save(AuthMaintenanceState state)
    {
        WindowsPathSafety.EnsureFile(_paths.AuthMaintenanceStateFile, "인증 유지 상태 파일");
        Directory.CreateDirectory(Path.GetDirectoryName(_paths.AuthMaintenanceStateFile)!);
        var temporary = Path.Combine(
            Path.GetDirectoryName(_paths.AuthMaintenanceStateFile)!,
            $".auth-maintenance.{Guid.NewGuid():N}.tmp");
        File.WriteAllText(temporary, JsonSerializer.Serialize(state, JsonOptions));
        try
        {
            File.Move(temporary, _paths.AuthMaintenanceStateFile, overwrite: true);
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

/// <summary>
/// Refreshes one canonical full-auth profile through the Claude Code CLI. The
/// refresh runs in a temporary CLAUDE_CONFIG_DIR so a failed or incomplete
/// refresh never replaces the working profile.
/// </summary>
public sealed class ClaudeAuthMaintenanceService
{
    private readonly WindowsPaths _paths;
    private readonly AuthStore _authStore;
    private readonly LocalSwitchService? _localSwitchService;
    private readonly Func<string, string, CancellationToken, Task<string>>? _runRefreshServer;
    private readonly AuthRefreshTransactionStore _refreshTransactions;

    public ClaudeAuthMaintenanceService(
        WindowsPaths paths,
        AuthStore authStore,
        LocalSwitchService? localSwitchService = null,
        Func<string, string, CancellationToken, Task<string>>? runRefreshServer = null)
    {
        _paths = paths;
        _authStore = authStore;
        _localSwitchService = localSwitchService;
        _runRefreshServer = runRefreshServer;
        _refreshTransactions = new(paths, authStore);
    }

    public IReadOnlyList<string> RecoverPendingRefreshes() => _refreshTransactions.Recover();

    public async Task<AuthMaintenanceResult> RefreshIfNeededAsync(
        int profileId,
        TimeSpan threshold,
        CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        var credentials = _authStore.ReadCredentials(profileId);
        if (credentials.ExpiresAt is { } expiresAt
            && expiresAt - DateTimeOffset.UtcNow > threshold)
        {
            return new AuthMaintenanceResult(
                DidRefresh: false,
                DidSync: false,
                DidDefer: false,
                IsPartial: false,
                Output: $"profile={profileId} action=noop result=ok");
        }

        return await RefreshAsync(profileId, cancellationToken);
    }

    public async Task<AuthMaintenanceResult> RefreshAsync(
        int profileId,
        CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        if (_refreshTransactions.Recover().Count > 0)
            throw new ClaudeSyncBarException("이전 인증 갱신의 복구를 먼저 완료해 주세요.");
        _authStore.ReconcileActiveCredentials(profileId);
        var source = _authStore.ProfileAuthFile(profileId);
        var before = _authStore.ReadAuthFile(source);
        if (ShouldDeferForRunningClient(before))
        {
            return new AuthMaintenanceResult(
                DidRefresh: false,
                DidSync: false,
                DidDefer: true,
                IsPartial: false,
                Output: $"profile={profileId} action=deferred-client-running result=ok");
        }

        var claude = _runRefreshServer is null ? FindClaude() : "synthetic-cli";
        _paths.EnsureDirectories();
        var runtime = Path.Combine(
            _paths.LoginSessionsDirectory,
            $"refresh-profile-{profileId}-{Guid.NewGuid():N}");
        var temporaryAuth = Path.Combine(runtime, "auth.json");
        WindowsPathSafety.EnsurePrivateDirectory(runtime, "Claude 인증 갱신 세션 디렉터리");
        _authStore.CopyAuthFile(source, temporaryAuth);
        _refreshTransactions.Begin(runtime, profileId, before);
        var canDeleteRuntime = false;

        try
        {
            var result = await (_runRefreshServer ?? RunRefreshAsync)(
                claude,
                runtime,
                cancellationToken);
            var refreshed = await ReadRefreshedAuthAsync(
                temporaryAuth,
                before,
                cancellationToken);
            if (!string.Equals(
                    before.AccountId,
                    refreshed.AccountId,
                    StringComparison.Ordinal))
            {
                throw new ClaudeSyncBarException(
                    "Claude 인증 갱신 결과의 계정이 기존 프로필과 달라 저장하지 않았습니다.");
            }

            var accessChanged = !string.Equals(
                before.AccessToken,
                refreshed.AccessToken,
                StringComparison.Ordinal);
            var refreshChanged = !string.Equals(
                before.RefreshToken,
                refreshed.RefreshToken,
                StringComparison.Ordinal);
            if (!_refreshTransactions.TryComplete(runtime))
                throw new ClaudeSyncBarException("인증이 동시에 변경되어 갱신 결과를 복구 기록에 보존했습니다.");
            canDeleteRuntime = true;

            return new AuthMaintenanceResult(
                DidRefresh: accessChanged || refreshChanged,
                DidSync: false,
                DidDefer: false,
                IsPartial: false,
                Output: $"profile={profileId} action=refreshed result=ok {result}");
        }
        catch
        {
            // Token rotation is an external side effect. A failure after the exchange must
            // not destroy the only valid new refresh token; preserve it under the same
            // account and generation checks, or keep the private recovery directory.
            try { canDeleteRuntime = _refreshTransactions.TryComplete(runtime); }
            catch { canDeleteRuntime = false; }
            throw;
        }
        finally
        {
            try
            {
                if (canDeleteRuntime && Directory.Exists(runtime))
                {
                    Directory.Delete(runtime, recursive: true);
                }
            }
            catch
            {
                // A CLI child can briefly retain a file handle. The runtime
                // is isolated and a later maintenance pass can clean it up.
            }
#if SYNCBAR_LINUX
            _authStore.ProtectRecoveryAuthFile(temporaryAuth);
#endif
        }
    }

    // `claude auth login` exchanges a refresh token supplied through the
    // environment without opening a browser or sending a message, so an idle
    // account's weekly cycle is not started by keeping its login alive.
    private async Task<string> RunRefreshAsync(
        string claude,
        string runtime,
        CancellationToken cancellationToken)
    {
        var before = _authStore.ReadAuthFile(Path.Combine(runtime, "auth.json"));
        var scopes = before.Oauth.Scopes is { Count: > 0 } stored ? stored : ["user:inference", "user:profile"];
        var environment = ClaudeCliEnvironment.Isolated(runtime);
        environment["CLAUDE_CODE_OAUTH_REFRESH_TOKEN"] = before.RefreshToken;
        environment["CLAUDE_CODE_OAUTH_SCOPES"] = string.Join(' ', scopes);
        ProcessResult result;
        try
        {
            result = await ProcessRunner.RunAsync(
                claude,
                ["auth", "login", "--claudeai"],
                standardInput: "",
                cancellationToken: cancellationToken,
                timeout: TimeSpan.FromMinutes(2),
                environment: environment);
        }
        finally
        {
            // The exchange may have rotated the refresh token even when the CLI then
            // failed or was cancelled; whatever it stored is the copy to keep.
            if (File.Exists(Path.Combine(runtime, ".credentials.json")))
                _authStore.CaptureClaudeHome(runtime, before.Account);
        }

        if (result.ExitCode != 0)
        {
            var detail = result.CombinedOutput.Trim()
                .Replace(before.RefreshToken!, "[redacted]", StringComparison.Ordinal)
                .Replace(before.AccessToken!, "[redacted]", StringComparison.Ordinal);
            // The token endpoint answers a revoked or expired refresh token with
            // a client error; retrying cannot help, only signing in again can.
            if (System.Text.RegularExpressions.Regex.IsMatch(detail, @"status code 40[013]\b"))
                throw new AuthenticationRequiredException("Claude 인증 갱신이 거부되었습니다. 다시 로그인해 주세요.");
            throw new ClaudeSyncBarException(
                detail.Length == 0 ? "Claude 인증 갱신에 실패했습니다." : detail.Length > 900 ? detail[..900] : detail);
        }

        return "validated=true";
    }

    private bool ShouldDeferForRunningClient(ClaudeAuthFile auth)
    {
        if (_localSwitchService is null || !_localSwitchService.HasClaudeClientsRunning())
        {
            return false;
        }

        return string.Equals(
            _authStore.ReadActiveAccountId(),
            auth.AccountId,
            StringComparison.Ordinal);
    }

    private async Task<ClaudeAuthFile> ReadRefreshedAuthAsync(
        string path,
        ClaudeAuthFile before,
        CancellationToken cancellationToken)
    {
        ClaudeAuthFile? lastValid = null;
        Exception? lastError = null;
        for (var attempt = 0; attempt < 12; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var candidate = _authStore.ReadAuthFile(path);
                lastValid = candidate;
                if (!string.Equals(
                        before.AccessToken,
                        candidate.AccessToken,
                        StringComparison.Ordinal)
                    || !string.Equals(
                        before.RefreshToken,
                        candidate.RefreshToken,
                        StringComparison.Ordinal))
                {
                    return candidate;
                }
            }
            catch (Exception error) when (
                error is ClaudeSyncBarException
                or AuthenticationRequiredException
                or IOException)
            {
                lastError = error;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
        }

        if (lastValid is not null)
        {
            return lastValid;
        }

        throw new ClaudeSyncBarException(
            lastError?.Message ?? "Claude 인증 갱신 결과 파일을 읽지 못했습니다.");
    }

    private static string FindClaude()
    {
        return ClaudeCliLocator.Find()
            ?? throw new ClaudeSyncBarException(
                "공식 Claude Code CLI를 찾지 못했습니다. CLI 관리에서 설치해 주세요.");
    }
}
