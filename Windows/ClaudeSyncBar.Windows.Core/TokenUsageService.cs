using System.Text.Json;

namespace ClaudeSyncBar.Windows.Core;

public sealed class TokenUsageService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly WindowsPaths _paths;

    public TokenUsageService(WindowsPaths paths)
    {
        _paths = paths;
    }

    public async Task<TokenUsageSnapshot> FetchAsync(
        AppConfiguration configuration,
        SshDeviceService sshDeviceService,
        CancellationToken cancellationToken = default)
    {
        // Other devices first: Claude Desktop keeps a copy of a session it ran on one of them here, and the
        // sessions they counted are left out of this computer's sums instead of being counted twice.
        var devices = new List<DeviceTokenUsage>();
        foreach (var device in configuration.Devices.Where(item => item.Enabled))
        {
            cancellationToken.ThrowIfCancellationRequested();
            devices.Add(await FetchRemoteAsync(device, sshDeviceService, cancellationToken));
        }

        var wsl = new WslDeviceService(_paths, new AuthStore(_paths));
        foreach (var device in new WslConfigurationStore(_paths).Load().Where(item => item.Enabled))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                devices.Add(new DeviceTokenUsage
                {
                    Id = device.Id, DisplayName = device.Distribution, IsReachable = true,
                    Summary = await wsl.FetchTokenUsageAsync(device, cancellationToken),
                });
            }
            catch (Exception error) when (!cancellationToken.IsCancellationRequested)
            {
                devices.Add(new DeviceTokenUsage { Id = device.Id, DisplayName = device.Distribution,
                    Error = error is ClaudeSyncBarException ? Trim(error.Message) : "WSL 사용량을 확인하지 못했습니다." });
            }
        }

        var counted = devices.SelectMany(device => device.Summary?.Sessions ?? []).ToHashSet(StringComparer.Ordinal);
        devices.Insert(0, await FetchLocalAsync(cancellationToken, counted));
        return new TokenUsageSnapshot
        {
            Devices = devices,
            CollectedAt = DateTimeOffset.UtcNow,
        };
    }

    public static DeviceTokenUsageSummary ParseSummary(string output)
    {
        var trimmed = output.Trim();
        var firstObject = trimmed.IndexOf('{');
        var lastObject = trimmed.LastIndexOf('}');
        if (firstObject >= 0 && lastObject > firstObject)
        {
            try
            {
                var summary = JsonSerializer.Deserialize<DeviceTokenUsageSummary>(
                    trimmed[firstObject..(lastObject + 1)],
                    JsonOptions);
                if (summary is not null && IsKnownSchema(summary.SchemaVersion))
                {
                    return summary;
                }
            }
            catch (JsonException)
            {
                // Fall through to the line-oriented parser below.
            }
        }

        foreach (var line in output
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Reverse())
        {
            try
            {
                var summary = JsonSerializer.Deserialize<DeviceTokenUsageSummary>(line, JsonOptions);
                if (summary is not null && IsKnownSchema(summary.SchemaVersion))
                {
                    return summary;
                }
            }
            catch (JsonException)
            {
                // Remote shells may print a harmless warning before the JSON line.
            }
        }

        throw new ClaudeSyncBarException("사용량 집계 helper가 올바른 JSON을 반환하지 않았습니다.");
    }

    // Schema 7 adds the minutes a period is cut from; a schema-6 helper still gives the 30-day totals.
    private static bool IsKnownSchema(int version) => version is 6 or 7;

    /// <summary>Where the in-process summary remembers how far each log was read, apart from the helper's cache.</summary>
    internal string NativeUsageCacheFile => Path.Combine(_paths.ExternalRuntimeDirectory, "usage-cache-native.json");

    internal async Task<DeviceTokenUsage> FetchLocalAsync(CancellationToken cancellationToken, IReadOnlySet<string>? skipSessions = null)
    {
        var node = ResolveNode();
        var helper = ResolveHelper();
        // Windows has no Node.js unless the user installed it: the app reads the logs itself, by the same rules.
        if (OperatingSystem.IsWindows() || node is null || helper is null)
        {
            try
            {
                _paths.EnsureDirectories();
                var summarizer = new TokenUsageSummarizer(Path.Combine(_paths.ClaudeHome, "projects"), NativeUsageCacheFile);
                return new DeviceTokenUsage
                {
                    Id = "windows",
                    DisplayName = "이 Windows PC",
                    IsReachable = true,
                    Summary = await Task.Run(() => summarizer.Summarize(cancellationToken, skipSessions), cancellationToken),
                };
            }
            catch (Exception error) when (error is ClaudeSyncBarException or IOException or UnauthorizedAccessException)
            {
                return new DeviceTokenUsage
                {
                    Id = "windows",
                    DisplayName = "이 Windows PC",
                    Error = Trim(error.Message),
                };
            }
        }

        var skipFile = Path.Combine(_paths.ExternalRuntimeDirectory, "usage-skip-sessions.json");
        try
        {
            _paths.EnsureDirectories();
            List<string> arguments = [helper, Path.Combine(_paths.ClaudeHome, "projects"), _paths.UsageCacheFile];
            if (skipSessions is { Count: > 0 })
            {
                WindowsPathSafety.WritePrivateBytes(skipFile, JsonSerializer.SerializeToUtf8Bytes(skipSessions.Order(StringComparer.Ordinal)));
                arguments.Add(skipFile);
            }

            var result = await ProcessRunner.RunAsync(
                node,
                arguments,
                cancellationToken: cancellationToken,
                timeout: TimeSpan.FromSeconds(45));
            if (result.ExitCode != 0)
            {
                throw new ClaudeSyncBarException(Trim(result.CombinedOutput));
            }

            return new DeviceTokenUsage
            {
                Id = "windows",
                DisplayName = "이 Windows PC",
                IsReachable = true,
                Summary = ParseSummary(result.StandardOutput),
            };
        }
        catch (Exception error) when (error is ClaudeSyncBarException or IOException or UnauthorizedAccessException)
        {
            return new DeviceTokenUsage
            {
                Id = "windows",
                DisplayName = "이 Windows PC",
                Error = Trim(error.Message),
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new DeviceTokenUsage
            {
                Id = "windows",
                DisplayName = "이 Windows PC",
                Error = "로컬 세션 로그 집계 시간이 초과되었습니다.",
            };
        }
        finally
        {
            File.Delete(skipFile);
        }
    }

    private static async Task<DeviceTokenUsage> FetchRemoteAsync(
        SshDeviceConfiguration device,
        SshDeviceService sshDeviceService,
        CancellationToken cancellationToken)
    {
        try
        {
            var summary = await sshDeviceService.FetchTokenUsageAsync(device, cancellationToken);
            return new DeviceTokenUsage
            {
                Id = device.Id,
                DisplayName = device.DisplayLabel,
                IsReachable = true,
                Summary = summary,
            };
        }
        catch (Exception error) when (error is ClaudeSyncBarException or IOException or UnauthorizedAccessException)
        {
            return new DeviceTokenUsage
            {
                Id = device.Id,
                DisplayName = device.DisplayLabel,
                Error = Trim(error.Message),
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new DeviceTokenUsage
            {
                Id = device.Id,
                DisplayName = device.DisplayLabel,
                Error = "원격 세션 로그 집계 시간이 초과되었습니다.",
            };
        }
    }

    private string? ResolveHelper()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Runtime", "usage-summary.mjs"),
            Path.Combine(AppContext.BaseDirectory, "usage-summary.mjs"),
            Path.Combine(Environment.CurrentDirectory, "Support", "usage-summary.mjs"),
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private static string? ResolveNode()
    {
        var bundled = Path.Combine(AppContext.BaseDirectory, "Runtime", "node.exe");
        if (OperatingSystem.IsWindows() && File.Exists(bundled)) return bundled;
        var pathEntries = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        // Under WSL the PATH also lists Windows folders, whose node.exe cannot read Linux paths.
        foreach (var name in OperatingSystem.IsWindows() ? new[] { "node.exe", "node" } : ["node"])
        {
            foreach (var directory in pathEntries)
            {
                var candidate = Path.Combine(directory, name);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private static string Trim(string value)
    {
        var result = value.Trim();
        return result.Length <= 1_024 ? result : result[..1_024];
    }
}
