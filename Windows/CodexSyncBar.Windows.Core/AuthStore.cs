using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodexSyncBar.Windows.Core;

public sealed class CodexAuthFile
{
    [JsonPropertyName("OPENAI_API_KEY")]
    public string? OpenAiApiKey { get; set; }

    [JsonPropertyName("auth_mode")]
    public string? AuthMode { get; set; }

    [JsonPropertyName("last_refresh")]
    public string? LastRefresh { get; set; }

    [JsonPropertyName("tokens")]
    public CodexTokens Tokens { get; set; } = new();
}

public sealed class CodexTokens
{
    [JsonPropertyName("id_token")]
    public string? IdToken { get; set; }

    [JsonPropertyName("access_token")]
    public string? AccessToken { get; set; }

    [JsonPropertyName("refresh_token")]
    public string? RefreshToken { get; set; }

    [JsonPropertyName("account_id")]
    public string? AccountId { get; set; }
}

public sealed class AuthStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private readonly WindowsPaths _paths;

    public AuthStore(WindowsPaths paths)
    {
        _paths = paths;
    }

    public string ProfileAuthFile(int profileId) => _paths.ProfileAuthFile(profileId);

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

    public CodexAuthFile ReadAuthFile(string path)
    {
        EnsureSafeFile(path);
        if (!File.Exists(path))
        {
            throw new CodexSyncBarException($"인증 파일을 찾을 수 없습니다: {path}");
        }

        try
        {
            var auth = JsonSerializer.Deserialize<CodexAuthFile>(
                           WindowsPathSafety.ReadPrivateFile(
                               path,
                               "Codex 인증 파일",
                               16 * 1024 * 1024),
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
        var accountId = auth.Tokens.AccountId!;
        var token = auth.Tokens.IdToken ?? auth.Tokens.AccessToken!;
        var claims = ReadJwtClaims(token);
        var email = FindEmail(claims) ?? $"계정 {profileId}";
        var expiry = ReadExpiry(auth.Tokens.AccessToken!);
        var activeAccountId = ReadActiveAccountId();

        return new ProfileCredentials(
            profileId,
            auth.Tokens.AccessToken!,
            auth.Tokens.IdToken,
            auth.Tokens.RefreshToken!,
            accountId,
            email,
            expiry,
            path,
            string.Equals(activeAccountId, accountId, StringComparison.Ordinal));
    }

    public string? ReadActiveAccountId()
    {
        EnsureSafeFile(_paths.ActiveAuthFile);
        if (!File.Exists(_paths.ActiveAuthFile))
        {
            return null;
        }

        try
        {
            var auth = JsonSerializer.Deserialize<CodexAuthFile>(
                WindowsPathSafety.ReadPrivateFile(
                    _paths.ActiveAuthFile,
                    "활성 Codex 인증 파일",
                    16 * 1024 * 1024),
                JsonOptions);
            return auth?.Tokens?.AccountId;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public CodexAuthFile? ReadActiveAuth()
    {
        EnsureSafeFile(_paths.ActiveAuthFile);
        return !File.Exists(_paths.ActiveAuthFile)
            ? null
            : ReadAuthFile(_paths.ActiveAuthFile);
    }

    public void RestoreActive(CodexAuthFile? auth)
    {
        EnsureSafeFile(_paths.ActiveAuthFile);
        if (auth is null)
        {
            if (File.Exists(_paths.ActiveAuthFile))
            {
                File.Delete(_paths.ActiveAuthFile);
            }

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
                if (string.Equals(other.Tokens.AccountId, incoming.Tokens.AccountId, StringComparison.Ordinal))
                {
                    throw new CodexSyncBarException(
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
            if (!string.Equals(existing.Tokens.AccountId, incoming.Tokens.AccountId, StringComparison.Ordinal))
            {
                throw new CodexSyncBarException("선택한 프로필과 다른 계정입니다. 재인증이라면 기존 계정 교체를 허용해 주세요.");
            }
        }

        WriteJsonAtomically(destination, incoming);
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

    public CodexAuthFile CreateAccessOnlyCopy(int profileId)
    {
        var auth = ReadAuthFile(ProfileAuthFile(profileId));
        return new CodexAuthFile
        {
            OpenAiApiKey = auth.OpenAiApiKey,
            AuthMode = auth.AuthMode,
            LastRefresh = auth.LastRefresh,
            Tokens = new CodexTokens
            {
                IdToken = auth.Tokens.IdToken,
                AccessToken = auth.Tokens.AccessToken,
                RefreshToken = string.Empty,
                AccountId = auth.Tokens.AccountId,
            },
        };
    }

    private void WriteJsonAtomically(string destination, CodexAuthFile auth)
    {
        EnsureSafeFile(destination);
        if (File.Exists(destination))
        {
            WindowsPathSafety.EnsurePrivateFile(
                destination,
                "Codex 인증 파일",
                16 * 1024 * 1024);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = Path.Combine(
            Path.GetDirectoryName(destination)!,
            $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
        File.WriteAllText(
            temporary,
            JsonSerializer.Serialize(auth, JsonOptions),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        try
        {
            File.Move(temporary, destination, overwrite: true);
            WindowsPathSafety.EnsurePrivateFile(
                destination,
                "Codex 인증 파일",
                16 * 1024 * 1024);
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
            throw new CodexSyncBarException($"인증 경로가 파일이 아닙니다: {path}");
        }

        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new CodexSyncBarException($"인증 파일 심볼릭 링크 또는 재분석 지점을 거부했습니다: {path}");
        }
    }

    private static void ValidateFullAuth(CodexAuthFile auth)
    {
        if (!string.Equals(auth.AuthMode, "chatgpt", StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(auth.Tokens.AccessToken)
            || string.IsNullOrWhiteSpace(auth.Tokens.IdToken)
            || string.IsNullOrWhiteSpace(auth.Tokens.RefreshToken)
            || string.IsNullOrWhiteSpace(auth.Tokens.AccountId))
        {
            throw new AuthenticationRequiredException("ChatGPT 전체 인증 정보가 없어 다시 로그인해야 합니다.");
        }
    }

    private static Dictionary<string, object?> ReadJwtClaims(string token)
    {
        var segments = token.Split('.');
        if (segments.Length < 2)
        {
            return [];
        }

        var encoded = segments[1].Replace('-', '+').Replace('_', '/');
        encoded += new string('=', (4 - encoded.Length % 4) % 4);
        try
        {
            using var document = JsonDocument.Parse(Convert.FromBase64String(encoded));
            return document.RootElement.EnumerateObject()
                .ToDictionary(property => property.Name, property => ToObject(property.Value));
        }
        catch (FormatException)
        {
            return [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static object? ToObject(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number when element.TryGetDouble(out var value) => value,
        JsonValueKind.Object => element.EnumerateObject()
            .ToDictionary(property => property.Name, property => ToObject(property.Value)),
        _ => null,
    };

    private static string? FindEmail(IReadOnlyDictionary<string, object?> claims)
    {
        if (claims.TryGetValue("email", out var value) && value is string email && email.Contains('@'))
        {
            return email;
        }

        if (claims.TryGetValue("https://api.openai.com/profile", out var profile)
            && profile is IReadOnlyDictionary<string, object?> profileValues
            && profileValues.TryGetValue("email", out var profileEmail)
            && profileEmail is string nestedEmail
            && nestedEmail.Contains('@'))
        {
            return nestedEmail;
        }

        return null;
    }

    private static DateTimeOffset? ReadExpiry(string token)
    {
        var claims = ReadJwtClaims(token);
        if (!claims.TryGetValue("exp", out var value))
        {
            return null;
        }

        return value switch
        {
            double seconds => DateTimeOffset.FromUnixTimeSeconds((long)seconds),
            _ => null,
        };
    }
}
