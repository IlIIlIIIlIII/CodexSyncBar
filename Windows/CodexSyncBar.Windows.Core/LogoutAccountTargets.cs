using System.Text;
using System.Text.Json;

namespace CodexSyncBar.Windows.Core;

internal sealed record LogoutCheckpoint(string Operation, int PreviousProfileId);

/// <summary>Uses the same tested helper staging protocol on SSH and WSL.</summary>
internal sealed class RemoteLogoutTarget(
    string id, string displayName, string fingerprint, int removedProfileId,
    Func<string[], CancellationToken, Task<ProcessResult>> run) : IAccountTarget
{
    private LogoutCheckpoint? _checkpoint;
    private int _fallback;
    public string Id => id;
    public string DisplayName => displayName;
    public string ConfigurationFingerprint => fingerprint;
    public bool IsLocal => false;

    public async Task<string> PrepareAsync(int profileId, CancellationToken cancellationToken)
    {
        _fallback = profileId;
        var result = await run(["logout-preflight", removedProfileId.ToString(), profileId.ToString()], cancellationToken);
        EnsureSuccess(result);
        var active = result.StandardOutput.Split([' ', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(field => field.StartsWith("active=", StringComparison.Ordinal))?[7..];
        if (!int.TryParse(active, out var previous) || previous <= 0) throw new CodexSyncBarException("로그아웃 대상의 현재 계정을 확인하지 못했습니다.");
        _checkpoint = new($"logout_{removedProfileId}_{Guid.NewGuid():N}", previous);
        return JsonSerializer.Serialize(_checkpoint);
    }

    public async Task ApplyAsync(int profileId, CancellationToken cancellationToken)
    {
        if (_checkpoint is null) throw new CodexSyncBarException("로그아웃 사전 점검이 필요합니다.");
        EnsureSuccess(await run(["switch", profileId.ToString(), "1"], cancellationToken));
        EnsureSuccess(await run(["logout-stage", _checkpoint.Operation, removedProfileId.ToString(), profileId.ToString()], cancellationToken));
    }

    public async Task VerifyAsync(int profileId, CancellationToken cancellationToken)
    {
        if (_checkpoint is null) throw new CodexSyncBarException("로그아웃 사전 점검이 필요합니다.");
        EnsureSuccess(await run(["logout-verify", _checkpoint.Operation, removedProfileId.ToString(), profileId.ToString()], cancellationToken));
    }

    public async Task RestoreAsync(string checkpoint, CancellationToken cancellationToken)
    {
        var value = ReadCheckpoint(checkpoint);
        EnsureSuccess(await run(["logout-restore", value.Operation, removedProfileId.ToString()], cancellationToken));
        EnsureSuccess(await run(["switch", value.PreviousProfileId.ToString(), "1"], cancellationToken));
        EnsureSuccess(await run(["verify", value.PreviousProfileId.ToString()], cancellationToken));
    }

    public async Task ReleaseAsync(string checkpoint, CancellationToken cancellationToken)
    {
        var value = ReadCheckpoint(checkpoint);
        // Stage restore already removes its backup. Commit is idempotent for a completed logout;
        // when a rollback restored the profile, the helper rejects commit without deleting anything.
        if (_fallback > 0)
            EnsureSuccess(await run(["logout-commit", value.Operation, removedProfileId.ToString(), _fallback.ToString()], cancellationToken));
    }

    internal void SetFallback(int fallback) => _fallback = fallback;
    private static LogoutCheckpoint ReadCheckpoint(string json)
    {
        var checkpoint = JsonSerializer.Deserialize<LogoutCheckpoint>(json);
        if (checkpoint is null || checkpoint.PreviousProfileId <= 0
            || !System.Text.RegularExpressions.Regex.IsMatch(checkpoint.Operation, "^logout_[0-9]+_[a-f0-9]{32}$"))
            throw new CodexSyncBarException("로그아웃 복구 기록이 올바르지 않습니다.");
        return checkpoint;
    }
    private static void EnsureSuccess(ProcessResult result)
    {
        if (result.ExitCode != 0) throw new CodexSyncBarException("장치 로그아웃 작업을 확인하지 못했습니다.");
    }
}

internal sealed record LocalLogoutCheckpoint(CodexAuthFile Profile, CodexAuthFile? Active);

internal sealed class WindowsLogoutTarget(AuthStore auth, LocalSwitchService local, WindowsPaths paths, int removedProfileId) : IAccountTarget
{
    private readonly WindowsSecretStore _secrets = new(paths);
    public string Id => "windows";
    public string DisplayName => "이 Windows PC";
    public string ConfigurationFingerprint => paths.ActiveAuthFile;
    public bool IsLocal => true;

    public Task<string> PrepareAsync(int profileId, CancellationToken cancellationToken)
    {
        _ = auth.ReadCredentials(profileId);
        auth.ReconcileActiveCredentials(removedProfileId);
        var key = "logout-backup-" + Guid.NewGuid().ToString("N");
        _secrets.Save(JsonSerializer.Serialize(new LocalLogoutCheckpoint(auth.ReadAuthFile(auth.ProfileAuthFile(removedProfileId)), auth.ReadActiveAuth())), key);
        return Task.FromResult(key);
    }

    public async Task ApplyAsync(int profileId, CancellationToken cancellationToken)
    {
        await local.SwitchAsync(profileId, cancellationToken);
        auth.DeleteProfile(removedProfileId);
    }

    public Task VerifyAsync(int profileId, CancellationToken cancellationToken)
    {
        if (auth.ProfileArtifactExists(removedProfileId) || auth.ReadActiveAccountId() != auth.ReadCredentials(profileId).AccountId)
            throw new CodexSyncBarException("Windows 로그아웃을 확인하지 못했습니다.");
        return Task.CompletedTask;
    }

    public async Task RestoreAsync(string checkpoint, CancellationToken cancellationToken)
    {
        var value = JsonSerializer.Deserialize<LocalLogoutCheckpoint>(_secrets.Read(checkpoint)
            ?? throw new CodexSyncBarException("로그아웃 복구 인증이 없습니다."))
            ?? throw new CodexSyncBarException("로그아웃 복구 인증이 올바르지 않습니다.");
        var temporary = Path.Combine(paths.LoginSessionsDirectory, "logout-restore-" + Guid.NewGuid().ToString("N"), "auth.json");
        WindowsPathSafety.EnsureDirectory(Path.GetDirectoryName(temporary)!, "로그아웃 복구 디렉터리");
        try
        {
            WindowsPathSafety.WritePrivateBytes(temporary, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value.Profile)));
            auth.ImportAuth(temporary, removedProfileId, replaceExisting: true);
            await local.RestoreAsync(value.Active, cancellationToken);
            if (auth.ReadCredentials(removedProfileId).AccountId != value.Profile.Tokens.AccountId
                || auth.ReadActiveAccountId() != value.Active?.Tokens.AccountId)
                throw new CodexSyncBarException("Windows 로그아웃 복구를 확인하지 못했습니다.");
        }
        finally { if (Directory.Exists(Path.GetDirectoryName(temporary))) Directory.Delete(Path.GetDirectoryName(temporary)!, recursive: true); }
    }

    public Task ReleaseAsync(string checkpoint, CancellationToken cancellationToken)
    {
        _secrets.Delete(checkpoint);
        return Task.CompletedTask;
    }
}
