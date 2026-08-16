using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace CodexSyncBar.Windows.Core;

public sealed class CursorBridgePreferences
{
    public const int CurrentSchemaVersion = 2;
    public const int DefaultPort = 32_125;

    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    [JsonPropertyName("port")]
    public int Port { get; set; } = DefaultPort;

    [JsonPropertyName("model")]
    public string Model { get; set; } = "auto";

    [JsonPropertyName("agentPath")]
    public string? AgentPath { get; set; }

    [JsonPropertyName("bridgeToken")]
    public string BridgeToken { get; set; } = CreateBridgeToken();

    public CursorBridgePreferences Clone() => new()
    {
        SchemaVersion = SchemaVersion,
        Port = Port,
        Model = Model,
        AgentPath = AgentPath,
        BridgeToken = BridgeToken,
    };

    public CursorBridgePreferences Validate()
    {
        if (SchemaVersion != CurrentSchemaVersion)
        {
            throw new CodexSyncBarException("지원하지 않는 Cursor 브리지 설정 버전입니다.");
        }

        if (Port is < 1_024 or > 65_535)
        {
            throw new CodexSyncBarException("Cursor 브리지 포트는 1024~65535 사이여야 합니다.");
        }

        Model = Model.Trim();
        if (!CursorModelCatalog.IsSafeSlug(Model))
        {
            throw new CodexSyncBarException("Cursor 모델 ID 형식이 올바르지 않습니다.");
        }

        if (!string.IsNullOrWhiteSpace(AgentPath))
        {
            if (!Path.IsPathFullyQualified(AgentPath))
            {
                throw new CodexSyncBarException("Cursor CLI 경로는 절대 경로여야 합니다.");
            }

            if (AgentPath.Any(char.IsControl))
            {
                throw new CodexSyncBarException("Cursor CLI 경로에 제어 문자를 사용할 수 없습니다.");
            }
        }

        if (!Regex.IsMatch(BridgeToken, "^[a-f0-9]{64}$"))
        {
            throw new CodexSyncBarException("Cursor 브리지 인증 token 형식이 올바르지 않습니다.");
        }

        return this;
    }

    private static string CreateBridgeToken() =>
        Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32))
            .ToLowerInvariant();
}

public sealed record CursorBridgeStatus(
    string State,
    string Title,
    string? Detail = null,
    int? ProcessId = null)
{
    public bool IsHealthy => State == "healthy";

    public static CursorBridgeStatus Stopped(string? detail = null) =>
        new("stopped", "중지됨", detail);
}

public sealed class CursorModelVariant
{
    public required string Slug { get; init; }
    public required string DisplayName { get; init; }
    public required string BaseSlug { get; init; }
    public string? Context { get; init; }
    public required string Effort { get; init; }
    public bool Fast { get; init; }
    public bool Thinking { get; init; }
}

public sealed class CursorAcpModelParameters
{
    [JsonPropertyName("model")]
    public required string Model { get; init; }

    [JsonPropertyName("context")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Context { get; init; }

    [JsonPropertyName("effort")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Effort { get; init; }

    [JsonPropertyName("fast")]
    public bool Fast { get; init; }

    [JsonPropertyName("thinking")]
    public bool Thinking { get; init; }
}

public sealed class CursorRouteVariant
{
    [JsonPropertyName("standard")]
    public required string Standard { get; init; }

    [JsonPropertyName("fast")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Fast { get; init; }
}

public sealed class CursorRoute
{
    [JsonPropertyName("default_effort")]
    public required string DefaultEffort { get; init; }

    [JsonPropertyName("variants")]
    public required Dictionary<string, CursorRouteVariant> Variants { get; init; }
}

public sealed record CursorBridgeEnvironmentPayload(
    string AllowedModelsJson,
    string ModelParametersJson,
    string ModelRoutesJson);

public sealed class CursorModelCatalog
{
    private static readonly string[] EffortSuffixes =
    [
        "-extra-high", "-minimal", "-default", "-medium", "-xhigh",
        "-none", "-high", "-low", "-max",
    ];

    private static readonly string[] PreferredEfforts =
        ["default", "medium", "high", "low", "none", "minimal", "xhigh", "max"];

    public CursorModelCatalog(IReadOnlyList<CursorModelVariant> variants)
    {
        Variants = variants
            .GroupBy(item => item.Slug, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
    }

    public IReadOnlyList<CursorModelVariant> Variants { get; }

    public static CursorModelCatalog Parse(string cliOutput)
    {
        var variants = new List<CursorModelVariant>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rawLine in cliOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = Regex.Replace(rawLine.Trim(), "\\x1B\\[[0-?]*[ -/]*[@-~]", string.Empty);
            var separator = line.IndexOf(" - ", StringComparison.Ordinal);
            if (separator <= 0)
            {
                continue;
            }

            var slug = line[..separator].Trim();
            var displayName = line[(separator + 3)..].Trim();
            if (displayName.Length == 0 || !IsSafeSlug(slug) || !seen.Add(slug))
            {
                continue;
            }

            var decomposition = Decompose(slug);
            variants.Add(new CursorModelVariant
            {
                Slug = slug,
                DisplayName = displayName,
                BaseSlug = decomposition.BaseSlug,
                Context = displayName.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                    .Any(token => token.Equals("1m", StringComparison.OrdinalIgnoreCase)) ? "1m" : null,
                Effort = decomposition.Effort,
                Fast = decomposition.Fast,
                Thinking = decomposition.Thinking,
            });
        }

        return new CursorModelCatalog(variants);
    }

    public string SuggestedModel =>
        Variants.FirstOrDefault(item => item.Slug.Equals("auto", StringComparison.OrdinalIgnoreCase))?.Slug
        ?? Variants.FirstOrDefault()?.Slug
        ?? "auto";

    public string? PreferredPickerModelId(string slug)
    {
        var variant = Variants.FirstOrDefault(item => item.Slug == slug);
        if (variant is null)
        {
            return null;
        }

        var modelId = $"syncbar-cursor/{variant.BaseSlug}"
            + (variant.Thinking ? "/thinking" : string.Empty);
        return BuildRoutes().ContainsKey(modelId) ? modelId : null;
    }

    public CursorBridgeEnvironmentPayload BuildBridgeEnvironment()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = null,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false,
        };
        var allowed = JsonSerializer.Serialize(Variants.Select(item => item.Slug), options);
        var parameters = Variants.ToDictionary(
            item => item.Slug,
            item => new CursorAcpModelParameters
            {
                Model = AcpModelId(item.BaseSlug),
                Context = item.Context,
                Effort = item.Effort == "default" ? null : item.Effort,
                Fast = item.Fast,
                Thinking = item.Thinking,
            },
            StringComparer.Ordinal);
        var modelParameters = JsonSerializer.Serialize(parameters, options);
        var routes = BuildRoutes();
        var modelRoutes = JsonSerializer.Serialize(routes, options);
        return new CursorBridgeEnvironmentPayload(allowed, modelParameters, modelRoutes);
    }

    public IReadOnlyDictionary<string, CursorRoute> BuildRoutes()
    {
        var routes = new Dictionary<string, CursorRoute>(StringComparer.Ordinal);
        foreach (var group in Variants.GroupBy(item => (item.BaseSlug, item.Thinking)))
        {
            var familyVariants = group.ToArray();
            var normal = familyVariants.Where(item => !item.Fast).ToArray();
            if (normal.Length == 0)
            {
                continue;
            }

            var fastEfforts = familyVariants
                .Where(item => item.Fast)
                .Select(item => item.Effort)
                .ToHashSet(StringComparer.Ordinal);
            var preferred = PreferredEfforts
                .Select(effort => normal.FirstOrDefault(item =>
                    item.Effort == effort && fastEfforts.Contains(effort)))
                .FirstOrDefault(item => item is not null)
                ?? PreferredEfforts
                    .Select(effort => normal.FirstOrDefault(item => item.Effort == effort))
                    .FirstOrDefault(item => item is not null)
                ?? normal[0];
            var defaultEffort = familyVariants.All(item => item.Effort == "default")
                ? "default"
                : RouteEffort(preferred.Effort);

            var variantMap = new Dictionary<string, CursorRouteVariant>(StringComparer.Ordinal);
            foreach (var effortGroup in familyVariants.GroupBy(
                         item => item.Effort == "default" ? defaultEffort : item.Effort,
                         StringComparer.Ordinal))
            {
                var standardVariants = effortGroup.Where(item => !item.Fast).ToArray();
                var fastVariants = effortGroup.Where(item => item.Fast).ToArray();
                if (standardVariants.Length == 0)
                {
                    continue;
                }

                if (standardVariants.Length > 1 || fastVariants.Length > 1)
                {
                    throw new CodexSyncBarException(
                        $"Cursor 모델 경로가 중복됩니다: {group.Key.BaseSlug}/{group.Key.Thinking}/{effortGroup.Key}");
                }

                variantMap[effortGroup.Key] = new CursorRouteVariant
                {
                    Standard = standardVariants[0].Slug,
                    Fast = fastVariants.FirstOrDefault()?.Slug,
                };
            }

            if (variantMap.Count == 0 || !variantMap.ContainsKey(defaultEffort))
            {
                continue;
            }

            var modelId = $"syncbar-cursor/{group.Key.BaseSlug}"
                + (group.Key.Thinking ? "/thinking" : string.Empty);
            if (IsSafeSlug(modelId))
            {
                routes[modelId] = new CursorRoute
                {
                    DefaultEffort = defaultEffort,
                    Variants = variantMap,
                };
            }
        }

        return routes;
    }

    private static string RouteEffort(string effort) => effort == "default" ? "medium" : effort;

    public static bool IsSafeSlug(string value) =>
        value.Length is > 0 and <= 128
        && Regex.IsMatch(value, "^[A-Za-z0-9][A-Za-z0-9._:/-]{0,127}$");

    public static string AcpModelId(string baseSlug) => baseSlug switch
    {
        "auto" => "default",
        "cursor-grok-4.6" => "grok-4.6",
        "cursor-grok-4.5" => "grok-4.5",
        "claude-4.6-sonnet" => "claude-sonnet-4-6",
        "claude-4.6-opus" => "claude-opus-4-6",
        "claude-4.5-opus" => "claude-opus-4-5",
        "claude-4.5-sonnet" => "claude-sonnet-4-5",
        "claude-4-sonnet" => "claude-sonnet-4",
        _ => baseSlug,
    };

    private static (string BaseSlug, string Effort, bool Fast, bool Thinking) Decompose(string slug)
    {
        var baseSlug = slug;
        string? effort = null;
        var fast = false;
        var thinking = false;
        var changed = true;
        while (changed)
        {
            changed = false;
            if (!fast && RemoveSuffix(ref baseSlug, "-fast"))
            {
                fast = true;
                changed = true;
            }

            if (!thinking && RemoveSuffix(ref baseSlug, "-thinking"))
            {
                thinking = true;
                changed = true;
            }

            if (effort is null)
            {
                foreach (var suffix in EffortSuffixes)
                {
                    if (!RemoveSuffix(ref baseSlug, suffix))
                    {
                        continue;
                    }

                    effort = suffix[1..] switch
                    {
                        "extra-high" => "xhigh",
                        "xhigh" => "xhigh",
                        _ => suffix[1..],
                    };
                    changed = true;
                    break;
                }
            }
        }

        return (baseSlug, effort ?? "default", fast, thinking);
    }

    private static bool RemoveSuffix(ref string value, string suffix)
    {
        if (!value.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        value = value[..^suffix.Length];
        return true;
    }
}

public static class CodexCursorModelCatalogBuilder
{
    private const int MaximumModelCount = 512;
    private const int MaximumBundledCatalogBytes = 8 * 1024 * 1024;
    private const int MaximumGeneratedCatalogBytes = 16 * 1024 * 1024;

    public static void ValidateBundledCatalog(string bundledCatalogJson)
    {
        _ = ParseBundledCatalog(bundledCatalogJson);
    }

    public static byte[] Build(CursorModelCatalog cursorCatalog, string? bundledCatalogJson)
    {
        if (cursorCatalog.Variants.Count == 0)
        {
            throw new CodexSyncBarException("Codex에 표시할 Cursor 모델이 없습니다.");
        }

        JsonObject root;
        JsonArray models;
        if (!string.IsNullOrWhiteSpace(bundledCatalogJson))
        {
            (root, models) = ParseBundledCatalog(bundledCatalogJson);
        }
        else
        {
            root = new JsonObject();
            models = new JsonArray();
            root["models"] = models;
        }

        var routes = cursorCatalog.BuildRoutes();
        if (models.Count + routes.Count > MaximumModelCount)
        {
            throw new CodexSyncBarException(
                $"Codex 모델 수가 안전 한도({MaximumModelCount}개)를 넘었습니다.");
        }

        var template = models.FirstOrDefault()?.DeepClone().AsObject()
            ?? new JsonObject
            {
                ["visibility"] = "list",
                ["supported_in_api"] = true,
                ["input_modalities"] = new JsonArray("text", "image"),
            };
        var used = models
            .Select(model => model!.AsObject()["slug"]!.GetValue<string>())
            .ToHashSet(StringComparer.Ordinal);
        var priority = models
            .Select(model => model!.AsObject()["priority"]?.GetValue<int?>() ?? 0)
            .DefaultIfEmpty(0)
            .Max() + 1;

        foreach (var route in routes)
        {
            if (!used.Add(route.Key))
            {
                throw new CodexSyncBarException(
                    $"Codex 모델 ID가 기존 카탈로그와 충돌합니다: {route.Key}");
            }

            var model = template.DeepClone().AsObject();
            var baseSlug = route.Key["syncbar-cursor/".Length..]
                .Replace("/thinking", string.Empty, StringComparison.Ordinal);
            model["slug"] = route.Key;
            model["display_name"] = $"Cursor · {baseSlug}{(route.Key.EndsWith("/thinking", StringComparison.Ordinal) ? " · Thinking" : string.Empty)}";
            model["description"] = "Cursor 구독을 로컬 Cursor CLI 브리지로 사용하는 모델입니다.";
            model["visibility"] = "list";
            model["supported_in_api"] = true;
            model["priority"] = priority++;
            model["additional_speed_tiers"] = route.Value.Variants.Values.Any(item => item.Fast is not null)
                ? new JsonArray("fast")
                : new JsonArray();
            model["service_tiers"] = new JsonArray();
            model["input_modalities"] = new JsonArray("text", "image");
            model["supports_parallel_tool_calls"] = false;
            model["supports_search_tool"] = false;
            model["experimental_supported_tools"] = new JsonArray();
            model["support_verbosity"] = false;
            model["default_reasoning_level"] = route.Value.DefaultEffort;
            models.Add(model);
        }

        root["models"] = models;
        var options = new JsonSerializerOptions { WriteIndented = true };
        var output = System.Text.Encoding.UTF8.GetBytes(root.ToJsonString(options) + Environment.NewLine);
        if (output.Length > MaximumGeneratedCatalogBytes)
        {
            throw new CodexSyncBarException("Codex 생성 모델 카탈로그가 너무 큽니다.");
        }

        return output;
    }

    private static (JsonObject Root, JsonArray Models) ParseBundledCatalog(string bundledCatalogJson)
    {
        if (System.Text.Encoding.UTF8.GetByteCount(bundledCatalogJson) > MaximumBundledCatalogBytes)
        {
            throw new CodexSyncBarException("Codex 번들 모델 카탈로그가 너무 큽니다.");
        }

        try
        {
            var root = JsonNode.Parse(bundledCatalogJson)?.AsObject()
                ?? throw new InvalidOperationException();
            var models = root["models"]?.AsArray()
                ?? throw new InvalidOperationException();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var node in models)
            {
                if (node is not JsonObject model
                    || model["slug"]?.GetValue<string>() is not { Length: > 0 } slug
                    || !seen.Add(slug))
                {
                    throw new InvalidOperationException("모델 ID가 올바르지 않습니다.");
                }
            }

            return (root, models);
        }
        catch (CodexSyncBarException)
        {
            throw;
        }
        catch (Exception error)
        {
            throw new CodexSyncBarException(
                $"Codex 모델 카탈로그 형식이 올바르지 않습니다: {error.Message}");
        }
    }
}
