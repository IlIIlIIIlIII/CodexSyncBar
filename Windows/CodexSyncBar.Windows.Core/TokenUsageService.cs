using System.Text.Json;

namespace CodexSyncBar.Windows.Core;

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
        var devices = new List<DeviceTokenUsage>();
        devices.Add(await FetchLocalAsync(cancellationToken));

        foreach (var device in configuration.Devices.Where(item => item.Enabled))
        {
            cancellationToken.ThrowIfCancellationRequested();
            devices.Add(await FetchRemoteAsync(device, sshDeviceService, cancellationToken));
        }

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
                if (summary is not null && summary.SchemaVersion > 0)
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
                if (summary is not null && summary.SchemaVersion > 0)
                {
                    return summary;
                }
            }
            catch (JsonException)
            {
                // Remote shells may print a harmless warning before the JSON line.
            }
        }

        throw new CodexSyncBarException("사용량 집계 helper가 올바른 JSON을 반환하지 않았습니다.");
    }

    private async Task<DeviceTokenUsage> FetchLocalAsync(CancellationToken cancellationToken)
    {
        var node = ResolveNode();
        if (node is null)
        {
            return new DeviceTokenUsage
            {
                Id = "windows",
                DisplayName = "이 Windows PC",
                Error = "Node.js를 찾지 못했습니다.",
            };
        }

        var helper = ResolveHelper();
        if (helper is null)
        {
            return new DeviceTokenUsage
            {
                Id = "windows",
                DisplayName = "이 Windows PC",
                Error = "usage-summary.mjs가 앱 Runtime 폴더에 없습니다.",
            };
        }

        try
        {
            _paths.EnsureDirectories();
            var result = await ProcessRunner.RunAsync(
                node,
                [helper, _paths.CodexHome + "\\sessions", _paths.UsageCacheFile],
                cancellationToken: cancellationToken,
                timeout: TimeSpan.FromSeconds(45));
            if (result.ExitCode != 0)
            {
                throw new CodexSyncBarException(Trim(result.CombinedOutput));
            }

            return new DeviceTokenUsage
            {
                Id = "windows",
                DisplayName = "이 Windows PC",
                IsReachable = true,
                Summary = ParseSummary(result.StandardOutput),
            };
        }
        catch (Exception error) when (error is CodexSyncBarException or IOException or UnauthorizedAccessException)
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
        catch (Exception error) when (error is CodexSyncBarException or IOException or UnauthorizedAccessException)
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
        var pathEntries = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var name in new[] { "node.exe", "node" })
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
