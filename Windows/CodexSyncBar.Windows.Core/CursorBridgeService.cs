using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace CodexSyncBar.Windows.Core;

public sealed class CursorBridgeService
{
    private readonly WindowsPaths _paths;
    private readonly HttpClient _httpClient = new();
    private readonly object _processGate = new();
    private readonly StringBuilder _stderr = new();
    private Process? _process;
    private CursorBridgePreferences? _activePreferences;

    public CursorBridgeService(WindowsPaths paths)
    {
        _paths = paths;
        _httpClient.Timeout = TimeSpan.FromSeconds(2);
    }

    public CursorBridgeStatus Status { get; private set; } = CursorBridgeStatus.Stopped();

    public Action<CursorBridgeStatus>? OnUnexpectedStatusChange { get; set; }

    public CursorBridgePreferences LoadPreferences()
    {
        _paths.EnsureDirectories();
        WindowsPathSafety.EnsureFile(_paths.CursorBridgePreferencesFile, "Cursor 브리지 설정 파일");
        if (!File.Exists(_paths.CursorBridgePreferencesFile))
        {
            return new CursorBridgePreferences().Validate();
        }

        try
        {
            var preferences = JsonSerializer.Deserialize<CursorBridgePreferences>(
                File.ReadAllText(_paths.CursorBridgePreferencesFile))
                ?? throw new CodexSyncBarException("Cursor 브리지 설정이 비어 있습니다.");
            return preferences.Validate();
        }
        catch (JsonException error)
        {
            throw new CodexSyncBarException($"Cursor 브리지 설정을 읽지 못했습니다: {error.Message}");
        }
    }

    public void SavePreferences(CursorBridgePreferences preferences)
    {
        var validated = preferences.Validate();
        _paths.EnsureDirectories();
        WindowsPathSafety.EnsureFile(_paths.CursorBridgePreferencesFile, "Cursor 브리지 설정 파일");
        var temporary = Path.Combine(
            _paths.StateRoot,
            $".cursor-bridge.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(
                temporary,
                JsonSerializer.Serialize(validated, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, _paths.CursorBridgePreferencesFile, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    public async Task<CursorModelCatalog> LoadModelCatalogAsync(
        string? preferredAgentPath = null,
        CancellationToken cancellationToken = default)
    {
        var agent = ResolveAgent(preferredAgentPath)
            ?? throw new CodexSyncBarException(
                "Cursor CLI를 찾지 못했습니다. Cursor를 설치하고 cursor-agent를 PATH에 추가해 주세요.");
        var result = await RunAgentAsync(
            agent,
            ["--list-models"],
            cancellationToken,
            TimeSpan.FromSeconds(30));
        if (result.ExitCode != 0)
        {
            throw new CodexSyncBarException(
                $"Cursor 모델 목록을 가져오지 못했습니다: {TrimError(result.CombinedOutput)}");
        }

        if (result.StandardOutput.Length > 256 * 1024)
        {
            throw new CodexSyncBarException("Cursor 모델 목록이 허용 크기를 초과했습니다.");
        }

        var catalog = CursorModelCatalog.Parse(result.StandardOutput);
        if (catalog.Variants.Count == 0)
        {
            throw new CodexSyncBarException("Cursor CLI가 사용 가능한 모델을 반환하지 않았습니다.");
        }

        if (catalog.Variants.Count > 512)
        {
            throw new CodexSyncBarException("Cursor 모델 수가 안전 한도(512개)를 초과했습니다.");
        }

        return catalog;
    }

    public async Task<CursorBridgeStatus> StartAsync(
        CursorBridgePreferences proposedPreferences,
        bool forceRestart = false,
        CancellationToken cancellationToken = default)
    {
        CursorBridgePreferences preferences;
        try
        {
            preferences = proposedPreferences.Clone().Validate();
        }
        catch (Exception error)
        {
            Status = new CursorBridgeStatus("failed", "오류", error.Message);
            return Status;
        }

        if (!forceRestart
            && _process is { HasExited: false }
            && _activePreferences is not null
            && SamePreferences(_activePreferences, preferences)
            && await IsHealthyAsync(preferences, _process.Id, cancellationToken))
        {
            Status = new CursorBridgeStatus("healthy", "연결됨", null, _process.Id);
            return Status;
        }

        await StopAsync(cancellationToken);
        Status = new CursorBridgeStatus("starting", "시작 중…");

        var node = ResolveNode();
        if (node is null)
        {
            Status = new CursorBridgeStatus("missing-node", "Node.js 필요", "Node.js 18 이상을 설치해 주세요.");
            return Status;
        }

        var agent = ResolveAgent(preferences.AgentPath);
        if (agent is null)
        {
            Status = new CursorBridgeStatus("missing-agent", "Cursor CLI 필요", "Cursor CLI 또는 cursor-agent를 찾지 못했습니다.");
            return Status;
        }

        try
        {
            var auth = await RunAgentAsync(
                agent,
                ["status"],
                cancellationToken,
                TimeSpan.FromSeconds(12));
            if (auth.ExitCode != 0)
            {
                Status = new CursorBridgeStatus(
                    "unauthenticated",
                    "Cursor 로그인 필요",
                    TrimError(auth.CombinedOutput));
                return Status;
            }

            var catalog = await LoadModelCatalogAsync(agent, cancellationToken);
            if (!catalog.Variants.Any(item => item.Slug == preferences.Model))
            {
                if (preferences.Model.Equals("auto", StringComparison.OrdinalIgnoreCase))
                {
                    preferences.Model = catalog.SuggestedModel;
                    SavePreferences(preferences);
                }
                else
                {
                    Status = new CursorBridgeStatus(
                        "failed",
                        "모델을 찾을 수 없음",
                        $"현재 Cursor 계정에서 사용할 수 없는 모델입니다: {preferences.Model}");
                    return Status;
                }
            }

            var helper = ResolveHelper();
            if (helper is null)
            {
                Status = new CursorBridgeStatus(
                    "failed",
                    "브리지 helper 없음",
                    "cursor-codex-bridge.mjs가 앱 Runtime 폴더에 없습니다.");
                return Status;
            }

            var environment = catalog.BuildBridgeEnvironment();
            var startInfo = new ProcessStartInfo
            {
                FileName = node,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = GetBridgeWorkspace(),
            };
            startInfo.ArgumentList.Add(helper);
            startInfo.ArgumentList.Add("--host");
            startInfo.ArgumentList.Add("127.0.0.1");
            startInfo.ArgumentList.Add("--port");
            startInfo.ArgumentList.Add(preferences.Port.ToString());
            startInfo.ArgumentList.Add("--agent");
            startInfo.ArgumentList.Add(agent);
            startInfo.ArgumentList.Add("--model");
            startInfo.ArgumentList.Add(preferences.Model);
            startInfo.ArgumentList.Add("--workspace");
            startInfo.ArgumentList.Add(GetBridgeWorkspace());
            startInfo.ArgumentList.Add("--parent-pid");
            startInfo.ArgumentList.Add(Environment.ProcessId.ToString());
            startInfo.Environment["SYNCBAR_CURSOR_BRIDGE_TOKEN"] = preferences.BridgeToken;
            startInfo.Environment["SYNCBAR_CURSOR_MODELS_JSON"] = environment.AllowedModelsJson;
            startInfo.Environment["SYNCBAR_CURSOR_MODEL_PARAMETERS_JSON"] = environment.ModelParametersJson;
            startInfo.Environment["SYNCBAR_CURSOR_MODEL_ROUTES_JSON"] = environment.ModelRoutesJson;

            var nativeModels = ReadNativeModelSlugs();
            if (nativeModels.Count > 0)
            {
                startInfo.Environment["SYNCBAR_NATIVE_MODELS_JSON"] = JsonSerializer.Serialize(nativeModels);
            }
            else
            {
                startInfo.Environment.Remove("SYNCBAR_NATIVE_MODELS_JSON");
            }

            var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            lock (_processGate)
            {
                _stderr.Clear();
                _process = process;
                _activePreferences = preferences.Clone();
            }

            process.ErrorDataReceived += (_, args) =>
            {
                if (!string.IsNullOrWhiteSpace(args.Data))
                {
                    lock (_processGate)
                    {
                        if (_stderr.Length < 8_192)
                        {
                            _stderr.AppendLine(args.Data);
                        }
                    }
                }
            };
            process.OutputDataReceived += (_, _) => { };
            process.Exited += (_, _) =>
            {
                CursorBridgeStatus? unexpected = null;
                lock (_processGate)
                {
                    if (ReferenceEquals(_process, process)
                        && Status.State == "healthy")
                    {
                        unexpected = new CursorBridgeStatus(
                            "failed",
                            "오류",
                            "Cursor 브리지 프로세스가 종료되었습니다.");
                        _process = null;
                        _activePreferences = null;
                    }
                }

                if (unexpected is not null)
                {
                    Status = unexpected;
                    OnUnexpectedStatusChange?.Invoke(unexpected);
                }
            };

            if (!process.Start())
            {
                throw new CodexSyncBarException("Cursor 브리지를 시작하지 못했습니다.");
            }

            process.BeginErrorReadLine();
            process.BeginOutputReadLine();
            for (var attempt = 0; attempt < 60; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (process.HasExited)
                {
                    throw new CodexSyncBarException(
                        $"Cursor 브리지가 시작 직후 종료되었습니다: {ReadStderr()}");
                }

                if (await IsHealthyAsync(preferences, process.Id, cancellationToken))
                {
                    Status = new CursorBridgeStatus("healthy", "연결됨", null, process.Id);
                    SavePreferences(preferences);
                    return Status;
                }

                await Task.Delay(100, cancellationToken);
            }

            throw new CodexSyncBarException(
                $"Cursor 브리지 health check가 시간 안에 완료되지 않았습니다: {ReadStderr()}");
        }
        catch (OperationCanceledException)
        {
            await StopAsync(CancellationToken.None);
            Status = CursorBridgeStatus.Stopped("Cursor 브리지 시작이 취소되었습니다.");
            throw;
        }
        catch (Exception error)
        {
            await StopAsync(CancellationToken.None);
            var message = error.Message;
            if (message.Contains("EADDRINUSE", StringComparison.OrdinalIgnoreCase))
            {
                Status = new CursorBridgeStatus("port-conflict", "포트 사용 중", message);
            }
            else
            {
                Status = new CursorBridgeStatus("failed", "오류", message);
            }

            return Status;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        Process? process;
        lock (_processGate)
        {
            process = _process;
            _process = null;
            _activePreferences = null;
        }

        if (process is not null)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(cancellationToken);
                }
            }
            catch (InvalidOperationException)
            {
            }
            finally
            {
                process.Dispose();
            }
        }

        Status = CursorBridgeStatus.Stopped();
    }

    public string? ResolveAgent(string? preferredPath = null)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(preferredPath))
        {
            candidates.Add(preferredPath);
        }

        candidates.AddRange(
        [
            "cursor-agent.exe", "cursor-agent.cmd", "agent.exe", "agent.cmd",
            Path.Combine(_paths.Home, ".cursor", "bin", "cursor-agent.exe"),
            Path.Combine(_paths.Home, ".cursor", "bin", "cursor-agent.cmd"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "cursor", "resources", "app", "bin", "cursor-agent.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "cursor", "resources", "app", "bin", "agent.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Cursor", "resources", "app", "bin", "cursor-agent.exe"),
        ]);
        return FindExecutable(candidates);
    }

    public string? ResolveNode()
    {
        var candidates = new[]
        {
            "node.exe",
            "node",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs", "node.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "nodejs", "node.exe"),
        };
        return FindExecutable(candidates);
    }

    private async Task<ProcessResult> RunAgentAsync(
        string agent,
        IEnumerable<string> arguments,
        CancellationToken cancellationToken,
        TimeSpan timeout)
    {
        return await ProcessRunner.RunAsync(
            agent,
            arguments,
            cancellationToken: cancellationToken,
            timeout: timeout,
            environment: new Dictionary<string, string?>
            {
                ["NO_COLOR"] = "1",
                ["TERM"] = "dumb",
            });
    }

    private async Task<bool> IsHealthyAsync(
        CursorBridgePreferences preferences,
        int expectedPid,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"http://127.0.0.1:{preferences.Port}/healthz");
        request.Headers.TryAddWithoutValidation("X-SyncBar-Bridge-Token", preferences.BridgeToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(500));
        try
        {
            using var response = await _httpClient.SendAsync(request, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(timeout.Token));
            var root = document.RootElement;
            return root.TryGetProperty("status", out var status)
                && status.GetString() == "ok"
                && root.TryGetProperty("protocol", out var protocol)
                && protocol.GetString() == "responses"
                && root.TryGetProperty("model", out var model)
                && model.GetString() == preferences.Model
                && root.TryGetProperty("pid", out var pid)
                && pid.TryGetInt32(out var actualPid)
                && actualPid == expectedPid;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception error) when (error is HttpRequestException or JsonException)
        {
            return false;
        }
    }

    private string GetBridgeWorkspace()
    {
        var workspace = Path.Combine(_paths.CursorBridgeDirectory, "workspace");
        WindowsPathSafety.EnsureDirectory(workspace, "Cursor 브리지 작업 디렉터리");
        return workspace;
    }

    private string? ResolveHelper()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Runtime", "cursor-codex-bridge.mjs"),
            Path.Combine(AppContext.BaseDirectory, "cursor-codex-bridge.mjs"),
            Path.Combine(Environment.CurrentDirectory, "Support", "cursor-codex-bridge.mjs"),
        };
        foreach (var candidate in candidates)
        {
            if (!File.Exists(candidate))
            {
                continue;
            }

            try
            {
                WindowsPathSafety.EnsureFile(candidate, "Cursor 브리지 helper");
                if ((File.GetAttributes(candidate) & FileAttributes.Directory) == 0)
                {
                    return candidate;
                }
            }
            catch (CodexSyncBarException)
            {
            }
        }

        return null;
    }

    private IReadOnlyList<string> ReadNativeModelSlugs()
    {
        try
        {
            var contents = WindowsPathSafety.ReadPrivateFile(
                _paths.CursorModelCatalogFile,
                "Codex 모델 카탈로그",
                16 * 1024 * 1024);
            if (contents.Length == 0)
            {
                return [];
            }

            using var document = JsonDocument.Parse(contents);
            if (!document.RootElement.TryGetProperty("models", out var models)
                || models.ValueKind != JsonValueKind.Array)
            {
                throw new CodexSyncBarException("Codex 모델 카탈로그 형식이 올바르지 않습니다.");
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            var slugs = new List<string>();
            foreach (var item in models.EnumerateArray())
            {
                if (!item.TryGetProperty("slug", out var slugElement)
                    || slugElement.ValueKind != JsonValueKind.String
                    || string.IsNullOrEmpty(slugElement.GetString()))
                {
                    throw new CodexSyncBarException(
                        "Codex 모델 카탈로그에 ID가 없는 항목이 있습니다.");
                }

                var slug = slugElement.GetString()!;
                if (slug.StartsWith("syncbar-cursor/", StringComparison.Ordinal))
                {
                    continue;
                }

                if (!CursorModelCatalog.IsSafeSlug(slug) || !seen.Add(slug))
                {
                    throw new CodexSyncBarException(
                        $"Codex 기본 모델 ID가 올바르지 않습니다: {slug}");
                }

                slugs.Add(slug);
            }

            if (slugs.Count > 512)
            {
                throw new CodexSyncBarException(
                    "Codex 기본 모델 수가 안전 한도를 초과했습니다.");
            }

            return slugs;
        }
        catch (JsonException)
        {
            throw new CodexSyncBarException("Codex 모델 카탈로그 형식이 올바르지 않습니다.");
        }
    }

    private static string? FindExecutable(IEnumerable<string> candidates)
    {
        var pathEntries = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var candidate in candidates)
        {
            if (Path.IsPathFullyQualified(candidate) && File.Exists(candidate))
            {
                if (IsSafeExecutable(candidate))
                {
                    return candidate;
                }

                continue;
            }

            if (!Path.IsPathFullyQualified(candidate))
            {
                foreach (var directory in pathEntries)
                {
                    var path = Path.Combine(directory, candidate);
                    if (File.Exists(path) && IsSafeExecutable(path))
                    {
                        return path;
                    }
                }
            }
        }

        return null;
    }

    private static bool IsSafeExecutable(string path)
    {
        try
        {
            WindowsPathSafety.EnsureFile(path, "실행 파일");
            return (File.GetAttributes(path) & FileAttributes.Directory) == 0;
        }
        catch (CodexSyncBarException)
        {
            return false;
        }
    }

    private string ReadStderr()
    {
        lock (_processGate)
        {
            return TrimError(_stderr.ToString());
        }
    }

    private static string TrimError(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length <= 1_024 ? trimmed : trimmed[..1_024];
    }

    private static bool SamePreferences(CursorBridgePreferences first, CursorBridgePreferences second) =>
        first.Port == second.Port
        && first.Model == second.Model
        && first.AgentPath == second.AgentPath
        && first.BridgeToken == second.BridgeToken;
}
