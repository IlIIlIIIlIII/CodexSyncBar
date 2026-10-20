namespace ClaudeSyncBar.Windows.Core;

public sealed class ClaudeLoginService
{
    // The CLI runs this in place of the system browser, so the sign-in page
    // can be opened in the account's own Chrome profile instead.
    private const string ShellOpenerScript = """
        #!/bin/sh
        umask 077
        printf '%s\n' "$1" > "$CLAUDE_SYNCBAR_LOGIN_URL_FILE.tmp" && mv "$CLAUDE_SYNCBAR_LOGIN_URL_FILE.tmp" "$CLAUDE_SYNCBAR_LOGIN_URL_FILE"

        """;
#if SYNCBAR_WINDOWS
    // On Windows the CLI starts BROWSER through cmd and passes the address in
    // quotes. %~1 drops them; delayed expansion writes '&' and '%' literally.
    internal const string OpenerName = "open-login-url.cmd";
    internal const string OpenerScript =
        "@echo off\r\n"
        + "setlocal DisableDelayedExpansion\r\n"
        + "set \"url=%~1\"\r\n"
        + "setlocal EnableDelayedExpansion\r\n"
        + ">\"!CLAUDE_SYNCBAR_LOGIN_URL_FILE!.tmp\" echo(!url!\r\n"
        + "move /y \"!CLAUDE_SYNCBAR_LOGIN_URL_FILE!.tmp\" \"!CLAUDE_SYNCBAR_LOGIN_URL_FILE!\" >nul\r\n";
#else
    internal const string OpenerName = "open-login-url";
    internal const string OpenerScript = ShellOpenerScript;
#endif

    private readonly WindowsPaths _paths;
    private readonly AuthStore _authStore;
    private readonly BrowserLoginService _browserLoginService;
    private readonly LoginTransactionStore _loginTransactions;
    internal string? ExecutableOverride { get; init; }
    internal Action<int, Uri>? OpenUrlOverride { get; init; }

    public ClaudeLoginService(
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
        var claude = ExecutableOverride ?? ClaudeCliLocator.Find()
            ?? throw new ClaudeSyncBarException("공식 Claude Code CLI를 찾지 못했습니다. CLI 관리에서 설치해 주세요.");
        _paths.EnsureDirectories();
        var loginHome = Path.Combine(
            _paths.LoginSessionsDirectory,
            $"profile-{profileId}-{Guid.NewGuid():N}");
        WindowsPathSafety.EnsurePrivateDirectory(loginHome, "Claude 로그인 세션 디렉터리");
        if (!Directory.Exists(loginHome))
        {
            throw new ClaudeSyncBarException(
                $"Claude 로그인 디렉터리를 만들지 못했습니다: {loginHome}");
        }

        // Keep the selected executable visible in the UI: a machine can have
        // a standalone CLI and the copy bundled with Claude Desktop.
        progress?.Report($"Claude Code CLI 확인: {claude}");

        var urlFile = Path.Combine(loginHome, "login-url");
        CommandProcess process;
        try
        {
            var opener = Path.Combine(loginHome, OpenerName);
            WindowsPathSafety.WritePrivateBytes(opener, System.Text.Encoding.UTF8.GetBytes(OpenerScript));
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(opener, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var environment = ClaudeCliEnvironment.Isolated(loginHome);
            environment["BROWSER"] = opener;
            environment["CLAUDE_SYNCBAR_LOGIN_URL_FILE"] = urlFile;
            process = ProcessRunner.StartInteractive(
                claude,
                ["auth", "login", "--claudeai"],
                redirectStandardInput: true,
                workingDirectory: _paths.Home,
                environment: environment);
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

        try
        {
            var outputLines = new List<string>();
            var errorLines = new List<string>();
            var output = CaptureLinesAsync(process.StandardOutput, outputLines, cancellationToken);
            var errors = CaptureLinesAsync(process.StandardError, errorLines, cancellationToken);
            progress?.Report("Claude 로그인 주소를 준비하고 있습니다…");

            var opened = false;
            var exit = process.WaitForExitAsync(cancellationToken);
            while (!exit.IsCompleted)
            {
                if (!opened && File.Exists(urlFile))
                {
                    OpenLoginUrl(profileId, urlFile);
                    opened = true;
                    progress?.Report("계정 전용 Chrome 창에서 로그인하세요…");
                }

                await Task.WhenAny(exit, Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken));
                cancellationToken.ThrowIfCancellationRequested();
            }

            await exit;
            await Task.WhenAll(output, errors);
            if (process.ExitCode != 0)
            {
                // The CLI explains a failure on standard error; its last progress line is only a fallback.
                var detail = errorLines.LastOrDefault(line => !string.IsNullOrWhiteSpace(line))
                    ?? outputLines.LastOrDefault(line => !string.IsNullOrWhiteSpace(line));
                throw new ClaudeSyncBarException(detail ?? "로그인을 완료하지 못했습니다.");
            }

            progress?.Report("로그인이 완료되었습니다. Claude 인증을 확인하는 중…");
            _authStore.CaptureClaudeHome(loginHome);
            await ImportWithRetryAsync(Path.Combine(loginHome, "auth.json"), profileId, replaceExisting, cancellationToken);
            progress?.Report("로그인이 완료되었습니다.");
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

    private void OpenLoginUrl(int profileId, string urlFile)
    {
        var text = System.Text.Encoding.UTF8.GetString(
            WindowsPathSafety.ReadPrivateFile(urlFile, "Claude 로그인 주소", 64 * 1024)).Trim();
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri))
        {
            throw new ClaudeSyncBarException("Claude Code가 올바른 로그인 주소를 반환하지 않았습니다.");
        }

        (OpenUrlOverride ?? _browserLoginService.OpenUrl)(profileId, uri);
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

        throw lastError ?? new ClaudeSyncBarException("로그인 인증 정보를 저장하지 못했습니다.");
    }

    private static async Task CaptureLinesAsync(
        StreamReader reader,
        List<string> lines,
        CancellationToken cancellationToken)
    {
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            lock (lines)
            {
                lines.Add(line);
                if (lines.Count > 40)
                {
                    lines.RemoveAt(0);
                }
            }
        }
    }
}
