using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace ClaudeSyncBar.Windows.Core;

/// <summary>
/// One Claude login as SyncBar stores and transports it. The two members use
/// the key names of the files Claude Code reads them from: claudeAiOauth in
/// .credentials.json and oauthAccount in .claude.json.
/// </summary>
public sealed class ClaudeAuthFile
{
    [JsonPropertyName("claudeAiOauth")]
    public ClaudeOauthTokens Oauth { get; set; } = new();

    [JsonPropertyName("oauthAccount")]
    public ClaudeOauthAccount Account { get; set; } = new();

    [JsonIgnore]
    public string? AccessToken => Oauth?.AccessToken;

    [JsonIgnore]
    public string? RefreshToken => Oauth?.RefreshToken;

    // One person can hold seats in several organizations, each with its own
    // quota, and one organization has many members. Both parts name a login.
    [JsonIgnore]
    public string? AccountId => string.IsNullOrWhiteSpace(Account?.AccountUuid)
        ? null
        : string.IsNullOrWhiteSpace(Account.OrganizationUuid)
            ? Account.AccountUuid
            : Account.OrganizationUuid + ":" + Account.AccountUuid;
}

public sealed class ClaudeOauthTokens
{
    [JsonPropertyName("accessToken")]
    public string? AccessToken { get; set; }

    [JsonPropertyName("refreshToken")]
    public string? RefreshToken { get; set; }

    /// <summary>Unix time in milliseconds, as Claude Code writes it.</summary>
    [JsonPropertyName("expiresAt")]
    public long? ExpiresAt { get; set; }

    [JsonPropertyName("scopes")]
    public List<string>? Scopes { get; set; }

    [JsonPropertyName("subscriptionType")]
    public string? SubscriptionType { get; set; }

    [JsonPropertyName("rateLimitTier")]
    public string? RateLimitTier { get; set; }

    // Fields added by later Claude Code versions must survive a switch.
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class ClaudeOauthAccount
{
    [JsonPropertyName("accountUuid")]
    public string? AccountUuid { get; set; }

    [JsonPropertyName("emailAddress")]
    public string? EmailAddress { get; set; }

    [JsonPropertyName("organizationUuid")]
    public string? OrganizationUuid { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class AuthStore
{
    private const string ProtectionKey = "claude-account-auth-v1";
    private const string CredentialsFileName = ".credentials.json";
    private const string ConfigFileName = ".claude.json";
    private const long MaximumAuthBytes = 16 * 1024 * 1024;
    private const long MaximumConfigBytes = 64 * 1024 * 1024;

    // A field the login never had is left out rather than written as null:
    // Claude Code reads these entries and does not expect null members.
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // Claude Code writes its own files without escaping non-ASCII text.
    private static readonly JsonSerializerOptions ClaudeFileOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly WindowsPaths _paths;

    public AuthStore(WindowsPaths paths)
    {
        _paths = paths;
    }

    public string ProfileAuthFile(int profileId) => _paths.ProfileAuthFile(profileId);

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private bool IsActivePath(string path) => Path.GetFullPath(path).Equals(
        Path.GetFullPath(_paths.ActiveAuthFile), PathComparison);

    /// <summary>Call under the controller lock after transaction recovery.</summary>
    public void ProtectLegacyProfiles()
    {
#if !SYNCBAR_LINUX
        if (!OperatingSystem.IsWindows()) return;
#endif
        foreach (var profileId in ExistingProfileIds())
        {
            var path = ProfileAuthFile(profileId);
            using var document = JsonDocument.Parse(WindowsPathSafety.ReadPrivateFile(path, "보관 인증", MaximumAuthBytes));
            if (!document.RootElement.TryGetProperty("protectedAuth", out _))
                WriteJsonAtomically(path, ReadAuthFile(path));
        }
    }

#if SYNCBAR_LINUX
    // Only after the isolated CLI has stopped: pending recovery must not keep
    // its temporary plaintext credential file on disk between service runs.
    internal void ProtectRecoveryAuthFile(string path)
    {
        // The CLI may have rotated the tokens after the last capture. Its own
        // credential file is then the only valid copy and wins over ours.
        var session = Path.GetDirectoryName(Path.GetFullPath(path))!;
        if (File.Exists(Path.Combine(session, CredentialsFileName)))
        {
            ClaudeOauthAccount? known = null;
            try { known = ReadAuthFile(path).Account; }
            catch (ClaudeSyncBarException) { /* A login session has no earlier copy. */ }
            try { CaptureClaudeHome(session, known); }
            catch (ClaudeSyncBarException) { ProtectRawFile(Path.Combine(session, CredentialsFileName)); }
        }
        ProtectRawFile(path);
    }

    private static void ProtectRawFile(string path)
    {
        if (!File.Exists(path)) return;
        var bytes = WindowsPathSafety.ReadPrivateFile(path, "인증 복구 파일", MaximumAuthBytes);
        try
        {
            using var document = JsonDocument.Parse(bytes);
            if (document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("protectedAuth", out _)) return;
        }
        catch (JsonException) { /* Preserve even an incomplete credential file in encrypted form. */ }
        var protectedBytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1,
            protectedAuth = Convert.ToBase64String(WindowsSecretStore.Protect(bytes, ProtectionKey)),
        });
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { WindowsPathSafety.WritePrivateBytes(temporary, protectedBytes); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal void ProtectAbandonedSessionCredentials()
    {
        if (!Directory.Exists(_paths.LoginSessionsDirectory)) return;
        foreach (var directory in Directory.EnumerateDirectories(_paths.LoginSessionsDirectory))
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(directory),
                "^(?:refresh-)?profile-[1-9][0-9]*-[a-f0-9]{32}$|^logout-restore-[a-f0-9]{32}$")) continue;
            ProtectRecoveryAuthFile(Path.Combine(directory, "auth.json"));
        }
    }

    internal void RestoreProfileSnapshot(int profileId, ClaudeAuthFile auth)
    {
        ValidateFullAuth(auth);
        WriteJsonAtomically(ProfileAuthFile(profileId), auth);
    }
#endif

    /// <summary>
    /// Lays out a session's auth.json as the two files an isolated Claude CLI
    /// reads when the session directory is its CLAUDE_CONFIG_DIR.
    /// </summary>
    public void MaterializeClaudeHome(string session)
    {
        var auth = ReadAuthFile(Path.Combine(session, "auth.json"));
        WriteClaudeFile(Path.Combine(session, CredentialsFileName),
            new JsonObject { ["claudeAiOauth"] = JsonSerializer.SerializeToNode(auth.Oauth, ClaudeFileOptions) });
        // Without the onboarding marker a non-interactive CLI stops at first-run setup.
        WriteClaudeFile(Path.Combine(session, ConfigFileName), new JsonObject
        {
            ["hasCompletedOnboarding"] = true,
            ["oauthAccount"] = JsonSerializer.SerializeToNode(auth.Account, ClaudeFileOptions),
        });
    }

    /// <summary>
    /// Folds the files an isolated Claude CLI wrote back into the session's
    /// auth.json and removes the CLI's plaintext credential file.
    /// </summary>
    public ClaudeAuthFile CaptureClaudeHome(string session, ClaudeOauthAccount? knownAccount = null)
    {
        var credentials = Path.Combine(session, CredentialsFileName);
        var auth = ReadClaudeFiles(credentials, Path.Combine(session, ConfigFileName))
            ?? throw new AuthenticationRequiredException("Claude CLI가 로그인 정보를 저장하지 않았습니다.");
        // A refresh that fails after the token exchange leaves no account record;
        // the tokens still belong to the account that was being refreshed.
        if (string.IsNullOrWhiteSpace(auth.Account.AccountUuid) && knownAccount is not null) auth.Account = knownAccount;
        ValidateFullAuth(auth);
        WriteJsonAtomically(Path.Combine(session, "auth.json"), auth);
        File.Delete(credentials);
        return auth;
    }

    // A running CLI may rotate the active account's tokens. Prefer that copy
    // only when it identifies the same account and expires later.
    public void ReconcileActiveCredentials(int profileId)
    {
        var stored = ReadAuthFile(ProfileAuthFile(profileId));
        var owner = ResolveActiveOwner();
        // Tokens are opaque, so a guess about whose they are must never be
        // stored: that would put one account's login into another's profile.
        if (owner is not { Proven: true } || owner.Login.AccountId != stored.AccountId) return;
        var active = owner.Login;
        ValidateFullAuth(active);
        if (active.Oauth.ExpiresAt is { } activeAt
            && (stored.Oauth.ExpiresAt is not { } storedAt || activeAt > storedAt))
            WriteJsonAtomically(ProfileAuthFile(profileId), active);
    }

    public bool ProfileArtifactExists(int profileId)
    {
        var path = ProfileAuthFile(profileId);
        EnsureSafeFile(path);
        return File.Exists(path);
    }

    public void DeleteProfile(int profileId)
    {
        var path = ProfileAuthFile(profileId);
        EnsureSafeFile(path);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    public ClaudeAuthFile ReadAuthFile(string path)
    {
        EnsureSafeFile(path);
        if (IsActivePath(path))
        {
            return ReadActiveAuth() ?? throw new ClaudeSyncBarException($"인증 파일을 찾을 수 없습니다: {path}");
        }

        if (!File.Exists(path))
        {
            throw new ClaudeSyncBarException($"인증 파일을 찾을 수 없습니다: {path}");
        }

        try
        {
            var bytes = WindowsPathSafety.ReadPrivateFile(path, "Claude 인증 파일", MaximumAuthBytes);
            using (var envelope = JsonDocument.Parse(bytes))
            {
                if (envelope.RootElement.ValueKind == JsonValueKind.Object
                    && envelope.RootElement.TryGetProperty("protectedAuth", out var protectedAuth))
                {
#if !SYNCBAR_LINUX
                    if (!OperatingSystem.IsWindows())
                        throw new PlatformNotSupportedException("DPAPI 인증은 해당 Windows 사용자만 해독할 수 있습니다.");
#endif
                    bytes = WindowsSecretStore.Unprotect(Convert.FromBase64String(protectedAuth.GetString()!), ProtectionKey);
                }
            }
            var auth = JsonSerializer.Deserialize<ClaudeAuthFile>(
                           bytes,
                           JsonOptions)
                ?? throw new AuthenticationRequiredException("인증 파일이 비어 있습니다.");
            ValidateFullAuth(auth);
            return auth;
        }
        catch (JsonException error)
        {
            throw new AuthenticationRequiredException($"인증 파일 형식이 올바르지 않습니다: {error.Message}");
        }
    }

    public ProfileCredentials ReadCredentials(int profileId)
    {
        var path = ProfileAuthFile(profileId);
        var auth = ReadAuthFile(path);
        var accountId = auth.AccountId!;
        var email = auth.Account.EmailAddress is { } address && address.Contains('@')
            ? address
            : $"계정 {profileId}";
        var activeAccountId = ReadActiveAccountId();

        return new ProfileCredentials(
            profileId,
            auth.AccessToken!,
            auth.RefreshToken!,
            accountId,
            email,
            auth.Oauth.ExpiresAt is { } expiresAt ? DateTimeOffset.FromUnixTimeMilliseconds(expiresAt) : null,
            path,
            string.Equals(activeAccountId, accountId, StringComparison.Ordinal),
            DescribePlan(auth.Oauth));
    }

    public string? ReadActiveAccountId()
    {
        try
        {
            return ResolveActiveOwner()?.Login.AccountId;
        }
        catch (AuthenticationRequiredException)
        {
            return null;
        }
    }

    public ClaudeAuthFile? ReadActiveAuth()
    {
        var auth = ResolveActiveOwner()?.Login
            ?? ReadClaudeFiles(_paths.ActiveAuthFile, _paths.ClaudeConfigFile);
        if (auth is null) return null;
        ValidateFullAuth(auth);
        return auth;
    }

    /// <summary>
    /// Asks the service whose tokens the active login holds when that cannot
    /// be told from local files, and records the answer for those tokens.
    /// </summary>
    public async Task VerifyActiveOwnerAsync(
        Func<string, CancellationToken, Task<ClaudeOauthAccount?>> fetchAccount,
        CancellationToken cancellationToken = default)
    {
        var tokens = ReadActiveTokens();
        if (string.IsNullOrWhiteSpace(tokens?.AccessToken) || ResolveActiveOwner() is { Proven: true }) return;
        var account = await fetchAccount(tokens.AccessToken, cancellationToken);
        if (string.IsNullOrWhiteSpace(account?.AccountUuid)) return;
        // The tokens may have changed while the request was in flight.
        var current = ReadActiveTokens();
        if (current?.AccessToken == tokens.AccessToken) SaveActiveMarker(current, account);
    }

    private sealed record ActiveOwner(ClaudeAuthFile Login, bool Proven);

    private sealed class ActiveLoginMarker
    {
        public string? AccessTokenSha256 { get; set; }
        public string? RefreshTokenSha256 { get; set; }
        public ClaudeOauthAccount? Account { get; set; }
    }

    // Claude Code names the signed-in account in its global config, and every
    // session on the machine shares that file, including ones Claude Desktop
    // hosts under its own login. The tokens are what the CLI actually uses, so
    // the account is taken from whoever is known to own these tokens first,
    // and from the config only while nothing contradicts it.
    private ActiveOwner? ResolveActiveOwner()
    {
        var tokens = ReadActiveTokens();
        if (string.IsNullOrWhiteSpace(tokens?.AccessToken)) return null;
        ActiveOwner Owned(ClaudeOauthAccount account, bool proven) =>
            new(new ClaudeAuthFile { Oauth = tokens, Account = account }, proven);

        var marker = LoadActiveMarker();
        if (marker?.Account is { } marked && MarkerMatches(marker, tokens)) return Owned(marked, true);

        foreach (var profileId in ExistingProfileIds())
        {
            ClaudeAuthFile stored;
            try { stored = ReadAuthFile(ProfileAuthFile(profileId)); }
            catch (ClaudeSyncBarException) { continue; }
            if (stored.AccessToken == tokens.AccessToken
                || !string.IsNullOrEmpty(tokens.RefreshToken) && stored.RefreshToken == tokens.RefreshToken)
            {
                SaveActiveMarker(tokens, stored.Account);
                return Owned(stored.Account, true);
            }
        }

        var configured = ReadClaudeFiles(_paths.ActiveAuthFile, _paths.ClaudeConfigFile)?.Account;
        var configuredId = new ClaudeAuthFile { Account = configured ?? new() }.AccountId;
        if (marker?.Account is null)
        {
            // Nothing was ever installed from here; the config is all there is.
            return configuredId is null ? null : Owned(configured!, false);
        }

        // The CLI refreshed the tokens it was given: same account, new tokens.
        if (configuredId is not null && configuredId == new ClaudeAuthFile { Account = marker.Account }.AccountId)
        {
            SaveActiveMarker(tokens, marker.Account);
            return Owned(marker.Account, true);
        }

        // New tokens and a different account in the config: either a manual
        // sign-in or a refresh plus another session's rewrite. Unknown until verified.
        return null;
    }

    private ClaudeOauthTokens? ReadActiveTokens()
    {
        EnsureSafeFile(_paths.ActiveAuthFile);
        try
        {
            return (ReadClaudeObject(_paths.ActiveAuthFile, "활성 Claude 인증 파일", MaximumAuthBytes, tolerateInvalid: true)
                ?["claudeAiOauth"] as JsonObject)?.Deserialize<ClaudeOauthTokens>(JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool MarkerMatches(ActiveLoginMarker marker, ClaudeOauthTokens tokens) =>
        marker.AccessTokenSha256 == Hash(tokens.AccessToken)
        || !string.IsNullOrEmpty(tokens.RefreshToken) && marker.RefreshTokenSha256 == Hash(tokens.RefreshToken);

    private ActiveLoginMarker? LoadActiveMarker()
    {
        var path = _paths.ActiveLoginMarkerFile;
        EnsureSafeFile(path);
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<ActiveLoginMarker>(
                WindowsPathSafety.ReadPrivateFile(path, "활성 로그인 기록", 1024 * 1024), JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private void SaveActiveMarker(ClaudeOauthTokens tokens, ClaudeOauthAccount account)
    {
        var path = _paths.ActiveLoginMarkerFile;
        EnsureSafeFile(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        // Only digests of the tokens are kept; the file is not a credential.
        WindowsPathSafety.WritePrivateBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(new ActiveLoginMarker
        {
            AccessTokenSha256 = Hash(tokens.AccessToken),
            RefreshTokenSha256 = string.IsNullOrEmpty(tokens.RefreshToken) ? null : Hash(tokens.RefreshToken),
            Account = account,
        }, JsonOptions));
        try { File.Move(temporary, path, overwrite: true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static string? Hash(string? value) => string.IsNullOrEmpty(value)
        ? null
        : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    public void RestoreActive(ClaudeAuthFile? auth)
    {
        if (auth is null)
        {
            ClearActive();
            return;
        }

        ValidateFullAuth(auth);
        WriteJsonAtomically(_paths.ActiveAuthFile, auth);
    }

    public IReadOnlyList<int> ExistingProfileIds()
    {
        if (!Directory.Exists(_paths.ProfilesDirectory))
        {
            return [];
        }

        return Directory.EnumerateFiles(_paths.ProfilesDirectory, "*.auth.json")
            .Select(path => Path.GetFileName(path).Replace(".auth.json", string.Empty))
            .Select(value => int.TryParse(value, out var id) ? id : 0)
            .Where(id => id > 0)
            .OrderBy(id => id)
            .ToArray();
    }

    public void ImportAuth(string sourcePath, int profileId, bool replaceExisting = false)
    {
        EnsureSafeFile(sourcePath);
        var incoming = ReadAuthFile(sourcePath);
        foreach (var otherProfileId in ExistingProfileIds().Where(id => id != profileId))
        {
            try
            {
                var other = ReadAuthFile(ProfileAuthFile(otherProfileId));
                if (string.Equals(other.AccountId, incoming.AccountId, StringComparison.Ordinal))
                {
                    throw new ClaudeSyncBarException(
                        $"이 계정은 이미 {otherProfileId}번 계정에 연결되어 있습니다.");
                }
            }
            catch (AuthenticationRequiredException)
            {
                // Keep malformed or expired rows visible; they must not block
                // importing a new account into a different slot.
            }
        }

        var destination = ProfileAuthFile(profileId);
        EnsureSafeFile(destination);
        if (File.Exists(destination) && !replaceExisting)
        {
            var existing = ReadAuthFile(destination);
            if (!string.Equals(existing.AccountId, incoming.AccountId, StringComparison.Ordinal))
            {
                throw new ClaudeSyncBarException("선택한 프로필과 다른 계정입니다. 재인증이라면 기존 계정 교체를 허용해 주세요.");
            }
        }

        WriteJsonAtomically(destination, incoming);
    }

    /// <summary>
    /// Turns a file the user picked into a login document in the private
    /// session area. The file is either such a document already or Claude
    /// Code's own credential file, which is paired with the config file that
    /// Claude Code keeps beside it.
    /// </summary>
    public string StageLoginForImport(string path)
    {
        EnsureSafeFile(path);
        if (!File.Exists(path)) throw new ClaudeSyncBarException($"인증 파일을 찾을 수 없습니다: {path}");
        ClaudeAuthFile login;
        JsonObject? picked;
        try { picked = ReadClaudeObject(path, "가져올 인증 파일", MaximumAuthBytes, tolerateInvalid: false); }
        catch (JsonException error) { throw new AuthenticationRequiredException($"인증 파일 형식이 올바르지 않습니다: {error.Message}"); }
        if (picked is not null && picked.ContainsKey("claudeAiOauth") && !picked.ContainsKey("oauthAccount"))
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
            // With CLAUDE_CONFIG_DIR the config sits next to the credentials;
            // by default it is one level up, in the home directory.
            var config = new[] { Path.Combine(directory, ConfigFileName), Path.Combine(Path.GetDirectoryName(directory) ?? directory, ConfigFileName) }
                .FirstOrDefault(File.Exists)
                ?? throw new AuthenticationRequiredException("이 인증 파일의 계정 정보(.claude.json)를 찾지 못했습니다.");
            login = ReadClaudeFiles(path, config)!;
            ValidateFullAuth(login);
        }
        else login = ReadAuthFile(path);

        _paths.EnsureDirectories();
        var staged = Path.Combine(_paths.LoginSessionsDirectory, $"import-{Guid.NewGuid():N}.json");
        WriteJsonAtomically(staged, login);
        return staged;
    }

    public void CopyAuthFile(string sourcePath, string destinationPath)
    {
        var auth = ReadAuthFile(sourcePath);
        WriteJsonAtomically(destinationPath, auth);
    }

    public void SwitchActive(int profileId)
    {
        var source = ProfileAuthFile(profileId);
        var auth = ReadAuthFile(source);
        WriteJsonAtomically(_paths.ActiveAuthFile, auth);
    }

    public ClaudeAuthFile CreateAccessOnlyCopy(int profileId)
    {
        var auth = ReadAuthFile(ProfileAuthFile(profileId));
        var tokens = JsonSerializer.Deserialize<ClaudeOauthTokens>(JsonSerializer.Serialize(auth.Oauth, JsonOptions), JsonOptions)!;
        tokens.RefreshToken = string.Empty;
        return new ClaudeAuthFile { Oauth = tokens, Account = auth.Account };
    }

    private void WriteJsonAtomically(string destination, ClaudeAuthFile auth)
    {
        EnsureSafeFile(destination);
        if (IsActivePath(destination))
        {
            WriteActive(auth);
            return;
        }

        if (File.Exists(destination))
        {
            WindowsPathSafety.EnsurePrivateFile(destination, "Claude 인증 파일", MaximumAuthBytes);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = Path.Combine(
            Path.GetDirectoryName(destination)!,
            $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(auth, JsonOptions);
        // Profiles and rollback copies stay encrypted. Only the active CLI
        // files and an explicitly isolated login/refresh session need plaintext.
        var isSession = Path.GetFullPath(destination).StartsWith(Path.GetFullPath(_paths.LoginSessionsDirectory)
            + Path.DirectorySeparatorChar, PathComparison);
#if SYNCBAR_LINUX
        if (!isSession)
#else
        if (OperatingSystem.IsWindows() && !isSession)
#endif
        {
            bytes = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schemaVersion = 1,
                protectedAuth = Convert.ToBase64String(WindowsSecretStore.Protect(bytes, ProtectionKey)),
            });
        }
        WindowsPathSafety.WritePrivateBytes(temporary, bytes);
        try
        {
            File.Move(temporary, destination, overwrite: true);
            WindowsPathSafety.EnsurePrivateFile(destination, "Claude 인증 파일", MaximumAuthBytes);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    // Claude Code keeps other state in both files (MCP tokens, project history),
    // so only the login entries are replaced.
    private void WriteActive(ClaudeAuthFile auth)
    {
        EnsureSafeFile(_paths.ClaudeConfigFile);
        Directory.CreateDirectory(_paths.ClaudeHome);
#if SYNCBAR_LINUX
        WindowsPathSafety.EnsureOwnerWritableOnly(_paths.ClaudeHome, "Claude 설정 디렉터리");
#endif
        var credentials = ReadClaudeObject(_paths.ActiveAuthFile, "활성 Claude 인증 파일", MaximumAuthBytes, tolerateInvalid: true)
            ?? new JsonObject();
        credentials["claudeAiOauth"] = JsonSerializer.SerializeToNode(auth.Oauth, ClaudeFileOptions);
        WriteClaudeFile(_paths.ActiveAuthFile, credentials);
        UpdateClaudeConfig(config => config["oauthAccount"] = JsonSerializer.SerializeToNode(auth.Account, ClaudeFileOptions));
        SaveActiveMarker(auth.Oauth, auth.Account);
    }

    private void ClearActive()
    {
#if SYNCBAR_LINUX
        WindowsPathSafety.EnsureOwnerWritableOnly(_paths.ClaudeHome, "Claude 설정 디렉터리");
#endif
        EnsureSafeFile(_paths.ActiveAuthFile);
        EnsureSafeFile(_paths.ClaudeConfigFile);
        if (File.Exists(_paths.ActiveAuthFile))
        {
            var credentials = ReadClaudeObject(_paths.ActiveAuthFile, "활성 Claude 인증 파일", MaximumAuthBytes, tolerateInvalid: true);
            credentials?.Remove("claudeAiOauth");
            if (credentials is null || credentials.Count == 0) File.Delete(_paths.ActiveAuthFile);
            else WriteClaudeFile(_paths.ActiveAuthFile, credentials);
        }

        if (File.Exists(_paths.ClaudeConfigFile))
        {
            UpdateClaudeConfig(config => config.Remove("oauthAccount"));
        }

        EnsureSafeFile(_paths.ActiveLoginMarkerFile);
        File.Delete(_paths.ActiveLoginMarkerFile);
    }

    private void UpdateClaudeConfig(Action<JsonObject> update)
    {
#if SYNCBAR_LINUX
        using var configLock = ClaudeConfigLock.Acquire(_paths.ClaudeConfigFile);
#endif
        // An unreadable config is left alone: replacing it would discard the
        // user's Claude Code settings and history.
        var config = ReadClaudeObject(_paths.ClaudeConfigFile, "Claude 설정 파일", MaximumConfigBytes, tolerateInvalid: false)
            ?? new JsonObject();
        update(config);
        WriteClaudeFile(_paths.ClaudeConfigFile, config);
    }

    private static ClaudeAuthFile? ReadClaudeFiles(string credentialsPath, string configPath)
    {
        EnsureSafeFile(credentialsPath);
        EnsureSafeFile(configPath);
        JsonObject? credentials;
        JsonObject? config;
        try
        {
            credentials = ReadClaudeObject(credentialsPath, "Claude 인증 파일", MaximumAuthBytes, tolerateInvalid: false);
            config = ReadClaudeObject(configPath, "Claude 설정 파일", MaximumConfigBytes, tolerateInvalid: false);
            if (credentials?["claudeAiOauth"] is not JsonObject oauth) return null;
            return new ClaudeAuthFile
            {
                Oauth = oauth.Deserialize<ClaudeOauthTokens>(JsonOptions) ?? new(),
                Account = (config?["oauthAccount"] as JsonObject)?.Deserialize<ClaudeOauthAccount>(JsonOptions) ?? new(),
            };
        }
        catch (JsonException error)
        {
            throw new AuthenticationRequiredException($"인증 파일 형식이 올바르지 않습니다: {error.Message}");
        }
    }

    private static JsonObject? ReadClaudeObject(string path, string description, long maximumBytes, bool tolerateInvalid)
    {
        if (!File.Exists(path)) return null;
        try
        {
            return JsonNode.Parse(WindowsPathSafety.ReadPrivateFile(path, description, maximumBytes)) as JsonObject
                ?? throw new JsonException($"{description}이 JSON 객체가 아닙니다.");
        }
        catch (JsonException) when (tolerateInvalid)
        {
            return null;
        }
    }

    private static void WriteClaudeFile(string destination, JsonObject contents)
    {
        EnsureSafeFile(destination);
        var temporary = Path.Combine(
            Path.GetDirectoryName(destination)!,
            $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
        WindowsPathSafety.WritePrivateBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(contents, ClaudeFileOptions));
        try
        {
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static void EnsureSafeFile(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            return;
        }

        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.Directory) != 0)
        {
            throw new ClaudeSyncBarException($"인증 경로가 파일이 아닙니다: {path}");
        }

        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new ClaudeSyncBarException($"인증 파일 심볼릭 링크 또는 재분석 지점을 거부했습니다: {path}");
        }
    }

    private static void ValidateFullAuth(ClaudeAuthFile auth)
    {
        if (auth.Oauth is null
            || auth.Account is null
            || string.IsNullOrWhiteSpace(auth.Oauth.AccessToken)
            || string.IsNullOrWhiteSpace(auth.Oauth.RefreshToken)
            || string.IsNullOrWhiteSpace(auth.Account.AccountUuid))
        {
            throw new AuthenticationRequiredException("Claude 전체 인증 정보가 없어 다시 로그인해야 합니다.");
        }
    }

    // "max" with tier "default_claude_max_20x" reads as "Max 20x".
    private static string? DescribePlan(ClaudeOauthTokens tokens)
    {
        if (string.IsNullOrWhiteSpace(tokens.SubscriptionType)) return null;
        var plan = char.ToUpperInvariant(tokens.SubscriptionType[0]) + tokens.SubscriptionType[1..].ToLowerInvariant();
        var multiplier = System.Text.RegularExpressions.Regex.Match(tokens.RateLimitTier ?? string.Empty, "_([0-9]+x)$");
        return multiplier.Success ? plan + " " + multiplier.Groups[1].Value : plan;
    }
}
