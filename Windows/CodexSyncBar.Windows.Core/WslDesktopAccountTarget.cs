using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodexSyncBar.Windows.Core;

internal sealed record WslDesktopProfile(string Root, string Distribution, string Username, string CodexHome)
{
    public string Launcher => Path.Combine(Root, "Start-Codex-WSL.ps1");
    public string UserData => Path.Combine(Root, "user-data");
    public string WindowsAuth => Path.Combine(Root, "codex-home", "auth.json");

    // This is the separately installed desktop launcher, not the ordinary WSL CLI device.
    internal static WslDesktopProfile? Discover(WindowsPaths paths)
    {
        var root = Path.Combine(paths.Home, @"AppData\Local\Packages\OpenAI.Codex_2p2nqsd0c76g0\LocalCache\Local\Codex-WSL");
        var launcher = Path.Combine(root, "Start-Codex-WSL.ps1");
        if (!File.Exists(launcher)) return null;
        WindowsPathSafety.EnsureFile(launcher, "WSL Codex 실행 스크립트");
        var script = File.ReadAllText(launcher);
        var match = Regex.Match(script,
            """(?m)^\s*\$env:CODEX_CLI_PATH\s*=\s*'\\\\wsl\.localhost\\(?<distro>[^\\'\r\n]+)\\home\\(?<user>[a-zA-Z_][a-zA-Z0-9_.-]*)\\(?<home>\.[a-zA-Z0-9_.-]+)\\runtime\\codex-wsl-app'\s*$""");
        if (!match.Success) throw new CodexSyncBarException("WSL Codex 실행 스크립트의 인증 경로를 확인하지 못했습니다.");
        var distro = match.Groups["distro"].Value;
        WslDeviceService.ValidateDistribution(distro);
        var username = match.Groups["user"].Value;
        return new(root, distro, username, "/home/" + username + "/" + match.Groups["home"].Value);
    }
}

internal sealed class WslDesktopAccountTarget(WindowsPaths paths, AuthStore auth, WslDesktopProfile profile,
    IWslCommandRunner? runner = null, Func<bool, ICodexDesktopSession>? capture = null) : IAccountTarget
{
    private readonly WindowsSecretStore _secrets = new(paths);
    private readonly IWslCommandRunner _runner = runner ?? new WslCommandRunner();
    private ICodexDesktopSession? _desktop;
    private sealed record Backup(string? LinuxAuth, string? WindowsAuth, bool WasRunning);
    public string Id => "wsl-desktop:" + profile.Distribution;
    public string DisplayName => "Codex (WSL)";
    public string ConfigurationFingerprint => JsonSerializer.Serialize(profile);
    public bool IsLocal => true;

    internal static IEnumerable<IAccountTarget> Discover(WindowsPaths paths, AuthStore auth)
    {
        var profile = WslDesktopProfile.Discover(paths);
        if (profile is not null) yield return new WslDesktopAccountTarget(paths, auth, profile);
    }

    private ICodexDesktopSession Capture(bool forceStart = false) => capture?.Invoke(forceStart)
        ?? CodexDesktopLifecycle.Capture(paths, profile.UserData, profile.Launcher, forceStart);

    public async Task<string> PrepareAsync(int profileId, CancellationToken cancellationToken)
    {
        _ = auth.ReadCredentials(profileId);
        var linux = await RunAsync("read", null, cancellationToken);
        WindowsPathSafety.EnsureFile(profile.WindowsAuth, "WSL 앱 인증");
        var windows = File.Exists(profile.WindowsAuth) ? File.ReadAllText(profile.WindowsAuth) : null;
        _desktop = Capture();
        var key = "wsl-desktop-backup-" + Guid.NewGuid().ToString("N");
        try
        {
            _secrets.Save(JsonSerializer.Serialize(new Backup(JsonSerializer.Deserialize<string?>(linux), windows, _desktop.WasRunning)), key);
            return key;
        }
        catch { _desktop.Dispose(); _desktop = null; throw; }
    }

    public async Task ApplyAsync(int profileId, CancellationToken cancellationToken)
    {
        if (_desktop is null) throw new CodexSyncBarException("WSL 앱 사전 점검이 필요합니다.");
        var json = JsonSerializer.Serialize(auth.CreateAccessOnlyCopy(profileId));
        await ChangeAsync(json, json, _desktop, cancellationToken);
    }

    private async Task ChangeAsync(string? linux, string? windows, ICodexDesktopSession desktop, CancellationToken cancellationToken)
    {
        await desktop.StopAsync(cancellationToken);
        // The Linux app-server can outlive wsl.exe and the Windows Electron process.
        await RunAsync("stop", null, cancellationToken);
        await RunAsync("write", linux, cancellationToken);
        WriteWindows(windows);
        await desktop.StartAsync(cancellationToken);
    }

    public async Task VerifyAsync(int profileId, CancellationToken cancellationToken)
    {
        var expected = auth.ReadCredentials(profileId).AccountId;
        var linux = JsonSerializer.Deserialize<string?>(await RunAsync("read", null, cancellationToken));
        if (AccountId(linux) != expected || AccountId(File.ReadAllText(profile.WindowsAuth)) != expected)
            throw new CodexSyncBarException("WSL Codex 계정 적용 결과가 일치하지 않습니다.");
    }

    private static string? AccountId(string? json) => json is null ? null : JsonSerializer.Deserialize<CodexAuthFile>(json)?.Tokens.AccountId;

    internal async Task SyncActiveAuthAsync(int profileId, CancellationToken cancellationToken)
    {
        var copy = auth.CreateAccessOnlyCopy(profileId);
        var linux = JsonSerializer.Deserialize<string?>(await RunAsync("read", null, cancellationToken));
        // Follow the ordinary WSL helper's access-only policy: Windows owns refresh tokens.
        // Background maintenance must not switch a separately selected account.
        if (AccountId(linux) != copy.Tokens.AccountId) return;
        var json = JsonSerializer.Serialize(copy);
        await RunAsync("write", json, cancellationToken);
        WindowsPathSafety.EnsureFile(profile.WindowsAuth, "WSL 앱 인증");
        if (File.Exists(profile.WindowsAuth) && AccountId(File.ReadAllText(profile.WindowsAuth)) == copy.Tokens.AccountId)
            WriteWindows(json);
    }

    public async Task RestoreAsync(string checkpoint, CancellationToken cancellationToken)
    {
        var backup = JsonSerializer.Deserialize<Backup>(_secrets.Read(checkpoint)
            ?? throw new CodexSyncBarException("WSL 앱 복구 인증을 찾지 못했습니다."))!;
        _desktop ??= Capture(backup.WasRunning);
        await ChangeAsync(backup.LinuxAuth, backup.WindowsAuth, _desktop, cancellationToken);
        var linux = JsonSerializer.Deserialize<string?>(await RunAsync("read", null, cancellationToken));
        if (AccountId(linux) != AccountId(backup.LinuxAuth)) throw new CodexSyncBarException("WSL 앱 인증 복구를 확인하지 못했습니다.");
    }

    public Task ReleaseAsync(string checkpoint, CancellationToken cancellationToken)
    {
        _desktop?.Dispose();
        _desktop = null;
        _secrets.Delete(checkpoint);
        return Task.CompletedTask;
    }

    private void WriteWindows(string? json)
    {
        WindowsPathSafety.EnsureFile(profile.WindowsAuth, "WSL 앱 인증");
        if (json is null) { File.Delete(profile.WindowsAuth); return; }
        var temporary = profile.WindowsAuth + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            WindowsPathSafety.WritePrivateBytes(temporary, Encoding.UTF8.GetBytes(json));
            File.Move(temporary, profile.WindowsAuth, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private async Task<string> RunAsync(string operation, string? contents, CancellationToken cancellationToken)
    {
        var result = await _runner.RunAsync(["--distribution", profile.Distribution, "--user", profile.Username,
            "--exec", "python3", "-c", Script, profile.CodexHome, operation],
            JsonSerializer.Serialize(contents), cancellationToken);
        // Never include helper output in exceptions: reads contain credentials.
        if (result.ExitCode != 0) throw new CodexSyncBarException("WSL Codex 인증 또는 실행 상태를 처리하지 못했습니다.");
        return result.StandardOutput.Trim();
    }

    internal const string Script = """
        import os, sys, json, pathlib, tempfile, signal, time, select
        home = pathlib.Path(sys.argv[1])
        operation = sys.argv[2]
        if not home.is_absolute() or home.resolve() != home or not home.is_dir():
            sys.exit(65)
        auth = home / 'auth.json'
        if auth.is_symlink(): sys.exit(65)
        if operation == 'read':
            print(json.dumps(auth.read_text() if auth.exists() else None))
        elif operation == 'write':
            contents = json.load(sys.stdin)
            if contents is None:
                auth.unlink(missing_ok=True)
            else:
                json.loads(contents)
                fd, temporary = tempfile.mkstemp(prefix='.syncbar-auth-', dir=home)
                try:
                    with os.fdopen(fd, 'w') as stream:
                        stream.write(contents)
                        stream.flush()
                        os.fsync(stream.fileno())
                    os.replace(temporary, auth)
                finally:
                    if os.path.exists(temporary): os.unlink(temporary)
        elif operation == 'stop':
            handles = []
            try:
                for entry in pathlib.Path('/proc').iterdir():
                    if not entry.name.isdigit(): continue
                    handle = None
                    try:
                        if entry.stat().st_uid != os.getuid(): continue
                        handle = os.pidfd_open(int(entry.name))
                        args = (entry / 'cmdline').read_bytes().split(b'\0')
                        env = (entry / 'environ').read_bytes().split(b'\0')
                        if (args and pathlib.Path(os.fsdecode(args[0])).name == 'codex'
                            and b'app-server' in args and os.fsencode('CODEX_HOME=' + str(home)) in env):
                            signal.pidfd_send_signal(handle, signal.SIGTERM)
                            handles.append(handle)
                            handle = None
                    except (FileNotFoundError, ProcessLookupError, PermissionError): pass
                    finally:
                        if handle is not None: os.close(handle)
                deadline = time.monotonic() + 10
                for handle in handles:
                    if not select.select([handle], [], [], max(0, deadline-time.monotonic()))[0]:
                        signal.pidfd_send_signal(handle, signal.SIGKILL)
                        if not select.select([handle], [], [], 5)[0]: sys.exit(70)
            finally:
                for handle in handles: os.close(handle)
        else: sys.exit(64)
        """;
}
