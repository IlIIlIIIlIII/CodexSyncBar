using System.Security.Cryptography;
using System.Text.Json;
using CodexSyncBar.Windows.Core;

namespace CodexSyncBar.Windows.Core.Tests;

public sealed class CursorAndTokenTests
{
    [Fact]
    public void CursorCatalogBuildsFlatParametersAndPickerRoutes()
    {
        var catalog = CursorModelCatalog.Parse("""
            auto - Auto
            claude-4.5-sonnet-medium - Claude Sonnet Medium
            claude-4.5-sonnet-medium-fast - Claude Sonnet Medium Fast
            claude-4.5-sonnet-thinking-medium - Claude Sonnet Thinking Medium
            """);

        var environment = catalog.BuildBridgeEnvironment();
        using var parameters = JsonDocument.Parse(environment.ModelParametersJson);
        using var routes = JsonDocument.Parse(environment.ModelRoutesJson);

        Assert.Contains(catalog.Variants, item => item.Slug == "auto");
        Assert.True(parameters.RootElement.TryGetProperty("auto", out _));
        Assert.True(routes.RootElement.TryGetProperty("syncbar-cursor/claude-4.5-sonnet", out var route));
        Assert.True(route.GetProperty("variants").TryGetProperty("medium", out var medium));
        Assert.Equal("claude-4.5-sonnet-medium", medium.GetProperty("standard").GetString());
        Assert.Equal("claude-4.5-sonnet-medium-fast", medium.GetProperty("fast").GetString());
        Assert.Equal("syncbar-cursor/claude-4.5-sonnet", catalog.PreferredPickerModelId("claude-4.5-sonnet-medium"));
        Assert.Null(catalog.PreferredPickerModelId("unknown-model"));
    }

    [Fact]
    public void CodexConfigActivationPreservesAndRestoresToml()
    {
        var root = Path.Combine(Path.GetTempPath(), $"codex-syncbar-config-{Guid.NewGuid():N}");
        try
        {
            var paths = new WindowsPaths(
                Path.Combine(root, "home"),
                Path.Combine(root, "local"));
            paths.EnsureDirectories();
            var configPath = Path.Combine(paths.CodexHome, "config.toml");
            var original = "model = \"gpt-5.2\"\r\nmodel_provider = \"openai\"\r\n\r\n[features]\r\nfoo = true\r\n";
            File.WriteAllText(configPath, original);
            var service = new CodexConfigService(paths);

            var state = service.Activate(
                "auto",
                32_125,
                new string('a', 64),
                Path.Combine(paths.StateRoot, "catalog.json"));

            var activated = File.ReadAllText(configPath);
            Assert.Contains("model_provider = \"syncbar_cursor_bridge\"", activated);
            Assert.Contains("[model_providers.syncbar_cursor_bridge]", activated);
            Assert.True(service.IsActive());
            Assert.Equal("auto", service.ActiveConfiguration()!.Model);
            Assert.Equal(state.InstalledManagedSuffix, activated[^state.InstalledManagedSuffix.Length..]);

            service.Deactivate();
            Assert.Equal(original, File.ReadAllText(configPath));
            Assert.False(File.Exists(paths.CursorActivationFile));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void CodexConfigRecoversCandidateAfterInterruptedActivation()
    {
        var root = Path.Combine(Path.GetTempPath(), $"codex-syncbar-config-recovery-{Guid.NewGuid():N}");
        try
        {
            var paths = new WindowsPaths(
                Path.Combine(root, "home"),
                Path.Combine(root, "local"));
            paths.EnsureDirectories();
            var configPath = Path.Combine(paths.CodexHome, "config.toml");
            var original = "model = \"gpt-5.2\"\r\nmodel_provider = \"openai\"\r\n";
            File.WriteAllText(configPath, original);
            var service = new CodexConfigService(paths);
            var state = service.Activate(
                "auto",
                32_125,
                new string('a', 64),
                Path.Combine(paths.StateRoot, "catalog.json"));
            var candidateConfiguration = File.ReadAllBytes(configPath);
            var candidateState = File.ReadAllBytes(paths.CursorActivationFile);
            var originalBytes = System.Text.Encoding.UTF8.GetBytes(original);

            service.Deactivate();
            File.WriteAllBytes(configPath, candidateConfiguration);
            var transaction = new
            {
                schemaVersion = 1,
                expectedConfigurationExisted = true,
                expectedConfigurationSHA256 = Sha256(originalBytes),
                candidateConfigurationExisted = true,
                candidateConfigurationSHA256 = Sha256(candidateConfiguration),
                previousActivationStateData = (byte[]?)null,
                candidateActivationStateData = candidateState,
            };
            File.WriteAllText(
                paths.CursorTransactionFile,
                JsonSerializer.Serialize(transaction));

            var recovered = service.ActiveConfiguration();

            Assert.NotNull(recovered);
            Assert.Equal(state.InstalledModel, recovered!.Model);
            Assert.True(File.Exists(paths.CursorActivationFile));
            Assert.False(File.Exists(paths.CursorTransactionFile));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void TokenSummaryAndPricingMatchSwiftContract()
    {
        const string json = """
            {
              "schemaVersion": 5,
              "generatedAt": "2030-01-01T00:00:00.000Z",
              "scannedFiles": 2,
              "requests": 3,
              "inputTokens": 1000000,
              "cachedInputTokens": 200000,
              "cacheWriteInputTokens": 0,
              "outputTokens": 100000,
              "reasoningOutputTokens": 0,
              "totalTokens": 1100000,
              "buckets": [
                {
                  "model": "gpt-5.2",
                  "serviceTier": "default",
                  "inputTokens": 1000000,
                  "cachedInputTokens": 200000,
                  "cacheWriteInputTokens": 0,
                  "outputTokens": 100000,
                  "reasoningOutputTokens": 0,
                  "totalTokens": 1100000,
                  "requests": 3
                }
              ],
              "errors": []
            }
            """;

        var summary = TokenUsageService.ParseSummary($"warning\n{json}\n");
        var estimate = TokenUsagePricing.EstimateUsd(summary.Buckets.Single());

        Assert.Equal(5, summary.SchemaVersion);
        Assert.Equal(1_100_000, summary.TotalTokens);
        Assert.True(estimate.IsPriced);
        Assert.Equal(2.835m, estimate.PricedUsd);
        Assert.Equal("$2.84", TokenUsageFormatting.Dollars(estimate.PricedUsd));
        Assert.Equal("$1,235", TokenUsageFormatting.Dollars(1234.56m));
    }

    private static string Sha256(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
