using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodexSyncBar.Windows.Core;

public sealed class CodexLoginService
{
    private readonly WindowsPaths _paths;
    private readonly AuthStore _authStore;
    private readonly BrowserLoginService _browserLoginService;
    private readonly LoginTransactionStore _loginTransactions;

    public CodexLoginService(
        WindowsPaths paths,
        AuthStore authStore,
        BrowserLoginService browserLoginService,
        LoginTransactionStore? loginTransactions = null)
    {
        _paths = paths;
        _authStore = authStore;
        _browserLoginService = browserLoginService;
        _loginTransactions = loginTransactions ?? new LoginTransactionStore(paths);
    }

    public async Task LoginAsync(
        int profileId,
        bool replaceExisting,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var codex = CodexCliLocator.Find()
            ?? throw new CodexSyncBarException(
                OperatingSystem.IsLinux() ? "공식 Codex CLI를 찾지 못했습니다. CLI 관리에서 설치해 주세요."
                    : "공식 Codex CLI를 찾지 못했습니다. Scripts/Windows/setup-cli.ps1로 설치해 주세요.");
        _paths.EnsureDirectories();
        var loginHome = Path.Combine(
            _paths.LoginSessionsDirectory,
            $"profile-{profileId}-{Guid.NewGuid():N}");
        WindowsPathSafety.EnsurePrivateDirectory(loginHome, "Codex 로그인 세션 디렉터리");
        if (!Directory.Exists(loginHome))
        {
            throw new CodexSyncBarException(
                $"Codex 로그인 디렉터리를 만들지 못했습니다: {loginHome}");
        }

        // Keep the selected executable visible in the UI. This is especially
        // useful on Windows where the desktop package also exposes a separate
        // codex.exe alongside the official npm codex.cmd shim.
        progress?.Report($"Codex CLI 확인: {codex}");

        CommandProcess process;
        try
        {
            process = ProcessRunner.StartInteractive(
                codex,
                [
                    "app-server",
                    "--stdio",
                    "-c",
                    "cli_auth_credentials_store=\"file\"",
                ],
                redirectStandardInput: true,
                workingDirectory: _paths.Home,
                environment: new Dictionary<string, string?>
                {
                    ["CODEX_HOME"] = loginHome,
                    ["NO_COLOR"] = "1",
                });
        }
        catch
        {
            try
            {
                if (Directory.Exists(loginHome))
                {
                    Directory.Delete(loginHome, recursive: true);
                }
            }
            catch
            {
            }

            throw;
        }

        var errorLines = new List<string>();
        try
        {
            var errorTask = CaptureErrorsAsync(process.StandardError, errorLines, cancellationToken);
            var writer = process.StandardInput;
            await WriteMessageAsync(writer, new JsonObject
            {
                ["id"] = 1,
                ["method"] = "initialize",
                ["params"] = new JsonObject
                {
                    ["clientInfo"] = new JsonObject
                    {
                        ["name"] = OperatingSystem.IsLinux() ? "codex-syncbar-ubuntu" : "codex-syncbar-windows",
                        ["title"] = OperatingSystem.IsLinux() ? "Codex SyncBar for Ubuntu" : "Codex SyncBar for Windows",
                        ["version"] = "1.0.0",
                    },
                    ["capabilities"] = new JsonObject(),
                },
            }, cancellationToken);

            if (await RunSessionAsync(process.StandardOutput, writer, loginHome, profileId, replaceExisting, progress, cancellationToken))
                return;

            await errorTask;
            var detail = errorLines.LastOrDefault(line => !string.IsNullOrWhiteSpace(line));
            throw new CodexSyncBarException(detail ?? "로그인이 완료되기 전에 Codex가 종료되었습니다.");
        }
        catch (CodexSyncBarException error) when (
            error.Message.Contains("CODEX_HOME", StringComparison.OrdinalIgnoreCase))
        {
            var directoryState = Directory.Exists(loginHome) ? "존재함" : "없음";
            throw new CodexSyncBarException(
                $"{error.Message}\n선택된 Codex CLI: {codex}\n앱이 만든 로그인 디렉터리: {directoryState}",
                error);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        finally
        {
            _browserLoginService.CloseLoginWindow(profileId);
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
#if SYNCBAR_LINUX
                await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
#endif
            }
            catch
            {
            }

            process.Dispose();

            try
            {
                if (Directory.Exists(loginHome))
                {
                    Directory.Delete(loginHome, recursive: true);
                }
            }
            catch
            {
                // The temporary login profile is harmless if Chrome still has
                // a file handle; a later login can use a new directory.
            }
#if SYNCBAR_LINUX
            _authStore.ProtectRecoveryAuthFile(Path.Combine(loginHome, "auth.json"));
#endif
        }
    }

    internal async Task<bool> RunSessionAsync(
        TextReader output, TextWriter writer, string loginHome, int profileId, bool replaceExisting,
        IProgress<string>? progress = null, CancellationToken cancellationToken = default,
        Action<int, Uri>? openUrl = null)
    {
        var loginStartRequested = false;
        var loginCompleted = false;
        var accountUpdated = false;
        var accountReadRequested = false;
        var rateLimitsRequested = false;
        var loginId = string.Empty;

        while (true)
        {
            var line = await output.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                break;
            }

            cancellationToken.ThrowIfCancellationRequested();
            using var document = TryParse(line);
            if (document is null || document.RootElement.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var root = document.RootElement;
            var responseId = root.TryGetProperty("id", out var id)
                && id.ValueKind == JsonValueKind.Number
                && id.TryGetInt32(out var parsedId)
                ? parsedId
                : 0;

            var responseExpected = responseId == 1 && !loginStartRequested
                || responseId == 2 && loginStartRequested && string.IsNullOrEmpty(loginId)
                || responseId == 3 && accountReadRequested && !rateLimitsRequested
                || responseId == 4 && rateLimitsRequested;
            if (root.TryGetProperty("error", out _) && responseExpected)
                throw new CodexSyncBarException("Codex 로그인 서버 요청이 실패했습니다.");

            if (responseId == 1 && !loginStartRequested && root.TryGetProperty("result", out _))
            {
                loginStartRequested = true;
                progress?.Report("Codex 로그인 주소를 준비하고 있습니다…");
                await WriteMessageAsync(writer, new JsonObject
                {
                    ["method"] = "initialized",
                    ["params"] = new JsonObject(),
                }, cancellationToken);
                await WriteMessageAsync(writer, new JsonObject
                {
                    ["id"] = 2,
                    ["method"] = "account/login/start",
                    ["params"] = new JsonObject { ["type"] = "chatgpt" },
                }, cancellationToken);
                continue;
            }

            if (responseId == 2 && loginStartRequested && string.IsNullOrEmpty(loginId))
            {
                if (!root.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object)
                    throw new CodexSyncBarException("Codex가 올바른 로그인 결과를 반환하지 않았습니다.");
                var type = result.TryGetProperty("type", out var typeValue)
                    && typeValue.ValueKind == JsonValueKind.String ? typeValue.GetString()
                    : null;
                var authUrl = result.TryGetProperty("authUrl", out var authUrlValue)
                    && authUrlValue.ValueKind == JsonValueKind.String ? authUrlValue.GetString()
                    : null;
                loginId = result.TryGetProperty("loginId", out var loginIdValue)
                    && loginIdValue.ValueKind == JsonValueKind.String ? loginIdValue.GetString() ?? string.Empty
                    : string.Empty;
                if (type != "chatgpt" || string.IsNullOrWhiteSpace(authUrl)
                    || string.IsNullOrWhiteSpace(loginId)
                    || !Uri.TryCreate(authUrl, UriKind.Absolute, out var uri))
                {
                    throw new CodexSyncBarException("Codex가 올바른 로그인 주소를 반환하지 않았습니다.");
                }

                (openUrl ?? _browserLoginService.OpenUrl)(profileId, uri);
                progress?.Report("계정 전용 Chrome 창에서 로그인하세요…");
                continue;
            }

            if (root.TryGetProperty("method", out var methodValue) && methodValue.ValueKind == JsonValueKind.String)
            {
                var method = methodValue.GetString();
                if (method == "account/updated" && !string.IsNullOrEmpty(loginId)
                    && root.TryGetProperty("params", out var accountParams) && accountParams.ValueKind == JsonValueKind.Object
                    && accountParams.TryGetProperty("authMode", out var authMode)
                    && authMode.ValueKind == JsonValueKind.String && authMode.GetString() == "chatgpt")
                {
                    accountUpdated = true;
                }
                else if (method == "account/login/completed" && !loginCompleted)
                {
                    // Both success and failure notifications must belong to this exact login.
                    // Missing/malformed IDs and notifications from an older attempt are not terminal events.
                    if (string.IsNullOrEmpty(loginId)
                        || !root.TryGetProperty("params", out var completion) || completion.ValueKind != JsonValueKind.Object
                        || !completion.TryGetProperty("loginId", out var completedId) || completedId.ValueKind != JsonValueKind.String
                        || !string.Equals(completedId.GetString(), loginId, StringComparison.Ordinal))
                        continue;
                    if (!completion.TryGetProperty("success", out var success) || success.ValueKind != JsonValueKind.True)
                        throw new CodexSyncBarException("로그인을 완료하지 못했습니다.");
                    loginCompleted = true;
                    progress?.Report("로그인이 완료되었습니다. Codex 인증을 확인하는 중…");
                }
            }

            if (loginCompleted && accountUpdated && !accountReadRequested)
            {
                accountReadRequested = true;
                await WriteMessageAsync(writer, new JsonObject
                {
                    ["id"] = 3,
                    ["method"] = "account/read",
                    ["params"] = new JsonObject { ["refreshToken"] = false },
                }, cancellationToken);
            }

            if (responseId == 3 && accountReadRequested && !rateLimitsRequested)
            {
                if (!root.TryGetProperty("result", out var result)
                    || result.ValueKind != JsonValueKind.Object
                    || !result.TryGetProperty("account", out var account) || account.ValueKind != JsonValueKind.Object
                    || !account.TryGetProperty("type", out var accountType) || accountType.ValueKind != JsonValueKind.String
                    || accountType.GetString() != "chatgpt")
                {
                    throw new CodexSyncBarException("새 Codex 계정 상태를 확인하지 못했습니다.");
                }

                progress?.Report("새 인증으로 Codex 서버 연결을 확인하는 중…");
                if (!rateLimitsRequested)
                {
                    rateLimitsRequested = true;
                    await WriteMessageAsync(writer, new JsonObject
                    {
                        ["id"] = 4,
                        ["method"] = "account/rateLimits/read",
                    }, cancellationToken);
                }
            }

            if (responseId == 4 && rateLimitsRequested)
            {
                if (!root.TryGetProperty("result", out var result)
                    || result.ValueKind != JsonValueKind.Object
                    || !result.TryGetProperty("rateLimits", out var limits) || limits.ValueKind != JsonValueKind.Object)
                {
                    throw new CodexSyncBarException("새 인증으로 Codex 서버 연결을 확인하지 못했습니다.");
                }

                var source = Path.Combine(loginHome, "auth.json");
                await ImportWithRetryAsync(source, profileId, replaceExisting, cancellationToken);
                progress?.Report("로그인이 완료되었습니다.");
                return true;
            }
        }

        return false;
    }

    private async Task ImportWithRetryAsync(
        string source,
        int profileId,
        bool replaceExisting,
        CancellationToken cancellationToken)
    {
        Exception? lastError = null;
        for (var attempt = 0; attempt < 60; attempt++)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                _loginTransactions.ImportAuth(_authStore, source, profileId, replaceExisting);
                return;
            }
            catch (Exception error) when (error is AuthenticationRequiredException or IOException)
            {
                lastError = error;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
        }

        throw lastError ?? new CodexSyncBarException("로그인 인증 정보를 저장하지 못했습니다.");
    }

    private static async Task WriteMessageAsync(
        TextWriter writer,
        JsonObject message,
        CancellationToken cancellationToken)
    {
        await writer.WriteLineAsync(message.ToJsonString());
        await writer.FlushAsync(cancellationToken);
    }

    private static async Task CaptureErrorsAsync(
        StreamReader reader,
        ICollection<string> lines,
        CancellationToken cancellationToken)
    {
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            lines.Add(line);
            while (lines.Count > 40)
            {
                lines.Remove(lines.First());
            }
        }
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

}
