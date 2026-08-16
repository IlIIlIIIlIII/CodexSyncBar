using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace CodexSyncBar.Windows.Core;

public sealed class CodexCursorActivationState
{
    public const int CurrentSchemaVersion = 5;

    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    [JsonPropertyName("previousConfigurationExisted")]
    public bool PreviousConfigurationExisted { get; init; }

    [JsonPropertyName("previousModelAssignment")]
    public string? PreviousModelAssignment { get; init; }

    [JsonPropertyName("previousProviderAssignment")]
    public string? PreviousProviderAssignment { get; init; }

    [JsonPropertyName("previousCatalogAssignment")]
    public string? PreviousCatalogAssignment { get; init; }

    [JsonPropertyName("installedModelAssignment")]
    public required string InstalledModelAssignment { get; init; }

    [JsonPropertyName("installedProviderAssignment")]
    public required string InstalledProviderAssignment { get; init; }

    [JsonPropertyName("installedCatalogAssignment")]
    public required string InstalledCatalogAssignment { get; init; }

    [JsonPropertyName("installedCatalogPath")]
    public required string InstalledCatalogPath { get; init; }

    [JsonPropertyName("installedManagedSuffix")]
    public required string InstalledManagedSuffix { get; init; }

    [JsonPropertyName("installedModel")]
    public required string InstalledModel { get; init; }

    [JsonPropertyName("installedPort")]
    public int InstalledPort { get; init; }

    [JsonPropertyName("bridgeToken")]
    public required string BridgeToken { get; init; }

    [JsonPropertyName("sourceSHA256")]
    public required string SourceSha256 { get; init; }

    [JsonPropertyName("installedSHA256")]
    public required string InstalledSha256 { get; init; }
}

public sealed record ActiveCursorProviderConfiguration(
    string Model,
    int Port,
    string BridgeToken,
    string ModelCatalogPath);

internal sealed class CodexConfigTransaction
{
    public const int CurrentSchemaVersion = 1;

    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    [JsonPropertyName("expectedConfigurationExisted")]
    public bool ExpectedConfigurationExisted { get; init; }

    [JsonPropertyName("expectedConfigurationSHA256")]
    public required string ExpectedConfigurationSha256 { get; init; }

    [JsonPropertyName("candidateConfigurationExisted")]
    public bool CandidateConfigurationExisted { get; init; }

    [JsonPropertyName("candidateConfigurationSHA256")]
    public required string CandidateConfigurationSha256 { get; init; }

    [JsonPropertyName("previousActivationStateData")]
    public byte[]? PreviousActivationStateData { get; init; }

    [JsonPropertyName("candidateActivationStateData")]
    public byte[]? CandidateActivationStateData { get; init; }

    public bool Matches(bool configurationExists, byte[] data, bool candidate)
    {
        var expectedExists = candidate
            ? CandidateConfigurationExisted
            : ExpectedConfigurationExisted;
        var expectedHash = candidate
            ? CandidateConfigurationSha256
            : ExpectedConfigurationSha256;
        var normalizedData = configurationExists ? data : [];
        return configurationExists == expectedExists
            && string.Equals(
                CodexConfigService.Sha256(normalizedData),
                expectedHash,
                StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class CodexConfigService
{
    private const string ProviderId = "syncbar_cursor_bridge";
    private const string MarkerBegin = "# BEGIN CODEX SYNCBAR CURSOR BRIDGE v1";
    private const string MarkerEnd = "# END CODEX SYNCBAR CURSOR BRIDGE v1";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private readonly WindowsPaths _paths;

    public CodexConfigService(WindowsPaths paths)
    {
        _paths = paths;
    }

    public string ConfigurationFile => Path.Combine(_paths.CodexHome, "config.toml");

    public bool IsActive()
    {
        EnsureDirectories();
        using var gate = AcquireLock();
        return ReadActiveLocked() is not null;
    }

    public ActiveCursorProviderConfiguration? ActiveConfiguration()
    {
        EnsureDirectories();
        using var gate = AcquireLock();
        return ReadActiveLocked();
    }

    public string? ConfiguredModelCatalogPath()
    {
        EnsureDirectories();
        using var gate = AcquireLock();
        RecoverTransactionIfNeeded();
        var text = ReadConfiguration();
        return ParseTopLevel(text).TryGetValue("model_catalog_json", out var line)
            ? ExtractValue(line.Content)
            : null;
    }

    public CodexCursorActivationState Activate(
        string model,
        int port,
        string bridgeToken,
        string? modelCatalogPath = null)
    {
        var preferences = new CursorBridgePreferences
        {
            Model = model,
            Port = port,
            BridgeToken = bridgeToken,
        }.Validate();
        var catalogPath = NormalizeCatalogPath(
            modelCatalogPath ?? _paths.CursorModelCatalogFile);

        EnsureDirectories();
        using var gate = AcquireLock();
        RecoverTransactionIfNeeded();
        var originalSnapshot = ReadConfigurationSnapshot();
        var configurationExists = originalSnapshot.Exists;
        var originalBytes = originalSnapshot.Data;
        var original = DecodeConfiguration(originalBytes);
        var existingState = ReadState();
        var baseText = original;
        string? previousModel;
        string? previousProvider;
        string? previousCatalog;

        if (existingState is not null)
        {
            ValidateState(existingState);
            var current = ParseTopLevel(baseText);
            EnsureInstalledAssignments(current, existingState);
            if (!baseText.EndsWith(existingState.InstalledManagedSuffix, StringComparison.Ordinal))
            {
                throw new CodexSyncBarException(
                    "Codex 설정이 Cursor 브리지를 켠 뒤 변경되어 자동으로 덮어쓸 수 없습니다.");
            }

            baseText = baseText[..^existingState.InstalledManagedSuffix.Length];
            previousModel = existingState.PreviousModelAssignment;
            previousProvider = existingState.PreviousProviderAssignment;
            previousCatalog = existingState.PreviousCatalogAssignment;
        }
        else
        {
            if (baseText.Contains(MarkerBegin, StringComparison.Ordinal)
                || baseText.Contains(MarkerEnd, StringComparison.Ordinal))
            {
                throw new CodexSyncBarException(
                    "기존 Cursor 브리지 marker가 있어 원복 기록 없이 설정을 변경하지 않았습니다.");
            }

            var current = ParseTopLevel(baseText);
            RejectProviderCollision(baseText);
            previousModel = current.GetValueOrDefault("model")?.Content.Trim();
            previousProvider = current.GetValueOrDefault("model_provider")?.Content.Trim();
            previousCatalog = current.GetValueOrDefault("model_catalog_json")?.Content.Trim();
        }

        var installedModel = $"model = \"{preferences.Model}\"";
        var installedProvider = $"model_provider = \"{ProviderId}\"";
        var installedCatalog = $"model_catalog_json = \"{catalogPath}\"";
        var patchedTop = ReplaceTopLevel(
            baseText,
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["model"] = installedModel,
                ["model_provider"] = installedProvider,
                ["model_catalog_json"] = installedCatalog,
            });
        var managedSuffix = MakeManagedSuffix(
            patchedTop,
            preferences.Port,
            preferences.BridgeToken);
        var candidate = patchedTop + managedSuffix;
        var state = new CodexCursorActivationState
        {
            PreviousConfigurationExisted = existingState?.PreviousConfigurationExisted ?? configurationExists,
            PreviousModelAssignment = previousModel,
            PreviousProviderAssignment = previousProvider,
            PreviousCatalogAssignment = previousCatalog,
            InstalledModelAssignment = installedModel,
            InstalledProviderAssignment = installedProvider,
            InstalledCatalogAssignment = installedCatalog,
            InstalledCatalogPath = catalogPath,
            InstalledManagedSuffix = managedSuffix,
            InstalledModel = preferences.Model,
            InstalledPort = preferences.Port,
            BridgeToken = preferences.BridgeToken,
            SourceSha256 = existingState?.SourceSha256 ?? Sha256(originalBytes),
            InstalledSha256 = Sha256(candidate),
        };

        var previousStateBytes = ReadActivationStateData();
        var candidateBytes = Encoding.UTF8.GetBytes(candidate);
        var candidateStateBytes = JsonSerializer.SerializeToUtf8Bytes(state, JsonOptions);
        var transaction = new CodexConfigTransaction
        {
            ExpectedConfigurationExisted = configurationExists,
            ExpectedConfigurationSha256 = Sha256(originalBytes),
            CandidateConfigurationExisted = true,
            CandidateConfigurationSha256 = Sha256(candidateBytes),
            PreviousActivationStateData = previousStateBytes,
            CandidateActivationStateData = candidateStateBytes,
        };
        WriteTransaction(transaction);
        try
        {
            CompareAndSwapConfiguration(
                configurationExists,
                originalBytes,
                candidateExists: true,
                candidateBytes);
            InstallActivationState(candidateStateBytes);
            RemoveTransaction();
            return state;
        }
        catch
        {
            try
            {
                RecoverTransactionIfNeeded();
                var recovered = ReadConfigurationSnapshot();
                if (transaction.Matches(recovered.Exists, recovered.Data, candidate: true)
                    && BytesEqual(ReadActivationStateData(), candidateStateBytes))
                {
                    return state;
                }
            }
            catch (Exception recoveryError)
            {
                throw new CodexSyncBarException(
                    "Cursor provider 설정 transaction을 복구하지 못했습니다.",
                    recoveryError);
            }

            throw;
        }
    }

    public void Deactivate()
    {
        EnsureDirectories();
        using var gate = AcquireLock();
        RecoverTransactionIfNeeded();
        var state = ReadState()
            ?? throw new CodexSyncBarException("Cursor 브리지의 이전 Codex 모델 원복 기록이 없습니다.");
        ValidateState(state);
        var originalSnapshot = ReadConfigurationSnapshot();
        if (!originalSnapshot.Exists)
        {
            throw new CodexSyncBarException("Cursor provider가 활성 상태인데 Codex 설정 파일이 없습니다.");
        }

        var originalBytes = originalSnapshot.Data;
        var original = DecodeConfiguration(originalBytes);
        var originalStateBytes = ReadActivationStateData()
            ?? throw new CodexSyncBarException("Cursor provider 원복 기록을 읽지 못했습니다.");
        var current = ParseTopLevel(original);
        EnsureInstalledAssignments(current, state);
        if (!original.EndsWith(state.InstalledManagedSuffix, StringComparison.Ordinal))
        {
            throw new CodexSyncBarException(
                "Codex 설정이 Cursor 브리지를 켠 뒤 변경되어 이전 모델을 자동 복구하지 않았습니다.");
        }

        var baseText = original[..^state.InstalledManagedSuffix.Length];
        var restored = ReplaceTopLevel(
            baseText,
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["model"] = state.PreviousModelAssignment,
                ["model_provider"] = state.PreviousProviderAssignment,
                ["model_catalog_json"] = state.PreviousCatalogAssignment,
            });
        var candidateExists = state.PreviousConfigurationExisted || restored.Length > 0;
        var candidateBytes = Encoding.UTF8.GetBytes(restored);
        var transaction = new CodexConfigTransaction
        {
            ExpectedConfigurationExisted = true,
            ExpectedConfigurationSha256 = Sha256(originalBytes),
            CandidateConfigurationExisted = candidateExists,
            CandidateConfigurationSha256 = Sha256(candidateExists ? candidateBytes : []),
            PreviousActivationStateData = originalStateBytes,
            CandidateActivationStateData = null,
        };
        WriteTransaction(transaction);
        try
        {
            CompareAndSwapConfiguration(
                expectedExists: true,
                originalBytes,
                candidateExists,
                candidateBytes);
            InstallActivationState(null);
            RemoveTransaction();
        }
        catch
        {
            try
            {
                RecoverTransactionIfNeeded();
                var recovered = ReadConfigurationSnapshot();
                if (transaction.Matches(recovered.Exists, recovered.Data, candidate: true)
                    && ReadActivationStateData() is null)
                {
                    return;
                }
            }
            catch (Exception recoveryError)
            {
                throw new CodexSyncBarException(
                    "Cursor provider 원복 transaction을 복구하지 못했습니다.",
                    recoveryError);
            }

            throw;
        }
    }

    private ActiveCursorProviderConfiguration? ReadActiveLocked()
    {
        RecoverTransactionIfNeeded();
        var state = ReadState();
        if (state is null)
        {
            return null;
        }

        ValidateState(state);
        var text = ReadConfiguration();
        if (text.Length == 0)
        {
            throw new CodexSyncBarException("Cursor provider 원복 기록은 있지만 Codex 설정이 없습니다.");
        }

        var current = ParseTopLevel(text);
        EnsureInstalledAssignments(current, state);
        if (!text.EndsWith(state.InstalledManagedSuffix, StringComparison.Ordinal))
        {
            throw new CodexSyncBarException(
                "Codex 설정과 Cursor 브리지 원복 기록이 일치하지 않습니다.");
        }

        return new ActiveCursorProviderConfiguration(
            state.InstalledModel,
            state.InstalledPort,
            state.BridgeToken,
            state.InstalledCatalogPath);
    }

    private CodexCursorActivationState? ReadState()
    {
        var bytes = ReadActivationStateData();
        if (bytes is null)
        {
            return null;
        }

        try
        {
            return DeserializeState(bytes);
        }
        catch (JsonException error)
        {
            throw new CodexSyncBarException($"Cursor 브리지 원복 기록을 읽지 못했습니다: {error.Message}");
        }
    }

    private sealed record ConfigurationSnapshot(bool Exists, byte[] Data);

    private byte[]? ReadActivationStateData()
    {
        WindowsPathSafety.EnsureFile(_paths.CursorActivationFile, "Cursor 브리지 원복 기록");
        if (!File.Exists(_paths.CursorActivationFile))
        {
            return null;
        }

        return WindowsPathSafety.ReadPrivateFile(
            _paths.CursorActivationFile,
            "Cursor 브리지 원복 기록",
            16 * 1024 * 1024);
    }

    private ConfigurationSnapshot ReadConfigurationSnapshot()
    {
        WindowsPathSafety.EnsureFile(ConfigurationFile, "Codex 설정 파일");
        if (!File.Exists(ConfigurationFile))
        {
            return new ConfigurationSnapshot(false, []);
        }

        return new ConfigurationSnapshot(
            true,
            WindowsPathSafety.ReadPrivateFile(
                ConfigurationFile,
                "Codex 설정 파일",
                16 * 1024 * 1024));
    }

    private static string DecodeConfiguration(byte[] bytes)
    {
        try
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(bytes);
        }
        catch (DecoderFallbackException error)
        {
            throw new CodexSyncBarException($"Codex 설정 파일이 UTF-8이 아닙니다: {error.Message}");
        }
    }

    private static CodexCursorActivationState DeserializeState(byte[] bytes)
    {
        var state = JsonSerializer.Deserialize<CodexCursorActivationState>(bytes, JsonOptions);
        return state ?? throw new CodexSyncBarException("Cursor 브리지 원복 기록이 비어 있습니다.");
    }

    private void WriteTransaction(CodexConfigTransaction transaction)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(transaction, JsonOptions);
        AtomicWrite(_paths.CursorTransactionFile, bytes);
    }

    private CodexConfigTransaction? ReadTransaction()
    {
        WindowsPathSafety.EnsureFile(_paths.CursorTransactionFile, "Cursor provider 설정 transaction");
        if (!File.Exists(_paths.CursorTransactionFile))
        {
            return null;
        }

        try
        {
            var bytes = WindowsPathSafety.ReadPrivateFile(
                _paths.CursorTransactionFile,
                "Cursor provider 설정 transaction",
                16 * 1024 * 1024);
            var transaction = JsonSerializer.Deserialize<CodexConfigTransaction>(bytes, JsonOptions)
                ?? throw new CodexSyncBarException("Cursor provider 설정 transaction이 비어 있습니다.");
            if (transaction.SchemaVersion != CodexConfigTransaction.CurrentSchemaVersion
                || !Regex.IsMatch(transaction.ExpectedConfigurationSha256, "^[a-f0-9]{64}$")
                || !Regex.IsMatch(transaction.CandidateConfigurationSha256, "^[a-f0-9]{64}$"))
            {
                throw new CodexSyncBarException("Cursor provider 설정 transaction 버전 또는 해시가 올바르지 않습니다.");
            }

            if (transaction.PreviousActivationStateData is not null)
            {
                ValidateState(DeserializeState(transaction.PreviousActivationStateData));
            }

            if (transaction.CandidateActivationStateData is not null)
            {
                ValidateState(DeserializeState(transaction.CandidateActivationStateData));
            }

            return transaction;
        }
        catch (JsonException error)
        {
            throw new CodexSyncBarException(
                $"Cursor provider 설정 transaction을 읽지 못했습니다: {error.Message}");
        }
    }

    private void RemoveTransaction()
    {
        WindowsPathSafety.EnsureFile(_paths.CursorTransactionFile, "Cursor provider 설정 transaction");
        if (File.Exists(_paths.CursorTransactionFile))
        {
            File.Delete(_paths.CursorTransactionFile);
        }
    }

    private void RecoverTransactionIfNeeded()
    {
        var transaction = ReadTransaction();
        if (transaction is null)
        {
            return;
        }

        var live = ReadConfigurationSnapshot();
        if (transaction.Matches(live.Exists, live.Data, candidate: false))
        {
            InstallActivationState(transaction.PreviousActivationStateData);
            RemoveTransaction();
            return;
        }

        if (transaction.Matches(live.Exists, live.Data, candidate: true))
        {
            InstallActivationState(transaction.CandidateActivationStateData);
            RemoveTransaction();
            return;
        }

        throw new CodexSyncBarException(
            "중단된 Cursor provider transaction 이후 Codex 설정이 외부에서 변경되어 자동 복구하지 않았습니다.");
    }

    private void InstallActivationState(byte[]? data)
    {
        WindowsPathSafety.EnsureFile(_paths.CursorActivationFile, "Cursor 브리지 원복 기록");
        if (data is null)
        {
            if (File.Exists(_paths.CursorActivationFile))
            {
                File.Delete(_paths.CursorActivationFile);
            }

            return;
        }

        ValidateState(DeserializeState(data));
        AtomicWrite(_paths.CursorActivationFile, data);
    }

    private void CompareAndSwapConfiguration(
        bool expectedExists,
        byte[] expectedBytes,
        bool candidateExists,
        byte[] candidateBytes)
    {
        var live = ReadConfigurationSnapshot();
        if (live.Exists != expectedExists || !BytesEqual(live.Data, expectedBytes))
        {
            throw new CodexSyncBarException(
                "Codex 설정이 동시에 변경되어 Cursor provider 적용을 중단했습니다.");
        }

        if (candidateExists)
        {
            AtomicWrite(ConfigurationFile, candidateBytes);
        }
        else if (File.Exists(ConfigurationFile))
        {
            WindowsPathSafety.EnsureFile(ConfigurationFile, "Codex 설정 파일");
            File.Delete(ConfigurationFile);
        }

        var installed = ReadConfigurationSnapshot();
        if (installed.Exists != candidateExists || !BytesEqual(installed.Data, candidateBytes))
        {
            throw new CodexSyncBarException("Codex 설정 저장 후 검증에 실패했습니다.");
        }
    }

    private static bool BytesEqual(byte[]? left, byte[]? right)
    {
        if (left is null || right is null)
        {
            return left is null && right is null;
        }

        return left.AsSpan().SequenceEqual(right);
    }

    private static void ValidateState(CodexCursorActivationState state)
    {
        if (state.SchemaVersion != CodexCursorActivationState.CurrentSchemaVersion
            || state.InstalledProviderAssignment != $"model_provider = \"{ProviderId}\""
            || state.InstalledModelAssignment != $"model = \"{state.InstalledModel}\""
            || state.InstalledCatalogAssignment != $"model_catalog_json = \"{state.InstalledCatalogPath}\""
            || !CursorModelCatalog.IsSafeSlug(state.InstalledModel)
            || state.InstalledPort is < 1_024 or > 65_535
            || !Regex.IsMatch(state.BridgeToken, "^[a-f0-9]{64}$")
            || !state.InstalledManagedSuffix.Contains(MarkerBegin, StringComparison.Ordinal)
            || !state.InstalledManagedSuffix.Contains(MarkerEnd, StringComparison.Ordinal))
        {
            throw new CodexSyncBarException("Cursor 브리지 원복 기록이 올바르지 않습니다.");
        }
    }

    private static void EnsureInstalledAssignments(
        IReadOnlyDictionary<string, TomlLine> current,
        CodexCursorActivationState state)
    {
        if (!current.TryGetValue("model", out var model)
            || !current.TryGetValue("model_provider", out var provider)
            || !current.TryGetValue("model_catalog_json", out var catalog)
            || !string.Equals(model.Content.Trim(), state.InstalledModelAssignment, StringComparison.Ordinal)
            || !string.Equals(provider.Content.Trim(), state.InstalledProviderAssignment, StringComparison.Ordinal)
            || !string.Equals(catalog.Content.Trim(), state.InstalledCatalogAssignment, StringComparison.Ordinal))
        {
            throw new CodexSyncBarException(
                "Codex 설정이 Cursor 브리지를 켠 뒤 변경되어 자동으로 덮어쓸 수 없습니다.");
        }
    }

    private static void RejectProviderCollision(string text)
    {
        foreach (var line in ScanLines(text))
        {
            var compact = line.Content.Replace(" ", string.Empty, StringComparison.Ordinal)
                .Replace("\t", string.Empty, StringComparison.Ordinal);
            if (compact.StartsWith($"[model_providers.{ProviderId}]", StringComparison.Ordinal)
                || compact.StartsWith($"[[model_providers.{ProviderId}]]", StringComparison.Ordinal)
                || compact.StartsWith($"model_providers.{ProviderId}.", StringComparison.Ordinal))
            {
                throw new CodexSyncBarException(
                    $"model_providers.{ProviderId}가 이미 정의되어 있어 충돌을 피했습니다.");
            }
        }
    }

    private static Dictionary<string, TomlLine> ParseTopLevel(string text)
    {
        var result = new Dictionary<string, TomlLine>(StringComparer.Ordinal);
        foreach (var line in ScanLines(text))
        {
            var trimmed = line.Content.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
            {
                continue;
            }

            if (trimmed.Contains("\"\"\"", StringComparison.Ordinal)
                || trimmed.Contains("'''", StringComparison.Ordinal))
            {
                throw new CodexSyncBarException(
                    "Codex 최상위 multiline TOML은 안전하게 보존할 수 없어 자동 수정하지 않았습니다.");
            }

            if (trimmed.StartsWith('['))
            {
                break;
            }

            var quotedKey = Regex.IsMatch(
                trimmed,
                "^(?:\\\"|').*(?:\\\"|')\\s*=",
                RegexOptions.CultureInvariant);
            if (quotedKey && (trimmed.Contains("model", StringComparison.Ordinal)
                || trimmed.Contains("provider", StringComparison.Ordinal)
                || trimmed.Contains("catalog", StringComparison.Ordinal)))
            {
                throw new CodexSyncBarException(
                    "Codex 최상위 model 관련 key가 quoted 형식이라 자동 수정하지 않았습니다.");
            }

            var match = Regex.Match(
                trimmed,
                "^(?<key>model|model_provider|model_catalog_json)\\s*=\\s*\"(?<value>[^\"]*)\"\\s*(?:#.*)?$",
                RegexOptions.CultureInvariant);
            if (!match.Success)
            {
                if (Regex.IsMatch(trimmed, "^(model|model_provider|model_catalog_json)\\s*=", RegexOptions.CultureInvariant))
                {
                    throw new CodexSyncBarException(
                        "Codex 최상위 model 관련 값이 안전한 한 줄 문자열이 아닙니다.");
                }

                continue;
            }

            var key = match.Groups["key"].Value;
            if (!result.TryAdd(key, line))
            {
                throw new CodexSyncBarException("Codex 최상위 model 관련 설정이 중복되어 자동 수정하지 않았습니다.");
            }
        }

        return result;
    }

    private static string? ExtractValue(string content)
    {
        var match = Regex.Match(
            content.Trim(),
            "^(?:model|model_provider|model_catalog_json)\\s*=\\s*\"(?<value>[^\"]*)\"",
            RegexOptions.CultureInvariant);
        return match.Success ? match.Groups["value"].Value : null;
    }

    private static string ReplaceTopLevel(
        string text,
        IReadOnlyDictionary<string, string?> values)
    {
        var lines = ScanLines(text).ToList();
        var parsed = ParseTopLevel(text);
        foreach (var (key, value) in values)
        {
            if (parsed.TryGetValue(key, out var current))
            {
                var index = lines.IndexOf(current);
                if (value is null)
                {
                    lines.RemoveAt(index);
                }
                else
                {
                    lines[index] = current with { Content = value };
                }
            }
        }

        var missing = values
            .Where(item => item.Value is not null && !parsed.ContainsKey(item.Key))
            .Select(item => new TomlLine(item.Value!, PreferredNewline(text)))
            .ToArray();
        if (missing.Length > 0)
        {
            var firstTable = lines.FindIndex(item => item.Content.TrimStart().StartsWith('['));
            var insertAt = firstTable >= 0 ? firstTable : lines.Count;
            if (insertAt == lines.Count && lines.Count > 0 && lines[^1].Ending.Length == 0)
            {
                lines[^1] = lines[^1] with { Ending = PreferredNewline(text) };
            }

            lines.InsertRange(insertAt, missing);
        }

        return string.Concat(lines.Select(item => item.Content + item.Ending));
    }

    private static string MakeManagedSuffix(string text, int port, string token)
    {
        var newline = PreferredNewline(text);
        var separator = text.Length == 0
            ? string.Empty
            : text.EndsWith(newline + newline, StringComparison.Ordinal)
                ? string.Empty
                : text.EndsWith(newline, StringComparison.Ordinal) ? newline : newline + newline;
        var block = string.Join(
            newline,
            MarkerBegin,
            $"[model_providers.{ProviderId}]",
            "name = \"Cursor Subscription (local SyncBar bridge)\"",
            $"base_url = \"http://127.0.0.1:{port}/v1\"",
            "wire_api = \"responses\"",
            "requires_openai_auth = true",
            $"http_headers = {{ \"X-SyncBar-Bridge-Token\" = \"{token}\", originator = \"codex_cli_rs\" }}",
            "request_max_retries = 0",
            "stream_max_retries = 0",
            "stream_idle_timeout_ms = 900000",
            MarkerEnd) + newline;
        return separator + block;
    }

    private static string NormalizeCatalogPath(string path)
    {
        var normalized = Path.GetFullPath(path).Replace('\\', '/');
        if (!Path.IsPathFullyQualified(normalized)
            || normalized.Any(character => character is '"' or '\n' or '\r' or '\0'))
        {
            throw new CodexSyncBarException("Codex 모델 카탈로그 경로가 올바른 절대 경로가 아닙니다.");
        }

        return normalized;
    }

    private string ReadConfiguration()
    {
        return DecodeConfiguration(ReadConfigurationSnapshot().Data);
    }

    private FileStream AcquireLock()
    {
        WindowsPathSafety.EnsureFile(_paths.CursorTransactionLockFile, "Cursor 브리지 잠금 파일");
        return new FileStream(
            _paths.CursorTransactionLockFile,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None);
    }

    private void EnsureDirectories()
    {
        _paths.EnsureDirectories();
        Directory.CreateDirectory(_paths.CodexHome);
    }

    private static void AtomicWrite(string path, string content)
    {
        WindowsPathSafety.EnsureFile(path, "원자적 설정 대상 파일");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = Path.Combine(
            Path.GetDirectoryName(path)!,
            $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        File.WriteAllText(temporary, content, new UTF8Encoding(false));
        try
        {
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static void AtomicWrite(string path, byte[] content)
    {
        WindowsPathSafety.EnsureFile(path, "원자적 설정 대상 파일");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = Path.Combine(
            Path.GetDirectoryName(path)!,
            $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        File.WriteAllBytes(temporary, content);
        try
        {
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static string Sha256(string value) => Sha256(Encoding.UTF8.GetBytes(value));

    internal static string Sha256(byte[] value) =>
        Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();

    private static string PreferredNewline(string text) => text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

    private static IReadOnlyList<TomlLine> ScanLines(string text)
    {
        var lines = new List<TomlLine>();
        var start = 0;
        while (start < text.Length)
        {
            var index = start;
            while (index < text.Length && text[index] is not ('\r' or '\n'))
            {
                index++;
            }

            var ending = string.Empty;
            if (index < text.Length)
            {
                if (text[index] == '\r' && index + 1 < text.Length && text[index + 1] == '\n')
                {
                    ending = "\r\n";
                    index += 2;
                }
                else
                {
                    ending = text[index].ToString();
                    index++;
                }
            }

            lines.Add(new TomlLine(text[start..(index - ending.Length)], ending));
            start = index;
        }

        return lines;
    }

    private sealed record TomlLine(string Content, string Ending);
}
