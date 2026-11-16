using System.Text.RegularExpressions;

namespace ClaudeSyncBar.Windows.Core;

/// <summary>
/// A WSL distribution of this PC as a device. It takes the same helper and the same steps as an SSH
/// device; only the way in differs: <c>wsl.exe</c> instead of <c>ssh</c>. Such a device is one whose
/// <see cref="SshDeviceConfiguration.Authentication"/> is <see cref="Authentication"/>, with the
/// distribution's name as its host.
/// </summary>
public sealed class WslStoppedException() : ClaudeSyncBarException("WSL 배포판이 정지되어 있습니다. 계정을 적용할 때 시작합니다.");

public static class WslTransport
{
    public const string Authentication = "wsl";

    // wsl.exe gives what it starts Windows' PATH after the distribution's own. A Windows program must
    // not stand in for a Linux one (claude, node), so the drive mounts are taken out before the
    // command runs, which leaves the PATH an SSH login to the same distribution would have.
    private const string Wrapper =
        "set -f; p=; IFS=:; for d in $PATH; do case \"$d\" in /mnt/?|/mnt/?/*) ;; *) p=\"${p:+$p:}$d\" ;; esac; done; "
        + "unset IFS; set +f; PATH=\"$p\"; export PATH; exec sh -c \"$1\"";

    private static readonly AsyncLocal<bool> Quiet = new();

    /// <summary>Stands in for wsl.exe in tests.</summary>
    internal static Func<IReadOnlyList<string>, string?, CancellationToken, TimeSpan, Task<ProcessResult>>? Runner { get; set; }

    public static bool Handles(SshDeviceConfiguration device) =>
        string.Equals(device.Authentication, Authentication, StringComparison.Ordinal);

    /// <summary>
    /// Until disposed, a stopped distribution is left stopped: a look made in the background must not
    /// start the WSL virtual machine. What the user asks for starts it.
    /// </summary>
    public static IDisposable LeaveStopped()
    {
        var before = Quiet.Value;
        Quiet.Value = true;
        return new Scope(before);
    }

    private sealed class Scope(bool before) : IDisposable
    {
        public void Dispose() => Quiet.Value = before;
    }

    /// <summary>The distributions of this PC, without Docker Desktop's own. Empty where there is no WSL.</summary>
    public static async Task<IReadOnlyList<WslDistribution>> DiscoverAsync(CancellationToken token = default)
    {
        if (!OperatingSystem.IsWindows() && Runner is null) return [];
        ProcessResult all;
        try { all = await RunWslAsync(["--list", "--quiet"], null, token, TimeSpan.FromSeconds(15)); }
        catch (ClaudeSyncBarException)
        {
            // No wsl.exe: this PC has no WSL.
            return [];
        }

        if (all.ExitCode != 0) return [];
        var running = await RunningAsync(token);
        return ParseList(all.StandardOutput).Select(name => new WslDistribution(name, running.Contains(name))).ToArray();
    }

    /// <summary>The user a distribution signs in by default, whose home the helper is installed in.</summary>
    public static async Task<string> DefaultUserAsync(string distribution, CancellationToken token = default)
    {
        ValidateDistribution(distribution);
        var result = await RunWslAsync(["--distribution", distribution, "--cd", "~", "--exec", "id", "-un"], null, token, TimeSpan.FromSeconds(60));
        var user = result.StandardOutput.Trim();
        if (result.ExitCode != 0 || !Regex.IsMatch(user, "^[A-Za-z0-9_][A-Za-z0-9._-]{0,63}$"))
            throw new ClaudeSyncBarException($"WSL 배포판 {distribution}의 사용자를 확인하지 못했습니다. 배포판이 시작되는지 확인해 주세요.");
        return user;
    }

    public static void ValidateDistribution(string distribution)
    {
        if (!Regex.IsMatch(distribution, "^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$")
            || distribution.StartsWith("docker-desktop", StringComparison.OrdinalIgnoreCase))
            throw new ClaudeSyncBarException("지원하지 않는 WSL 배포판 이름입니다.");
    }

    internal static IReadOnlyList<string> ParseList(string output) => output.Replace("\0", "")
        .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(name => !name.StartsWith("docker-desktop", StringComparison.OrdinalIgnoreCase))
        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    /// <summary>What wsl.exe is given to run a command in the device's distribution, in its user's home.</summary>
    internal static IReadOnlyList<string> Arguments(SshDeviceConfiguration device, string command)
    {
        ValidateDistribution(device.Host);
        return ["--distribution", device.Host, "--user", device.Username, "--cd", "~", "--exec", "sh", "-c", Wrapper, "syncbar", command];
    }

    /// <summary>Runs a command as ssh would: one line for the distribution's shell, with optional input.</summary>
    internal static async Task<ProcessResult> RunAsync(SshDeviceConfiguration device, IEnumerable<string> remoteArguments,
        string? standardInput, CancellationToken token, TimeSpan timeout)
    {
        var arguments = Arguments(device, string.Join(' ', remoteArguments));
        if (Quiet.Value && !(await RunningAsync(token)).Contains(device.Host))
            throw new WslStoppedException();
        return await RunWslAsync(arguments, standardInput, token, timeout);
    }

    /// <summary>
    /// Writes a file into the device's home. <paramref name="remoteFile"/> is given the way scp takes it:
    /// under <c>~/</c>.
    /// </summary>
    internal static async Task<ProcessResult> WriteFileAsync(SshDeviceConfiguration device, string contents, string remoteFile,
        CancellationToken token)
    {
        if (!Regex.IsMatch(remoteFile, "^~/[A-Za-z0-9._/-]+$") || remoteFile.Contains("..", StringComparison.Ordinal))
            throw new ClaudeSyncBarException("WSL에 쓸 파일 경로가 올바르지 않습니다.");
        return await RunAsync(device, ["umask 077; cat > \"$HOME/" + remoteFile[2..] + "\""], contents, token, TimeSpan.FromSeconds(60));
    }

    /// <summary>The names of the distributions running now. Asking does not start any.</summary>
    public static async Task<HashSet<string>> RunningAsync(CancellationToken token = default)
    {
        var none = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!OperatingSystem.IsWindows() && Runner is null) return none;
        ProcessResult running;
        try { running = await RunWslAsync(["--list", "--running", "--quiet"], null, token, TimeSpan.FromSeconds(15)); }
        catch (ClaudeSyncBarException) { return none; }
        // With none running wsl.exe fails and says so in words, which are not names.
        return running.ExitCode != 0 ? none : ParseList(running.StandardOutput).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static Task<ProcessResult> RunWslAsync(IReadOnlyList<string> arguments, string? input, CancellationToken token, TimeSpan timeout)
    {
        if (Runner is { } runner) return runner(arguments, input, token, timeout);
        var system = Path.Combine(Environment.SystemDirectory, "wsl.exe");
        // WSL_UTF8: wsl.exe's own words are otherwise UTF-16, unlike what the distribution prints.
        return ProcessRunner.RunAsync(File.Exists(system) ? system : "wsl.exe", arguments, input, token, timeout,
            new Dictionary<string, string?> { ["WSL_UTF8"] = "1" });
    }
}
