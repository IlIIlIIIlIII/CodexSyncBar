using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace CodexSyncBar.Windows.Core;

public sealed record SshHostKey(string Host, int Port, string LookupName, string KnownHostsFile, string Key, string Fingerprint);

public sealed class SshHostTrust
{
    // A successful unauthenticated handshake verifies the server before a secret is sent.
    public async Task<SshHostKey?> InspectAsync(SshDeviceConfiguration device, CancellationToken token = default)
    {
        var options = SshDeviceService.BuildCommonOptions(device, false, false);
        options.InsertRange(0, ["-o", "UpdateHostKeys=no", "-o", "PreferredAuthentications=none", "-o", "PubkeyAuthentication=no", "-o", "PasswordAuthentication=no", "-o", "KbdInteractiveAuthentication=no", "-o", "GSSAPIAuthentication=no", "-o", "HostbasedAuthentication=no"]);
        options.AddRange([$"{device.Username}@{device.Host}", "exit"]);
        var probe = await ProcessRunner.RunAsync("ssh.exe", options, cancellationToken: token, timeout: TimeSpan.FromSeconds(20));
        if (!RequiresRegistration(probe)) return null;

        var config = await ProcessRunner.RunAsync("ssh.exe", ["-G", "-p", device.Port.ToString(), $"{device.Username}@{device.Host}"], cancellationToken: token);
        if (config.ExitCode != 0) throw new CodexSyncBarException("OpenSSH 연결 설정을 읽을 수 없습니다.");
        var fields = config.StandardOutput.Split('\n').Select(line => line.Trim().Split(' ', 2))
            .Where(parts => parts.Length == 2).GroupBy(parts => parts[0]).ToDictionary(group => group.Key, group => group.First()[1]);
        var host = fields.GetValueOrDefault("hostname", device.Host);
        var port = int.Parse(fields.GetValueOrDefault("port", device.Port.ToString()));
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).Replace('\\', '/');
        var files = fields.GetValueOrDefault("userknownhostsfile", "~/.ssh/known_hosts");
        var match = Regex.Match(files, "^(?:\"([^\"]+)\"|(\\S+))");
        var knownHosts = (match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value)
            .Replace("~", home).Replace("%d", home).Replace("%h", host).Replace("%p", port.ToString()).Replace("%r", device.Username);
        if (!Path.IsPathFullyQualified(knownHosts) || knownHosts.Contains('%'))
            throw new CodexSyncBarException("호스트 키 저장 경로를 확인할 수 없습니다. OpenSSH known_hosts 설정을 확인해 주세요.");
        var alias = fields.GetValueOrDefault("hostkeyalias");
        var lookup = !string.IsNullOrWhiteSpace(alias) && alias != "none" ? alias : port == 22 ? host : $"[{host}]:{port}";
        // Windows ssh-keyscan can advertise KEX algorithms it cannot implement.
        // Use ssh's own handshake, with all authentication disabled, in an isolated
        // trust file. The user's real trust store is untouched until TrustAsync.
        var runtime = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex-syncbar");
        WindowsPathSafety.EnsurePrivateDirectory(runtime, "SSH 확인 디렉터리");
        var scanFile = Path.Combine(runtime, ".host-scan-" + Guid.NewGuid().ToString("N"));
        string scanned;
        try
        {
            WindowsPathSafety.WritePrivateBytes(scanFile, []);
            var scanOptions = new List<string> { "-o", "StrictHostKeyChecking=accept-new", "-o", $"UserKnownHostsFile=\"{scanFile.Replace('\\', '/')}\"", "-o", "GlobalKnownHostsFile=NUL", "-o", "HashKnownHosts=no" };
            scanOptions.AddRange(options);
            await ProcessRunner.RunAsync("ssh.exe", scanOptions, cancellationToken: token, timeout: TimeSpan.FromSeconds(20));
            scanned = await File.ReadAllTextAsync(scanFile, token);
        }
        finally { if (File.Exists(scanFile)) File.Delete(scanFile); }
        var keys = scanned.Split('\n').Select(line => line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Where(parts => parts.Length == 3 && !parts[0].StartsWith('#')).OrderBy(parts => parts[1] == "ssh-ed25519" ? 0 : 1).ToArray();
        if (keys.Length == 0) throw new CodexSyncBarException("서버 호스트 키를 가져오지 못했습니다.");
        var key = keys[0][1] + " " + keys[0][2];
        // Existing entries of any algorithm must never be silently replaced or supplemented.
        if (File.Exists(knownHosts))
        {
            var found = await ProcessRunner.RunAsync("ssh-keygen.exe", ["-F", lookup, "-f", knownHosts], cancellationToken: token);
            if (found.ExitCode == 0) throw new CodexSyncBarException("기존 서버 키가 있지만 검증에 실패했습니다. known_hosts 기록을 확인해 주세요.");
        }
        return new(host, port, lookup, knownHosts, key, Fingerprint(keys[0][2]));
    }

    public static string Fingerprint(string base64Key) => "SHA256:" + Convert.ToBase64String(SHA256.HashData(Convert.FromBase64String(base64Key))).TrimEnd('=');

    internal static bool RequiresRegistration(ProcessResult probe)
    {
        if (probe.StandardError.Contains("REMOTE HOST IDENTIFICATION HAS CHANGED", StringComparison.OrdinalIgnoreCase)
            || probe.StandardError.Contains("REVOKED HOST KEY", StringComparison.OrdinalIgnoreCase))
            throw new CodexSyncBarException("서버 호스트 키가 기존 기록과 다릅니다. 서버 변경 여부를 확인해야 하며 자동으로 덮어쓰지 않습니다.");
        if (probe.StandardError.Contains("Host key verification failed", StringComparison.OrdinalIgnoreCase)) return true;
        if (probe.ExitCode == 0 || probe.StandardError.Contains("Permission denied", StringComparison.OrdinalIgnoreCase)) return false;
        throw new CodexSyncBarException("SSH 네트워크·설정 확인 실패: " + probe.StandardError.Trim());
    }

    public async Task TrustAsync(SshDeviceConfiguration device, SshHostKey approved, CancellationToken token = default)
    {
        var current = await InspectAsync(device, token);
        if (current is null) return;
        if (current != approved) throw new CodexSyncBarException("확인 중 서버 키 또는 SSH 설정이 변경됐습니다. 다시 연결해 주세요.");
        var directory = Path.GetDirectoryName(approved.KnownHostsFile)!;
        WindowsPathSafety.EnsureDirectory(directory, "SSH 호스트 키 디렉터리");
        WindowsPathSafety.EnsureFile(approved.KnownHostsFile, "SSH 호스트 키 파일");
        // Append under an exclusive handle; preserve existing user records and their line ending.
        using var stream = new FileStream(approved.KnownHostsFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        stream.Seek(0, SeekOrigin.End);
        using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false));
        await writer.WriteLineAsync();
        await writer.WriteLineAsync($"{approved.LookupName} {approved.Key}");
    }
}
