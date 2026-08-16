using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodexSyncBar.Windows.Core;

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
/// Refreshes one canonical full-auth profile through Codex app-server. The
/// refresh runs in a temporary CODEX_HOME so a failed or incomplete refresh
/// never replaces the working profile.
/// </summary>
public sealed class CodexAuthMaintenanceService
{
    private readonly WindowsPaths _paths;
    private readonly AuthStore _authStore;
    private readonly LocalSwitchService? _localSwitchService;

    public CodexAuthMaintenanceService(
        WindowsPaths paths,
        AuthStore authStore,
        LocalSwitchService? localSwitchService = null)
    {
        _paths = paths;
        _authStore = authStore;
        _localSwitchService = localSwitchService;
    }

    public async Task<AuthMaintenanceResult> RefreshIfNeededAsync(
        int profileId,
        TimeSpan threshold,
        CancellationToken cancellationToken = default)
    {
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

        var codex = FindCodex();
        _paths.EnsureDirectories();
        var runtime = Path.Combine(
            _paths.LoginSessionsDirectory,
            $"refresh-profile-{profileId}-{Guid.NewGuid():N}");
        var temporaryAuth = Path.Combine(runtime, "auth.json");
        WindowsPathSafety.EnsureDirectory(runtime, "Codex 인증 갱신 세션 디렉터리");
        File.Copy(source, temporaryAuth, overwrite: true);

        try
        {
            var result = await RunRefreshServerAsync(
                codex,
                runtime,
                cancellationToken);
            var refreshed = await ReadRefreshedAuthAsync(
                temporaryAuth,
                before,
                cancellationToken);
            if (!string.Equals(
                    before.Tokens.AccountId,
                    refreshed.Tokens.AccountId,
                    StringComparison.Ordinal))
            {
                throw new CodexSyncBarException(
                    "Codex 인증 갱신 결과의 계정이 기존 프로필과 달라 저장하지 않았습니다.");
            }

            var accessChanged = !string.Equals(
                before.Tokens.AccessToken,
                refreshed.Tokens.AccessToken,
                StringComparison.Ordinal);
            var refreshChanged = !string.Equals(
                before.Tokens.RefreshToken,
                refreshed.Tokens.RefreshToken,
                StringComparison.Ordinal);
            _authStore.ImportAuth(temporaryAuth, profileId, replaceExisting: true);

            if (string.Equals(_authStore.ReadActiveAccountId(), before.Tokens.AccountId, StringComparison.Ordinal))
            {
                _authStore.SwitchActive(profileId);
            }

            return new AuthMaintenanceResult(
                DidRefresh: accessChanged || refreshChanged,
                DidSync: false,
                DidDefer: false,
                IsPartial: false,
                Output: $"profile={profileId} action=refreshed result=ok {result}");
        }
        finally
        {
            try
            {
                if (Directory.Exists(runtime))
                {
                    Directory.Delete(runtime, recursive: true);
                }
            }
            catch
            {
                // A Codex child can briefly retain a file handle. The runtime
                // is isolated and a later maintenance pass can clean it up.
            }
        }
    }

    private static async Task<string> RunRefreshServerAsync(
        string codex,
        string runtime,
        CancellationToken cancellationToken)
    {
        using var process = ProcessRunner.StartInteractive(
            codex,
            [
                "app-server",
                "--stdio",
                "-c",
                "cli_auth_credentials_store=\"file\"",
            ],
            redirectStandardInput: true,
            workingDirectory: runtime,
            environment: new Dictionary<string, string?>
            {
                ["CODEX_HOME"] = runtime,
                ["NO_COLOR"] = "1",
            });

        try
        {
            var errors = CaptureErrorsAsync(process.StandardError, cancellationToken);
            await WriteMessageAsync(
                process.StandardInput,
                new JsonObject
                {
                    ["id"] = 1,
                    ["method"] = "initialize",
                    ["params"] = new JsonObject
                    {
                        ["clientInfo"] = new JsonObject
                        {
                            ["name"] = "codex-syncbar-windows",
                            ["title"] = "Codex SyncBar for Windows",
                            ["version"] = "1.0.0",
                        },
                        ["capabilities"] = new JsonObject(),
                    },
                },
                cancellationToken);

            var requestedAccountRead = false;
            var requestedRateLimits = false;
            while (true)
            {
                var line = await process.StandardOutput.ReadLineAsync(cancellationToken);
                if (line is null)
                {
                    var detail = (await errors).LastOrDefault(value => !string.IsNullOrWhiteSpace(value));
                    throw new CodexSyncBarException(
                        detail ?? "Codex 인증 갱신 서버가 응답하기 전에 종료되었습니다.");
                }

                using var document = TryParse(line);
                if (document is null)
                {
                    continue;
                }

                var root = document.RootElement;
                if (root.TryGetProperty("error", out var error))
                {
                    throw new CodexSyncBarException(
                        error.TryGetProperty("message", out var message)
                            ? message.GetString() ?? "Codex 인증 갱신 요청이 실패했습니다."
                            : "Codex 인증 갱신 요청이 실패했습니다.");
                }

                var responseId = root.TryGetProperty("id", out var id)
                    && id.ValueKind == JsonValueKind.Number
                    && id.TryGetInt32(out var parsedId)
                    ? parsedId
                    : 0;
                if (responseId == 1 && root.TryGetProperty("result", out _))
                {
                    await WriteMessageAsync(
                        process.StandardInput,
                        new JsonObject
                        {
                            ["method"] = "initialized",
                            ["params"] = new JsonObject(),
                        },
                        cancellationToken);
                    if (!requestedAccountRead)
                    {
                        requestedAccountRead = true;
                        await WriteMessageAsync(
                            process.StandardInput,
                            new JsonObject
                            {
                                ["id"] = 2,
                                ["method"] = "account/read",
                                ["params"] = new JsonObject { ["refreshToken"] = true },
                            },
                            cancellationToken);
                    }
                }

                if (responseId == 2 && root.TryGetProperty("result", out var accountResult))
                {
                    if (accountResult.TryGetProperty("account", out var account)
                        && account.TryGetProperty("type", out var type)
                        && type.GetString() != "chatgpt")
                    {
                        throw new CodexSyncBarException("Codex 인증 갱신 결과가 ChatGPT 계정이 아닙니다.");
                    }

                    if (!requestedRateLimits)
                    {
                        requestedRateLimits = true;
                        await WriteMessageAsync(
                            process.StandardInput,
                            new JsonObject
                            {
                                ["id"] = 3,
                                ["method"] = "account/rateLimits/read",
                            },
                            cancellationToken);
                    }
                }

                if (responseId == 3)
                {
                    if (!root.TryGetProperty("result", out var result)
                        || !result.TryGetProperty("rateLimits", out _))
                    {
                        throw new CodexSyncBarException("갱신된 인증으로 Codex 서버 연결을 확인하지 못했습니다.");
                    }

                    return "validated=true";
                }
            }
        }
        finally
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
            }
        }
    }

    private bool ShouldDeferForRunningClient(CodexAuthFile auth)
    {
        if (_localSwitchService is null || !_localSwitchService.HasCodexClientsRunning())
        {
            return false;
        }

        return string.Equals(
            _authStore.ReadActiveAccountId(),
            auth.Tokens.AccountId,
            StringComparison.Ordinal);
    }

    private async Task<CodexAuthFile> ReadRefreshedAuthAsync(
        string path,
        CodexAuthFile before,
        CancellationToken cancellationToken)
    {
        CodexAuthFile? lastValid = null;
        Exception? lastError = null;
        for (var attempt = 0; attempt < 12; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var candidate = _authStore.ReadAuthFile(path);
                lastValid = candidate;
                if (!string.Equals(
                        before.Tokens.AccessToken,
                        candidate.Tokens.AccessToken,
                        StringComparison.Ordinal)
                    || !string.Equals(
                        before.Tokens.RefreshToken,
                        candidate.Tokens.RefreshToken,
                        StringComparison.Ordinal))
                {
                    return candidate;
                }
            }
            catch (Exception error) when (
                error is CodexSyncBarException
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

        throw new CodexSyncBarException(
            lastError?.Message ?? "Codex 인증 갱신 결과 파일을 읽지 못했습니다.");
    }

    private static async Task<IReadOnlyList<string>> CaptureErrorsAsync(
        StreamReader reader,
        CancellationToken cancellationToken)
    {
        var lines = new List<string>();
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            lines.Add(line);
            if (lines.Count > 40)
            {
                lines.RemoveAt(0);
            }
        }

        return lines;
    }

    private static async Task WriteMessageAsync(
        StreamWriter writer,
        JsonObject message,
        CancellationToken cancellationToken)
    {
        await writer.WriteLineAsync(message.ToJsonString());
        await writer.FlushAsync(cancellationToken);
    }

    private static JsonDocument? TryParse(string line)
    {
        try
        {
            return JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string FindCodex()
    {
        return CodexCliLocator.Find()
            ?? throw new CodexSyncBarException(
                "공식 Codex CLI를 찾지 못했습니다. Windows에서는 codex.cmd가 PATH에 있어야 합니다.");
    }
}
