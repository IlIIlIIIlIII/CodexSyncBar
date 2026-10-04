using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodexSyncBar.Windows.Core;

internal sealed record AuthRefreshManifest(int ProfileId, string AccountFingerprint, string OriginalGeneration);

/// <summary>Retains a rotated refresh token even if the subsequent server validation or process fails.</summary>
public sealed class AuthRefreshTransactionStore(WindowsPaths paths, AuthStore auth)
{
    public void Begin(string runtime, int profileId, CodexAuthFile before)
    {
        var manifest = new AuthRefreshManifest(profileId, Fingerprint(before.Tokens.AccountId!), Generation(before));
        WindowsPathSafety.WritePrivateBytes(Path.Combine(runtime, "refresh-manifest.json"), Encoding.UTF8.GetBytes(JsonSerializer.Serialize(manifest)));
    }

    /// <returns>True when the private temporary home can safely be removed.</returns>
    public bool TryComplete(string runtime)
    {
        var manifestPath = Path.Combine(runtime, "refresh-manifest.json");
        var source = Path.Combine(runtime, "auth.json");
        // Recursive cleanup can remove the credentials and journal before a
        // locked CLI log/database stops it. Such leftovers need only cleanup.
        // An auth file without its journal still needs recovery; never discard it.
        WindowsPathSafety.EnsureFile(manifestPath, "인증 갱신 복구 기록");
        WindowsPathSafety.EnsureFile(source, "인증 갱신 임시 파일");
        if (!File.Exists(manifestPath)) return !File.Exists(source);
        var manifest = JsonSerializer.Deserialize<AuthRefreshManifest>(WindowsPathSafety.ReadPrivateFile(manifestPath, "인증 갱신 복구 기록", 8192))
            ?? throw new CodexSyncBarException("인증 갱신 복구 기록이 올바르지 않습니다.");
        if (manifest.ProfileId <= 0 || !Regex.IsMatch(manifest.AccountFingerprint, "^[a-f0-9]{64}$")
            || !Regex.IsMatch(manifest.OriginalGeneration, "^[a-f0-9]{64}$"))
            throw new CodexSyncBarException("인증 갱신 복구 기록이 올바르지 않습니다.");

        var candidate = auth.ReadAuthFile(source);
        if (Fingerprint(candidate.Tokens.AccountId!) != manifest.AccountFingerprint) return false;
        // No rotation happened: even if another CLI has advanced the canonical credentials,
        // this old temporary copy is disposable and must never replace that newer generation.
        if (Generation(candidate) == manifest.OriginalGeneration) return true;

        auth.ReconcileActiveCredentials(manifest.ProfileId);
        var current = auth.ReadAuthFile(auth.ProfileAuthFile(manifest.ProfileId));
        if (Fingerprint(current.Tokens.AccountId!) != manifest.AccountFingerprint) return false;
        var active = auth.ReadActiveAuth();
        var updateActive = active is not null && Generation(active) == manifest.OriginalGeneration
            && Fingerprint(active.Tokens.AccountId!) == manifest.AccountFingerprint;
        if (Generation(current) == Generation(candidate))
        {
            // A previous process may have committed the protected profile and exited
            // before replacing the active auth file. Finish that second atomic write.
            if (updateActive) auth.SwitchActive(manifest.ProfileId);
            return true;
        }
        if (Generation(current) != manifest.OriginalGeneration) return false;

        auth.ImportAuth(source, manifest.ProfileId, replaceExisting: true);
        if (updateActive) auth.SwitchActive(manifest.ProfileId);
        return true;
    }

    public IReadOnlyList<string> Recover()
    {
        if (!Directory.Exists(paths.LoginSessionsDirectory)) return [];
        var pending = new List<string>();
        foreach (var runtime in Directory.EnumerateDirectories(paths.LoginSessionsDirectory, "refresh-profile-*"))
        {
            if (!Regex.IsMatch(Path.GetFileName(runtime), "^refresh-profile-[1-9][0-9]*-[a-f0-9]{32}$")) continue;
            try
            {
                WindowsPathSafety.EnsureDirectory(runtime, "인증 갱신 복구 디렉터리");
#if SYNCBAR_LINUX
                auth.ProtectRecoveryAuthFile(Path.Combine(runtime, "auth.json"));
#endif
                if (TryComplete(runtime))
                {
                    try { Directory.Delete(runtime, recursive: true); }
                    catch (IOException) when (HasOnlyCleanupRemaining(runtime)) { }
                    catch (UnauthorizedAccessException) when (HasOnlyCleanupRemaining(runtime)) { }
                }
                else pending.Add(Path.GetFileName(runtime));
            }
            catch (Exception) { pending.Add(Path.GetFileName(runtime)); }
        }
        return pending;
    }

    private static bool HasOnlyCleanupRemaining(string runtime) =>
        !File.Exists(Path.Combine(runtime, "auth.json"))
        && !File.Exists(Path.Combine(runtime, "refresh-manifest.json"));

    internal static string Generation(CodexAuthFile value) => Fingerprint(value.Tokens.AccessToken + "\0" + value.Tokens.RefreshToken);
    private static string Fingerprint(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
