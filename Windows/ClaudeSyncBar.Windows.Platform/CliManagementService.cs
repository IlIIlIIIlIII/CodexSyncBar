// Windows port of Ubuntu/ClaudeSyncBar.Ubuntu.Platform/CliManagementService.cs. Keep the two in step;
// they differ only in install locations and in running npm as a command script.
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ClaudeSyncBar.Windows.Core;

public sealed record CliInstallation(string Version, string Path, string Manager, bool CanUpdate, string Notice)
{
    public static CliInstallation Parse(ProcessResult result)
    {
        if (result.ExitCode != 0) throw new ClaudeSyncBarException("CLI 조회 실패: " + result.CombinedOutput.Trim());
        return JsonSerializer.Deserialize<CliInstallation>(result.StandardOutput, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new ClaudeSyncBarException("CLI 조회 응답이 비어 있습니다.");
    }
}

public sealed record CliUpdateResult(string Before, string After, string Manager, string Restart)
{
    public string DisplayText => (Before == After
        ? $"이미 최신 버전입니다: {After}\n"
        : $"업데이트 완료: {Before} → {After}\n") + (Restart switch
    {
        "not-needed" => "버전 변경이 없어 실행 중인 CLI를 유지했습니다.",
        "not-running" => "새 버전은 다음 실행부터 적용됩니다. 실행 중인 Claude Code 세션은 종료 후 다시 시작해 주세요.",
        _ => "업데이트 완료 · 재시작 대기. 해당 장치에서 실행 중인 Claude Code 세션을 다시 시작해 주세요.",
    });

    public static CliUpdateResult Parse(ProcessResult result)
    {
        var line = result.StandardOutput.Split('\n').LastOrDefault(line => line.StartsWith("before=", StringComparison.Ordinal));
        if (line is null || result.ExitCode is not (0 or 2))
            throw new ClaudeSyncBarException("CLI 업데이트 실패: " + result.CombinedOutput.Trim());
        var fields = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(item => item.Split('=', 2))
            .Where(item => item.Length == 2).ToDictionary(item => item[0], item => item[1]);
        if (!fields.TryGetValue("after", out var after) || string.IsNullOrWhiteSpace(after))
            throw new ClaudeSyncBarException("업데이트된 CLI 버전을 확인하지 못했습니다.");
        return new(fields.GetValueOrDefault("before", ""), after, fields.GetValueOrDefault("manager", "unknown"), fields.GetValueOrDefault("restart", "reconnect-pending"));
    }
}

public interface ICliManagementService
{
    Task<CliInstallation> InspectAsync(CancellationToken cancellationToken = default);
    Task<CliUpdateResult> UpdateAsync(CancellationToken cancellationToken = default);
}

public sealed class RemoteCliManagementService(Func<bool, CancellationToken, Task<ProcessResult>> run) : ICliManagementService
{
    public async Task<CliInstallation> InspectAsync(CancellationToken cancellationToken = default) => CliInstallation.Parse(await run(false, cancellationToken));
    public async Task<CliUpdateResult> UpdateAsync(CancellationToken cancellationToken = default) => CliUpdateResult.Parse(await run(true, cancellationToken));
}

public sealed class WindowsCliManagementService(WindowsPaths paths, LocalSwitchService local) : ICliManagementService
{
    private const string Package = "@anthropic-ai/claude-code";
    // npm is a command script on Windows; the runner starts it through cmd.exe.
    private const string Npm = "npm.cmd";
    private static readonly string[] NpmOptions = ["--no-audit", "--no-fund", "--fetch-retries=1", "--fetch-timeout=60000"];
    // Test seam: the managed install runs npm, which needs the network.
    internal Func<string, IReadOnlyList<string>, CancellationToken, Task<ProcessResult>> RunNpm { get; init; } =
        (npm, arguments, token) => ProcessRunner.RunAsync(npm, arguments, cancellationToken: token, timeout: TimeSpan.FromMinutes(10));

    public async Task<CliInstallation> InspectAsync(CancellationToken cancellationToken = default)
    {
        var executable = ClaudeCliLocator.Find();
        if (executable is null) return new("미설치", "", "managed", true, "공식 Claude Code CLI를 이 사용자에게 설치합니다. npm이 필요합니다.");
        var version = await VersionAsync(executable, cancellationToken);
        var resolved = new FileInfo(executable).ResolveLinkTarget(true)?.FullName ?? Path.GetFullPath(executable);
        if (Under(Path.GetFullPath(executable), Path.Combine(paths.ExternalRuntimeDirectory, "tools")))
            return new(version, executable, "managed", true, "");
        // The native installer puts claude.exe in ~/.local/bin and updates it in place.
        if (string.Equals(resolved, ClaudeCliLocator.NativeExecutable(paths.Home), StringComparison.OrdinalIgnoreCase)
            || Under(resolved, Path.Combine(paths.Home, ".local", "share", "claude", "versions")))
            return new(version, executable, "native", true, "");
        if (Under(resolved, paths.DesktopBundledCliDirectory))
            return new(version, executable, "desktop", false, "Claude 데스크톱 앱에 포함된 CLI입니다. 데스크톱 앱을 업데이트하면 함께 갱신됩니다.");
        if (await FindNpmAsync(resolved, cancellationToken)) return new(version, executable, "npm", true, "");
        return new(version, executable, "unknown", false, "이 CLI를 설치한 패키지 관리자로 업데이트해 주세요.");
    }
    public async Task<CliUpdateResult> UpdateAsync(CancellationToken cancellationToken = default)
    {
        var before = await InspectAsync(cancellationToken);
        if (!before.CanUpdate) throw new ClaudeSyncBarException(before.Notice);
        string executable;
        if (before.Manager == "native")
        {
            var result = await ProcessRunner.RunAsync(before.Path, ["update"], cancellationToken: cancellationToken, timeout: TimeSpan.FromMinutes(10));
            if (result.ExitCode != 0) throw new ClaudeSyncBarException("Claude Code 업데이트에 실패했습니다. 네트워크를 확인해 주세요.");
            executable = before.Path;
        }
        else if (before.Manager == "npm")
        {
            var result = await RunNpm(Npm, ["install", "--global", Package + "@latest", .. NpmOptions], cancellationToken);
            if (result.ExitCode != 0) throw new ClaudeSyncBarException("npm 업데이트에 실패했습니다. 설치 디렉터리 권한과 네트워크를 확인해 주세요.");
            executable = before.Path;
        }
        else executable = await InstallManagedAsync(cancellationToken);
        var after = await VersionAsync(executable, cancellationToken);
        string restart;
        try { restart = before.Version == after ? "not-needed" : await local.ReconnectAfterCliUpdateAsync(after, cancellationToken); }
        catch { restart = "reconnect-pending"; }
        return new(before.Version, after, before.Manager, restart);
    }
    private static bool Under(string path, string directory) =>
        path.StartsWith(directory.TrimEnd('\\') + '\\', StringComparison.OrdinalIgnoreCase);

    private static async Task<string> VersionAsync(string executable, CancellationToken token)
    {
        var result = await ProcessRunner.RunAsync(executable, ["--version"], cancellationToken: token, timeout: TimeSpan.FromSeconds(15));
        var match = Regex.Match(result.StandardOutput.Trim(), "^([0-9]+\\.[0-9]+\\.[0-9]+(?:[-+][A-Za-z0-9.-]+)?) \\(Claude Code\\)$");
        if (result.ExitCode != 0 || !match.Success) throw new ClaudeSyncBarException("Claude Code CLI 버전을 확인하지 못했습니다.");
        return match.Groups[1].Value;
    }
    private static async Task<bool> FindNpmAsync(string resolved, CancellationToken token)
    {
        try
        {
            var result = await ProcessRunner.RunAsync(Npm, ["root", "-g"], cancellationToken: token, timeout: TimeSpan.FromSeconds(15));
            var root = result.StandardOutput.Trim();
            if (result.ExitCode != 0 || !Path.IsPathFullyQualified(root)) return false;
            var packageRoot = Path.Combine(root, "@anthropic-ai", "claude-code");
            // The npm shim claude.cmd sits in the prefix next to node_modules, not inside the package.
            var prefix = Path.GetDirectoryName(root);
            return File.Exists(Path.Combine(packageRoot, "package.json"))
                && (Under(resolved, packageRoot) || prefix is not null && string.Equals(Path.GetDirectoryName(resolved), prefix, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception error) when (error is not OperationCanceledException) { return false; }
    }
    // npm checks each download against the registry's integrity hash; the new
    // version is selected only after it runs and reports its own version.
    internal async Task<string> InstallManagedAsync(CancellationToken token)
    {
        var tools = Path.Combine(paths.ExternalRuntimeDirectory, "tools");
        WindowsPathSafety.EnsurePrivateDirectory(tools, "CLI 설치 디렉터리");
        var directory = Path.Combine(tools, "claude-code-" + Guid.NewGuid().ToString("N"));
        WindowsPathSafety.EnsurePrivateDirectory(directory, "CLI 버전 디렉터리");
        try
        {
            ProcessResult result;
            try { result = await RunNpm(Npm, ["install", "--prefix", directory, Package + "@latest", .. NpmOptions], token); }
            catch (Exception error) when (error is System.ComponentModel.Win32Exception or ClaudeSyncBarException) { throw new ClaudeSyncBarException("npm을 찾지 못했습니다. Node.js와 npm을 설치한 뒤 다시 시도해 주세요."); }
            if (result.ExitCode != 0) throw new ClaudeSyncBarException("Claude Code CLI 설치에 실패했습니다. 네트워크를 확인해 주세요.");
            var executable = ClaudeCliLocator.ManagedExecutable(directory);
            if (!ClaudeCliLocator.IsExecutable(executable)) throw new ClaudeSyncBarException("설치한 패키지에 Claude Code 실행 파일이 없습니다.");
            _ = await VersionAsync(executable, token);
            var pointer = Path.Combine(tools, "current.txt");
            WindowsPathSafety.EnsurePrivateFile(pointer, "CLI 버전 선택", 1024);
            var temporary = pointer + "." + Guid.NewGuid().ToString("N");
            try { WindowsPathSafety.WritePrivateBytes(temporary, System.Text.Encoding.UTF8.GetBytes(Path.GetFileName(directory))); File.Move(temporary, pointer, true); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            return executable;
        }
        catch
        {
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
            throw;
        }
    }
}
