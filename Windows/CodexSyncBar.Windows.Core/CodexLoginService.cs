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
                "공식 Codex CLI를 찾지 못했습니다. Windows에서는 codex.cmd가 PATH에 있어야 합니다.");
        _paths.EnsureDirectories();
        var loginHome = Path.Combine(
            _paths.LoginSessionsDirectory,
            $"profile-{profileId}-{Guid.NewGuid():N}");
        WindowsPathSafety.EnsureDirectory(loginHome, "Codex 로그인 세션 디렉터리");
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
                        ["name"] = "codex-syncbar-windows",
                        ["title"] = "Codex SyncBar for Windows",
                        ["version"] = "1.0.0",
                    },
                    ["capabilities"] = new JsonObject(),
                },
            }, cancellationToken);

            var loginCompleted = false;
            var accountUpdated = false;
            var accountReadRequested = false;
            var rateLimitsRequested = false;
            var loginId = string.Empty;

            while (true)
            {
                var line = await process.StandardOutput.ReadLineAsync(cancellationToken);
                if (line is null)
                {
                    break;
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
                            ? message.GetString() ?? "Codex 로그인 서버 요청이 실패했습니다."
                            : "Codex 로그인 서버 요청이 실패했습니다.");
                }

                var responseId = root.TryGetProperty("id", out var id)
                    && id.ValueKind == JsonValueKind.Number
                    && id.TryGetInt32(out var parsedId)
                    ? parsedId
                    : 0;

                if (responseId == 1 && root.TryGetProperty("result", out _))
                {
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

                if (responseId == 2)
                {
                    var result = root.GetProperty("result");
                    var type = result.TryGetProperty("type", out var typeValue)
                        ? typeValue.GetString()
                        : null;
                    var authUrl = result.TryGetProperty("authUrl", out var authUrlValue)
                        ? authUrlValue.GetString()
                        : null;
                    loginId = result.TryGetProperty("loginId", out var loginIdValue)
                        ? loginIdValue.GetString() ?? string.Empty
                        : string.Empty;
                    if (type != "chatgpt" || string.IsNullOrWhiteSpace(authUrl)
                        || string.IsNullOrWhiteSpace(loginId)
                        || !Uri.TryCreate(authUrl, UriKind.Absolute, out var uri))
                    {
                        throw new CodexSyncBarException("Codex가 올바른 로그인 주소를 반환하지 않았습니다.");
                    }

                    _browserLoginService.OpenUrl(profileId, uri);
                    progress?.Report("Windows 기본 브라우저에서 로그인하세요…");
                    continue;
                }

                if (root.TryGetProperty("method", out var methodValue))
                {
                    var method = methodValue.GetString();
                    if (method == "account/updated"
                        && root.TryGetProperty("params", out var accountParams)
                        && accountParams.TryGetProperty("authMode", out var authMode)
                        && authMode.GetString() == "chatgpt")
                    {
                        accountUpdated = true;
                    }
                    else if (method == "account/login/completed"
                        && root.TryGetProperty("params", out var completion)
                        && completion.TryGetProperty("success", out var success)
                        && success.GetBoolean())
                    {
                        if (completion.TryGetProperty("loginId", out var completedId)
                            && !string.IsNullOrWhiteSpace(completedId.GetString())
                            && completedId.GetString() != loginId)
                        {
                            continue;
                        }
                        loginCompleted = true;
                        progress?.Report("로그인이 완료되었습니다. Codex 인증을 확인하는 중…");
                    }
                    else if (method == "account/login/completed")
                    {
                        var failure = root.GetProperty("params").TryGetProperty("error", out var failureValue)
                            ? failureValue.GetString()
                            : null;
                        throw new CodexSyncBarException(failure ?? "로그인을 완료하지 못했습니다.");
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

                if (responseId == 3)
                {
                    if (!root.TryGetProperty("result", out var result)
                        || !result.TryGetProperty("account", out var account)
                        || account.TryGetProperty("type", out var accountType)
                            && accountType.GetString() != "chatgpt")
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

                if (responseId == 4)
                {
                    if (!root.TryGetProperty("result", out var result)
                        || !result.TryGetProperty("rateLimits", out _))
                    {
                        throw new CodexSyncBarException("새 인증으로 Codex 서버 연결을 확인하지 못했습니다.");
                    }

                    var source = Path.Combine(loginHome, "auth.json");
                    await ImportWithRetryAsync(source, profileId, replaceExisting, cancellationToken);
                    _browserLoginService.CloseLoginWindow(profileId);
                    progress?.Report("로그인이 완료되었습니다.");
                    return;
                }
            }

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
        }
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
                _loginTransactions.ImportAuth(_authStore, source, profileId, replaceExisting);
                return;
            }
            catch (Exception error) when (error is AuthenticationRequiredException or CodexSyncBarException)
            {
                lastError = error;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
        }

        throw lastError ?? new CodexSyncBarException("로그인 인증 정보를 저장하지 못했습니다.");
    }

    private static async Task WriteMessageAsync(
        StreamWriter writer,
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
