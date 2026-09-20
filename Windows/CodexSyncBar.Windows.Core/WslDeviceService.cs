using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodexSyncBar.Windows.Core;

public sealed class WslDeviceConfiguration
{
    public string Distribution { get; set; } = "";
    public string Username { get; set; } = "";
    public string HomeDirectory { get; set; } = "";
    public bool Enabled { get; set; }
    public bool IsInstalled { get; set; }
    public string Id => "wsl:" + Distribution;
}

public sealed record WslDistribution(string Name, bool IsRunning);

public sealed class WslConfigurationStore(WindowsPaths paths)
{
    public string FilePath => Path.Combine(paths.StateRoot, "wsl-devices.json");

    public IReadOnlyList<WslDeviceConfiguration> Load()
    {
        WindowsPathSafety.EnsureFile(FilePath, "WSL 설정");
        if (!File.Exists(FilePath)) return [];
        var devices = JsonSerializer.Deserialize<List<WslDeviceConfiguration>>(File.ReadAllText(FilePath))
            ?? throw new CodexSyncBarException("WSL 설정이 비어 있습니다.");
        Validate(devices);
        return devices;
    }

    public void Save(IEnumerable<WslDeviceConfiguration> devices)
    {
        var copy = devices.ToList();
        Validate(copy);
        paths.EnsureDirectories();
        WindowsPathSafety.EnsureFile(FilePath, "WSL 설정");
        var temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(copy, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, FilePath, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void Validate(IReadOnlyList<WslDeviceConfiguration> devices)
    {
        if (devices.Select(device => device.Distribution).Distinct(StringComparer.OrdinalIgnoreCase).Count() != devices.Count)
            throw new CodexSyncBarException("중복된 WSL 배포판 설정입니다.");
        foreach (var device in devices)
        {
            WslDeviceService.ValidateDistribution(device.Distribution);
            if (!string.IsNullOrEmpty(device.Username) && !Regex.IsMatch(device.Username, "^[a-zA-Z_][a-zA-Z0-9_.-]{0,63}[$]?$"))
                throw new CodexSyncBarException("WSL 사용자 이름이 올바르지 않습니다.");
            if (device.Enabled && (!device.IsInstalled || string.IsNullOrWhiteSpace(device.Username) || !device.HomeDirectory.StartsWith('/')))
                throw new CodexSyncBarException("WSL 설치와 검증이 끝난 후 활성화할 수 있습니다.");
        }
    }
}

public interface IWslCommandRunner
{
    Task<ProcessResult> RunAsync(IReadOnlyList<string> arguments, string? input, CancellationToken cancellationToken);
}

public sealed class WslCommandRunner : IWslCommandRunner
{
    public Task<ProcessResult> RunAsync(IReadOnlyList<string> arguments, string? input, CancellationToken cancellationToken) =>
        ProcessRunner.RunAsync("wsl.exe", arguments, input, cancellationToken,
            arguments.Any(argument => argument.Contains("__node update-codex", StringComparison.Ordinal)) ? TimeSpan.FromMinutes(10) : TimeSpan.FromSeconds(90),
            new Dictionary<string, string?> { ["WSL_UTF8"] = "1" });
}

public sealed class WslDeviceService(WindowsPaths paths, AuthStore authStore, IWslCommandRunner? runner = null, string? runtimeDirectory = null)
{
    private readonly AuthStore _authStore = authStore;
    private readonly IWslCommandRunner _runner = runner ?? new WslCommandRunner();
    public const string HelperVersion = "2.1.3";
    public bool HasPendingBootstrap => File.Exists(BootstrapJournalPath);
    private string BootstrapJournalPath => Path.Combine(paths.StateRoot, "wsl-bootstrap-pending.json");

    public async Task<IReadOnlyList<string>> RecoverPendingBootstrapTransactionsAsync(bool startStopped = false, CancellationToken cancellationToken = default)
    {
        WindowsPathSafety.EnsureFile(BootstrapJournalPath, "WSL 설치 복구 기록");
        if (!File.Exists(BootstrapJournalPath)) return [];
        var device = JsonSerializer.Deserialize<WslDeviceConfiguration>(await File.ReadAllTextAsync(BootstrapJournalPath, cancellationToken))
            ?? throw new CodexSyncBarException("WSL 설치 복구 기록을 읽지 못했습니다.");
        ValidateDistribution(device.Distribution);
        var distributions = await DiscoverAsync(cancellationToken);
        if (!startStopped && !distributions.Any(distribution => distribution.Name == device.Distribution && distribution.IsRunning))
            return [device.Distribution];
        try
        {
            EnsureSuccess(await ShellAsync(device, RecoverBootstrapScript, null, cancellationToken));
            File.Delete(BootstrapJournalPath);
            return [];
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested) { return [device.Distribution]; }
    }

    private const string RecoverBootstrapScript = """
        set -eu
        umask 077
        backup="$HOME/.local/share/.syncbar-wsl-bootstrap"
        state="$HOME/.local/share/gpt-switch"
        codex="$HOME/.codex"
        [ ! -L "$backup" ] && [ ! -L "$state" ] && [ ! -L "$codex" ] && [ ! -L "$codex/auth.json" ] || exit 65
        [ -d "$backup" ] || exit 0
        if [ -f "$backup/ready" ] && [ ! -f "$backup/committed" ]; then
          rm -rf "$state"
          if [ -f "$backup/state-existed" ]; then cp -a "$backup/state" "$state"; fi
          if [ -f "$backup/auth-existed" ]; then mkdir -p "$codex"; cp -p "$backup/auth.json" "$codex/auth.json"; else rm -f "$codex/auth.json"; fi
        fi
        rm -rf "$backup"
        """;

    public async Task<IReadOnlyList<WslDistribution>> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        var all = await _runner.RunAsync(["--list", "--quiet"], null, cancellationToken);
        if (all.ExitCode != 0) return [];
        var running = await _runner.RunAsync(["--list", "--running", "--quiet"], null, cancellationToken);
        var active = ParseDistributionList(running.StandardOutput).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return ParseDistributionList(all.StandardOutput).Select(name => new WslDistribution(name, active.Contains(name))).ToArray();
    }

    public static IReadOnlyList<string> ParseDistributionList(string output) => output.Replace("\0", "")
        .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(name => !name.StartsWith("docker-desktop", StringComparison.OrdinalIgnoreCase))
        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    public static void ValidateDistribution(string distribution)
    {
        if (string.IsNullOrWhiteSpace(distribution) || distribution.Length > 128 || distribution.Any(char.IsControl)
            || distribution.StartsWith('-') || distribution.StartsWith("docker-desktop", StringComparison.OrdinalIgnoreCase))
            throw new CodexSyncBarException("지원하지 않는 WSL 배포판 이름입니다.");
    }

    public async Task<IReadOnlyList<DeviceStatus>> FetchStatusesAsync(CancellationToken cancellationToken = default)
    {
        var states = await DiscoverAsync(cancellationToken);
        var statuses = new List<DeviceStatus>();
        foreach (var device in new WslConfigurationStore(paths).Load())
        {
            if (!device.Enabled || !states.Any(state => state.Name.Equals(device.Distribution, StringComparison.OrdinalIgnoreCase) && state.IsRunning))
            {
                statuses.Add(new(device.Id, device.Distribution, null, null, "wsl", device.Enabled ? "stopped" : "disabled", false,
                    device.Enabled ? "배포판이 정지되어 있습니다. 적용할 때 시작합니다." : "비활성화됨"));
                continue;
            }
            try
            {
                var result = await NodeAsync(device, ["status"], null, cancellationToken);
                EnsureSuccess(result);
                var fields = ParseFields(result.StandardOutput);
                statuses.Add(new(device.Id, device.Distribution,
                    int.TryParse(fields.GetValueOrDefault("active"), out var id) ? id : null,
                    fields.GetValueOrDefault("fingerprint"), "access-only", fields.GetValueOrDefault("cli"), true));
            }
            catch (Exception error) when (!cancellationToken.IsCancellationRequested)
            {
                statuses.Add(new(device.Id, device.Distribution, null, null, "wsl", "unreachable", false, error.Message));
            }
        }
        return statuses;
    }

    public async Task<WslDeviceConfiguration> BootstrapAsync(WslDeviceConfiguration device,
        IReadOnlyList<AccountProfile> accounts, int? activeProfileId = null, CancellationToken cancellationToken = default)
    {
        ValidateDistribution(device.Distribution);
        var pending = await RecoverPendingBootstrapTransactionsAsync(startStopped: true, cancellationToken);
        if (pending.Count != 0) throw new CodexSyncBarException("이전 WSL 설치 복구를 먼저 완료해 주세요.");
        var probe = await ShellAsync(device, "set -eu; command -v bash >/dev/null; command -v jq >/dev/null; command -v node >/dev/null; printf '%s\\n' \"$(id -un)\" \"$HOME\"", null, cancellationToken);
        EnsureSuccess(probe);
        var identity = probe.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        if (identity.Length != 2 || !identity[1].StartsWith('/')) throw new CodexSyncBarException("WSL 사용자와 홈을 확인하지 못했습니다.");
        device.Username = identity[0];
        device.HomeDirectory = identity[1];
        var profiles = accounts.Where(account => !account.IsPending && !account.NeedsLogin).Select(account => account.Id).ToArray();
        if (profiles.Length == 0) throw new CodexSyncBarException("먼저 Windows에서 계정을 로그인해 주세요.");
        paths.EnsureDirectories();
        WindowsPathSafety.EnsureFile(BootstrapJournalPath, "WSL 설치 복구 기록");
        var pendingPath = BootstrapJournalPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            WindowsPathSafety.WritePrivateBytes(pendingPath, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(device)));
            File.Move(pendingPath, BootstrapJournalPath, overwrite: true);
        }
        finally { if (File.Exists(pendingPath)) File.Delete(pendingPath); }
        await InstallFileAsync(device, runtimeDirectory is null ? paths.BundledGptSwitch : Path.Combine(runtimeDirectory, "gpt-switch"), ".local/bin/gpt-switch", "755", cancellationToken);
        await InstallFileAsync(device, runtimeDirectory is null ? paths.BundledUsageSummary : Path.Combine(runtimeDirectory, "usage-summary.mjs"), ".local/lib/gpt-switch/usage-summary.mjs", "755", cancellationToken);
        var version = await NodeAsync(device, ["version"], null, cancellationToken);
        EnsureSuccess(version);
        if (version.StandardOutput.Trim() != HelperVersion) throw new CodexSyncBarException("WSL helper 버전이 일치하지 않습니다.");
        // Bootstrap credentials are sent only through stdin, protected in the Linux home with umask 077.
        var selected = activeProfileId is { } active && profiles.Contains(active) ? active : profiles[0];
        var payload = JsonSerializer.Serialize(profiles.Select(id => new { id, auth = _authStore.CreateAccessOnlyCopy(id) }));
        var script = BootstrapScript.Replace("__SELECTED_PROFILE__", selected.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var result = await ShellAsync(device, script, payload, cancellationToken);
        EnsureSuccess(result);
        device.IsInstalled = true;
        device.Enabled = true;
        File.Delete(BootstrapJournalPath);
        return device;
    }

    private const string BootstrapScript = """
        set -eu
        umask 077
        state="$HOME/.local/share/gpt-switch"
        codex="$HOME/.codex"
        [ ! -L "$state" ] && [ ! -L "$codex" ] || exit 65
        mkdir -p "$HOME/.local/share" "$codex"
        backup="$HOME/.local/share/.syncbar-wsl-bootstrap"
        [ ! -L "$backup" ] || exit 65
        recover_bootstrap() {
          if [ -f "$backup/ready" ] && [ ! -f "$backup/committed" ]; then
            rm -rf "$state"
            if [ -f "$backup/state-existed" ]; then cp -a "$backup/state" "$state" || return 1; fi
            if [ -f "$backup/auth-existed" ]; then cp -p "$backup/auth.json" "$codex/auth.json" || return 1; else rm -f "$codex/auth.json"; fi
          fi
          rm -rf "$backup"
        }
        if [ -d "$backup" ]; then recover_bootstrap || exit 66; fi
        mkdir "$backup"
        chmod 700 "$backup"
        [ ! -e "$state" ] || { cp -a "$state" "$backup/state"; : > "$backup/state-existed"; }
        [ ! -L "$codex/auth.json" ] || exit 65
        [ ! -e "$codex/auth.json" ] || { cp -p "$codex/auth.json" "$backup/auth.json"; : > "$backup/auth-existed"; }
        : > "$backup/ready"
        trap recover_bootstrap EXIT
        cat > "$backup/profiles.json"
        jq -e 'type == "array" and length > 0 and all(.[]; .id > 0 and (.auth.tokens.refresh_token == null or .auth.tokens.refresh_token == ""))' "$backup/profiles.json" >/dev/null
        for id in $(jq -r '.[].id' "$backup/profiles.json"); do
          jq -c --argjson id "$id" '.[] | select(.id == $id) | .auth' "$backup/profiles.json" > "$backup/incoming.json"
          account=$(jq -r '.tokens.account_id' "$backup/incoming.json" | tr -d '\n' | sha256sum | cut -c1-12)
          access=$(jq -r '.tokens.access_token' "$backup/incoming.json" | tr -d '\n' | sha256sum | cut -c1-12)
          "$HOME/.local/bin/gpt-switch" __node install-access "$id" "$account" "$access" < "$backup/incoming.json"
        done
        "$HOME/.local/bin/gpt-switch" __node initialize __SELECTED_PROFILE__
        "$HOME/.local/bin/gpt-switch" __node verify __SELECTED_PROFILE__
        : > "$backup/committed"
        """;

    public async Task<DeviceTokenUsageSummary> FetchTokenUsageAsync(WslDeviceConfiguration device, CancellationToken cancellationToken = default)
    {
        var running = await DiscoverAsync(cancellationToken);
        if (!running.Any(state => state.Name.Equals(device.Distribution, StringComparison.OrdinalIgnoreCase) && state.IsRunning))
            throw new CodexSyncBarException("WSL 배포판이 정지되어 있습니다.");
        var result = await NodeAsync(device, ["usage-summary"], null, cancellationToken);
        EnsureSuccess(result);
        var summary = TokenUsageService.ParseSummary(result.StandardOutput);
        if (summary.SchemaVersion != 6) throw new CodexSyncBarException("WSL 사용량 helper를 다시 설치해 주세요.");
        return summary;
    }

    public IAccountTarget CreateAccountTarget(WslDeviceConfiguration device) => new WslAccountTarget(this, device);

    public async Task<ProcessResult> RunCodexHelperAsync(WslDeviceConfiguration device, bool update, CancellationToken cancellationToken = default)
    {
        if (!update && !(await DiscoverAsync(cancellationToken)).Any(item => item.Name.Equals(device.Distribution, StringComparison.OrdinalIgnoreCase) && item.IsRunning))
            return new ProcessResult(0, "{\"version\":\"\",\"path\":\"\",\"manager\":\"wsl\",\"canUpdate\":true,\"notice\":\"배포판이 정지되어 있습니다. 업데이트를 누르면 시작합니다.\"}", "");
        return await ShellAsync(device, "exec bash -l -s -- __node " + (update ? "update-codex" : "codex-info"),
            SshDeviceService.NormalizeRemoteInput(await File.ReadAllTextAsync(paths.BundledGptSwitch, cancellationToken)), cancellationToken);
    }

    public IAccountTarget CreateLogoutTarget(WslDeviceConfiguration device, int removedProfileId, int fallbackProfileId)
    {
        var fingerprint = Fingerprint(JsonSerializer.Serialize(new { device.Distribution, device.Username, device.HomeDirectory }));
        var target = new RemoteLogoutTarget(device.Id, device.Distribution, fingerprint, removedProfileId,
            (arguments, cancellationToken) => NodeAsync(device, arguments, null, cancellationToken));
        target.SetFallback(fallbackProfileId);
        return target;
    }

    public async Task SyncAuthAsync(WslDeviceConfiguration device, int profileId, CancellationToken cancellationToken = default)
    {
        var auth = _authStore.CreateAccessOnlyCopy(profileId);
        var result = await NodeAsync(device, ["install-access", profileId.ToString(), CredentialFingerprint(auth.Tokens.AccountId!), CredentialFingerprint(auth.Tokens.AccessToken!)],
            JsonSerializer.Serialize(auth), cancellationToken);
        EnsureSuccess(result);
    }

    private async Task InstallFileAsync(WslDeviceConfiguration device, string source, string relative, string mode, CancellationToken cancellationToken)
    {
        WindowsPathSafety.EnsureFile(source, "WSL helper 설치 파일");
        var script = "set -eu; umask 077; destination=\"$HOME/" + relative + "\"; "
            + "directory=$(dirname \"$destination\"); [ ! -L \"$directory\" ] && [ ! -L \"$destination\" ] || exit 65; "
            + "mkdir -p \"$directory\"; tmp=$(mktemp \"$directory/.syncbar-install.XXXXXX\"); trap 'rm -f \"$tmp\"' EXIT; "
            + "cat > \"$tmp\"; chmod " + mode + " \"$tmp\"; mv -f \"$tmp\" \"$destination\"";
        EnsureSuccess(await ShellAsync(device, script, await File.ReadAllTextAsync(source, cancellationToken), cancellationToken));
    }

    private Task<ProcessResult> NodeAsync(WslDeviceConfiguration device, string[] arguments, string? input, CancellationToken cancellationToken) =>
        ShellAsync(device, "exec \"$HOME/.local/bin/gpt-switch\" __node " + string.Join(' ', arguments.Select(ShellQuote)), input, cancellationToken);

    private Task<ProcessResult> ShellAsync(WslDeviceConfiguration device, string script, string? input, CancellationToken cancellationToken)
    {
        ValidateDistribution(device.Distribution);
        List<string> arguments = ["--distribution", device.Distribution];
        if (!string.IsNullOrWhiteSpace(device.Username)) arguments.AddRange(["--user", device.Username]);
        arguments.AddRange(["--exec", "bash", "-lc", script]);
        return _runner.RunAsync(arguments, input, cancellationToken);
    }

    internal static string ShellQuote(string value) => "'" + value.Replace("'", "'\\''") + "'";
    internal static string CredentialFingerprint(string value) => Fingerprint(value)[..12];
    private static string Fingerprint(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static Dictionary<string, string> ParseFields(string output) => output.Split([' ', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)
        .Select(part => part.Split('=', 2)).Where(part => part.Length == 2).GroupBy(part => part[0]).ToDictionary(group => group.Key, group => group.Last()[1]);
    private static void EnsureSuccess(ProcessResult result)
    {
        if (result.ExitCode != 0) throw new CodexSyncBarException("WSL 작업에 실패했습니다. 배포판의 bash, jq, node 및 helper 설치 상태를 확인해 주세요.");
    }

    private sealed class WslAccountTarget(WslDeviceService service, WslDeviceConfiguration device) : IAccountTarget
    {
        public string Id => device.Id;
        public string DisplayName => device.Distribution;
        public bool IsLocal => false;
        public string ConfigurationFingerprint => Fingerprint(JsonSerializer.Serialize(new { device.Distribution, device.Username, device.HomeDirectory }));

        public async Task<string> PrepareAsync(int profileId, CancellationToken cancellationToken)
        {
            _ = service._authStore.CreateAccessOnlyCopy(profileId);
            if (!device.Enabled || !device.IsInstalled) throw new CodexSyncBarException("WSL 장치를 설치하고 활성화해 주세요.");
            var version = await service.NodeAsync(device, ["version"], null, cancellationToken);
            EnsureSuccess(version);
            if (version.StandardOutput.Trim() != HelperVersion) throw new CodexSyncBarException("WSL helper를 다시 설치해 주세요.");
            var status = await service.NodeAsync(device, ["status"], null, cancellationToken);
            EnsureSuccess(status);
            if (!int.TryParse(ParseFields(status.StandardOutput).GetValueOrDefault("active"), out var previous) || previous <= 0)
                throw new CodexSyncBarException("WSL의 현재 계정을 확인하지 못했습니다.");
            EnsureSuccess(await service.NodeAsync(device, ["preflight", previous.ToString()], null, cancellationToken));
            return previous.ToString();
        }

        public async Task ApplyAsync(int profileId, CancellationToken cancellationToken)
        {
            await service.SyncAuthAsync(device, profileId, cancellationToken);
            EnsureSuccess(await service.NodeAsync(device, ["preflight", profileId.ToString()], null, cancellationToken));
            EnsureSuccess(await service.NodeAsync(device, ["switch", profileId.ToString(), "1"], null, cancellationToken));
        }

        public async Task VerifyAsync(int profileId, CancellationToken cancellationToken) =>
            EnsureSuccess(await service.NodeAsync(device, ["verify", profileId.ToString()], null, cancellationToken));

        public async Task RestoreAsync(string checkpoint, CancellationToken cancellationToken)
        {
            if (!int.TryParse(checkpoint, out var profile) || profile <= 0) throw new CodexSyncBarException("잘못된 WSL 복구 계정입니다.");
            EnsureSuccess(await service.NodeAsync(device, ["switch", profile.ToString(), "1"], null, cancellationToken));
            await VerifyAsync(profile, cancellationToken);
        }

        public Task ReleaseAsync(string checkpoint, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
