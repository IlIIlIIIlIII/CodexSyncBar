using System.Text;

namespace CodexSyncBar.Windows.Core;

public sealed class CursorProviderService
{
    private readonly WindowsPaths _paths;
    private readonly CursorBridgeService _bridge;
    private readonly CodexConfigService _configuration;

    public CursorProviderService(
        WindowsPaths paths,
        CursorBridgeService bridge,
        CodexConfigService configuration)
    {
        _paths = paths;
        _bridge = bridge;
        _configuration = configuration;
    }

    public CursorBridgeService Bridge => _bridge;

    public CodexConfigService Configuration => _configuration;

    public async Task<CursorBridgeStatus> EnableAsync(
        CursorBridgePreferences proposedPreferences,
        CancellationToken cancellationToken = default)
    {
        var preferences = proposedPreferences.Clone().Validate();
        var catalog = await _bridge.LoadModelCatalogAsync(
            preferences.AgentPath,
            cancellationToken);
        if (!catalog.Variants.Any(item => item.Slug == preferences.Model))
        {
            if (preferences.Model.Equals("auto", StringComparison.OrdinalIgnoreCase))
            {
                preferences.Model = catalog.SuggestedModel;
            }
            else
            {
                throw new CodexSyncBarException(
                    $"현재 Cursor 계정에서 사용할 수 없는 모델입니다: {preferences.Model}");
            }
        }

        var pickerModel = catalog.PreferredPickerModelId(preferences.Model)
            ?? throw new CodexSyncBarException(
                $"선택한 Cursor 모델을 Codex 모델 선택기에 연결할 수 없습니다: {preferences.Model}");

        var previousPreferences = _bridge.LoadPreferences();
        var previousActive = _configuration.ActiveConfiguration();
        WindowsPathSafety.EnsureFile(_paths.CursorModelCatalogFile, "Cursor 모델 카탈로그");
        WindowsPathSafety.EnsureFile(_paths.CursorModelCatalogBackupFile, "Cursor 모델 카탈로그 백업");
        var previousCatalogBytes = WindowsPathSafety.ReadPrivateFile(
            _paths.CursorModelCatalogFile,
            "Cursor 모델 카탈로그",
            16 * 1024 * 1024);
        var previousCatalog = previousCatalogBytes.Length == 0 ? null : previousCatalogBytes;
        var createdCatalogBackup = false;
        if (previousActive is null && previousCatalog is not null
            && !File.Exists(_paths.CursorModelCatalogBackupFile))
        {
            AtomicWrite(_paths.CursorModelCatalogBackupFile, previousCatalog);
            createdCatalogBackup = true;
        }
        var bundledCatalog = LoadBundledCatalog();
        var generatedCatalog = CodexCursorModelCatalogBuilder.Build(catalog, bundledCatalog);
        AtomicWrite(_paths.CursorModelCatalogFile, generatedCatalog);

        var configurationActivated = false;
        try
        {
            _configuration.Activate(
                pickerModel,
                preferences.Port,
                preferences.BridgeToken,
                _paths.CursorModelCatalogFile);
            configurationActivated = true;
            _bridge.SavePreferences(preferences);
            var status = await _bridge.StartAsync(
                preferences,
                forceRestart: true,
                cancellationToken);
            if (!status.IsHealthy)
            {
                throw new CodexSyncBarException(status.Detail ?? status.Title);
            }

            return status;
        }
        catch
        {
            if (configurationActivated)
            {
                try
                {
                    _configuration.Deactivate();
                }
                catch
                {
                    // Preserve the original startup/configuration error.
                }
            }

            if (previousActive is not null)
            {
                try
                {
                    _configuration.Activate(
                        previousActive.Model,
                        previousActive.Port,
                        previousActive.BridgeToken,
                        previousActive.ModelCatalogPath);
                    _bridge.SavePreferences(previousPreferences);
                    await _bridge.StartAsync(
                        previousPreferences,
                        forceRestart: true,
                        cancellationToken);
                }
                catch
                {
                    // Preserve the original error; the configuration service
                    // has already failed closed if the old provider cannot be
                    // restored.
                }
            }

            if (previousCatalog is null)
            {
                if (File.Exists(_paths.CursorModelCatalogFile))
                {
                    File.Delete(_paths.CursorModelCatalogFile);
                }
            }
            else
            {
                AtomicWrite(_paths.CursorModelCatalogFile, previousCatalog);
            }

            if (createdCatalogBackup && File.Exists(_paths.CursorModelCatalogBackupFile))
            {
                File.Delete(_paths.CursorModelCatalogBackupFile);
            }

            throw;
        }
    }

    public async Task DisableAsync(CancellationToken cancellationToken = default)
    {
        var previousActive = _configuration.ActiveConfiguration()
            ?? throw new CodexSyncBarException("Cursor provider가 활성 상태가 아닙니다.");
        var previousPreferences = _bridge.LoadPreferences();
        WindowsPathSafety.EnsureFile(_paths.CursorModelCatalogFile, "Cursor 모델 카탈로그");
        WindowsPathSafety.EnsureFile(_paths.CursorModelCatalogBackupFile, "Cursor 모델 카탈로그 백업");
        var previousCatalogBytes = WindowsPathSafety.ReadPrivateFile(
            _paths.CursorModelCatalogFile,
            "Cursor 모델 카탈로그",
            16 * 1024 * 1024);
        var previousCatalog = previousCatalogBytes.Length == 0 ? null : previousCatalogBytes;
        try
        {
            await _bridge.StopAsync(cancellationToken);
            _configuration.Deactivate();
            RestoreCatalogAfterDisable();
        }
        catch
        {
            try
            {
                if (previousCatalog is not null)
                {
                    AtomicWrite(_paths.CursorModelCatalogFile, previousCatalog);
                }

                if (!_configuration.IsActive())
                {
                    _configuration.Activate(
                        previousActive.Model,
                        previousActive.Port,
                        previousActive.BridgeToken,
                        previousActive.ModelCatalogPath);
                }

                _bridge.SavePreferences(previousPreferences);
                await _bridge.StartAsync(
                    previousPreferences,
                    forceRestart: true,
                    CancellationToken.None);
            }
            catch
            {
                // Preserve the original failure. The configuration service
                // refuses to overwrite a changed file, so a failed recovery
                // remains visible for explicit repair.
            }

            throw;
        }
    }

    private void RestoreCatalogAfterDisable()
    {
        if (File.Exists(_paths.CursorModelCatalogBackupFile))
        {
            AtomicWrite(
                _paths.CursorModelCatalogFile,
                WindowsPathSafety.ReadPrivateFile(
                    _paths.CursorModelCatalogBackupFile,
                    "Cursor 모델 카탈로그 백업",
                    16 * 1024 * 1024));
            File.Delete(_paths.CursorModelCatalogBackupFile);
        }
        else if (File.Exists(_paths.CursorModelCatalogFile))
        {
            File.Delete(_paths.CursorModelCatalogFile);
        }
    }

    private string? LoadBundledCatalog()
    {
        var cache = Path.Combine(_paths.CodexHome, "models_cache.json");
        var contents = WindowsPathSafety.ReadPrivateFile(
            cache,
            "Codex 모델 캐시",
            8 * 1024 * 1024);
        if (contents.Length == 0)
        {
            return null;
        }

        var json = Encoding.UTF8.GetString(contents);
        CodexCursorModelCatalogBuilder.ValidateBundledCatalog(json);
        return json;
    }

    private static void AtomicWrite(string path, byte[] contents)
    {
        WindowsPathSafety.EnsureFile(path, "Cursor 모델 카탈로그 파일");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = Path.Combine(
            Path.GetDirectoryName(path)!,
            $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        File.WriteAllBytes(temporary, contents);
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
}
