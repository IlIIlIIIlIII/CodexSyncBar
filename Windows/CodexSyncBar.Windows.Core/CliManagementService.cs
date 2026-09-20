using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodexSyncBar.Windows.Core;

public sealed record CliInstallation(string Version, string Path, string Manager, bool CanUpdate, string Notice)
{
    public static CliInstallation Parse(ProcessResult result)
    {
        if (result.ExitCode != 0) throw new CodexSyncBarException("CLI 조회 실패: " + result.CombinedOutput.Trim());
        return JsonSerializer.Deserialize<CliInstallation>(result.StandardOutput, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new CodexSyncBarException("CLI 조회 응답이 비어 있습니다.");
    }
}

public sealed record CliUpdateResult(string Before, string After, string Manager, string Restart)
{
    public string DisplayText => $"업데이트 완료: {Before} → {After}\n" + (Restart switch
    {
        "reconnected" => "Codex 연결에 새 버전을 적용했습니다.",
        "not-running" => "실행 중인 Codex 연결이 없습니다. 다음 실행에 새 버전이 적용됩니다.",
        _ => "업데이트 완료 · 재연결 대기. 해당 장치의 Codex 연결을 다시 열어 주세요.",
    });

    public static CliUpdateResult Parse(ProcessResult result)
    {
        var line = result.StandardOutput.Split('\n').LastOrDefault(line => line.StartsWith("before=", StringComparison.Ordinal));
        if (line is null || result.ExitCode is not (0 or 2))
            throw new CodexSyncBarException("CLI 업데이트 실패: " + result.CombinedOutput.Trim());
        var fields = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(item => item.Split('=', 2))
            .Where(item => item.Length == 2).ToDictionary(item => item[0], item => item[1]);
        if (!fields.TryGetValue("after", out var after) || string.IsNullOrWhiteSpace(after))
            throw new CodexSyncBarException("업데이트된 CLI 버전을 확인하지 못했습니다.");
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

public sealed class WindowsCliManagementService(WindowsPaths paths, LocalSwitchService local, HttpClient? http = null) : ICliManagementService
{
    private static readonly HttpClient Http = CreateHttp();
    private static HttpClient CreateHttp()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("CodexSyncBar/1.0");
        return client;
    }

    public async Task<CliInstallation> InspectAsync(CancellationToken cancellationToken = default)
    {
        var executable = CodexCliLocator.Find();
        if (executable is null) return new("미설치", "", "managed", true, "공식 Codex CLI를 이 Windows 사용자용으로 설치합니다.");
        var version = await VersionAsync(executable, cancellationToken);
        var tools = Path.Combine(paths.Home, ".codex-syncbar", "Tools");
        if (Path.GetFullPath(executable).StartsWith(tools + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return new(version, executable, "managed", true, "");
        if (await FindNpmAsync(executable, cancellationToken) is not null)
            return new(version, executable, "npm", true, "");
        return new(version, executable, "unknown", false, "설치 방식을 확인할 수 없습니다. 이 CLI를 설치한 도구로 업데이트해 주세요.");
    }

    public async Task<CliUpdateResult> UpdateAsync(CancellationToken cancellationToken = default)
    {
        var before = await InspectAsync(cancellationToken);
        if (!before.CanUpdate) throw new CodexSyncBarException(before.Notice);
        string executable;
        if (before.Manager == "npm")
        {
            var npm = await FindNpmAsync(before.Path, cancellationToken) ?? throw new CodexSyncBarException("npm 설치 경로가 변경됐습니다.");
            var updated = await ProcessRunner.RunAsync(npm, ["install", "--global", "@openai/codex@latest", "--no-audit", "--no-fund", "--fetch-retries=1", "--fetch-timeout=60000"],
                cancellationToken: cancellationToken, timeout: TimeSpan.FromMinutes(10));
            if (updated.ExitCode != 0) throw new CodexSyncBarException("npm 업데이트 실패: " + updated.CombinedOutput.Trim());
            executable = before.Path;
        }
        else executable = await InstallManagedAsync(cancellationToken);
        var after = await VersionAsync(executable, cancellationToken);
        // Only restart after the new executable has passed its version check.
        string restart;
        try { restart = await local.ReconnectAfterCliUpdateAsync(after, cancellationToken); }
        catch { restart = "reconnect-pending"; }
        return new(before.Version, after, before.Manager, restart);
    }

    private static async Task<string> VersionAsync(string executable, CancellationToken token)
    {
        var result = await ProcessRunner.RunAsync(executable, ["--version"], cancellationToken: token, timeout: TimeSpan.FromSeconds(15));
        var match = Regex.Match(result.StandardOutput.Trim(), "^codex-cli ([0-9]+\\.[0-9]+\\.[0-9]+(?:[-+][A-Za-z0-9.-]+)?)$");
        if (result.ExitCode != 0 || !match.Success) throw new CodexSyncBarException("Codex CLI 버전을 확인하지 못했습니다.");
        return match.Groups[1].Value;
    }

    private static async Task<string?> FindNpmAsync(string executable, CancellationToken token)
    {
        if (!executable.EndsWith("codex.cmd", StringComparison.OrdinalIgnoreCase)) return null;
        var npm = Path.Combine(Path.GetDirectoryName(executable)!, "npm.cmd");
        if (!File.Exists(npm)) npm = "npm.cmd";
        try
        {
            var root = await ProcessRunner.RunAsync(npm, ["root", "-g"], cancellationToken: token, timeout: TimeSpan.FromSeconds(15));
            var directory = root.StandardOutput.Trim();
            if (root.ExitCode != 0 || !Path.IsPathFullyQualified(directory)) return null;
            var shim = Path.Combine(Path.GetDirectoryName(directory)!, "codex.cmd");
            return string.Equals(Path.GetFullPath(shim), Path.GetFullPath(executable), StringComparison.OrdinalIgnoreCase)
                && File.Exists(Path.Combine(directory, "@openai", "codex", "package.json")) ? npm : null;
        }
        catch (Exception error) when (error is not OperationCanceledException) { return null; }
    }

    internal async Task<string> InstallManagedAsync(CancellationToken token)
    {
        var client = http ?? Http;
        using var release = JsonDocument.Parse(await client.GetStringAsync("https://api.github.com/repos/openai/codex/releases/latest", token));
        var root = release.RootElement;
        var tag = root.GetProperty("tag_name").GetString()!;
        if (root.GetProperty("prerelease").GetBoolean() || !Regex.IsMatch(tag, "^rust-v[0-9]+\\.[0-9]+\\.[0-9]+$"))
            throw new CodexSyncBarException("공식 안정 릴리스 정보를 확인하지 못했습니다.");
        var architecture = RuntimeInformation.OSArchitecture switch { Architecture.X64 => "x86_64", Architecture.Arm64 => "aarch64", _ => throw new CodexSyncBarException("지원하지 않는 Windows 아키텍처입니다.") };
        var name = $"codex-{architecture}-pc-windows-msvc.exe.zip";
        var asset = root.GetProperty("assets").EnumerateArray().Single(item => item.GetProperty("name").GetString() == name);
        var digest = asset.GetProperty("digest").GetString() ?? "";
        var url = asset.GetProperty("browser_download_url").GetString();
        if (!Regex.IsMatch(digest, "^sha256:[a-fA-F0-9]{64}$") || url != $"https://github.com/openai/codex/releases/download/{tag}/{name}")
            throw new CodexSyncBarException("공식 다운로드 주소 또는 해시가 올바르지 않습니다.");
        var tools = Path.Combine(paths.Home, ".codex-syncbar", "Tools");
        WindowsPathSafety.EnsurePrivateDirectory(tools, "Codex CLI 디렉터리");
        var versionDirectory = Path.Combine(tools, tag + "-" + Guid.NewGuid().ToString("N"));
        WindowsPathSafety.EnsurePrivateDirectory(versionDirectory, "Codex CLI 설치 디렉터리");
        var archive = Path.Combine(versionDirectory, "release.zip");
        using (var source = await client.GetStreamAsync(url, token))
        using (var destination = File.Create(archive)) await source.CopyToAsync(destination, token);
        using (var stream = File.OpenRead(archive))
        {
            var actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, token));
            if (!actual.Equals(digest[7..], StringComparison.OrdinalIgnoreCase)) throw new CodexSyncBarException("CLI 다운로드 해시가 일치하지 않습니다. 기존 CLI를 유지합니다.");
        }
        using (var zip = ZipFile.OpenRead(archive))
        {
            foreach (var file in new[] { name[..^4], "codex-command-runner.exe", "codex-windows-sandbox-setup.exe" })
            {
                var entry = zip.Entries.SingleOrDefault(item => item.FullName == file) ?? throw new CodexSyncBarException("공식 CLI 압축 파일에 필수 실행 파일이 없습니다.");
                entry.ExtractToFile(Path.Combine(versionDirectory, file == name[..^4] ? "codex.exe" : file));
            }
        }
        var executable = Path.Combine(versionDirectory, "codex.exe");
        if (await VersionAsync(executable, token) != tag[6..]) throw new CodexSyncBarException("설치된 버전이 릴리스와 일치하지 않습니다.");
        var pointer = Path.Combine(tools, "current.txt");
        WindowsPathSafety.EnsureFile(pointer, "CLI 버전 선택 파일");
        var temporary = Path.Combine(tools, ".current-" + Guid.NewGuid().ToString("N"));
        await File.WriteAllTextAsync(temporary, Path.GetFileName(versionDirectory), token);
        File.Move(temporary, pointer, true);
        File.Delete(archive);
        return executable;
    }
}
