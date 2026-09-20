namespace CodexSyncBar.Windows.Core;

public enum WindowsReadmeScreen
{
    Popover,
    Settings,
    Tray,
}

public sealed record WindowsLaunchOptions(
    bool PreviewWindow,
    int? LoginProfileId,
    WindowsReadmeScreen? ReadmeScreen,
    string? ReadmeOutput,
    bool Background = false,
    bool Demo = false,
    bool Settings = false,
    string? DemoTheme = null)
{
    // Match the macOS launch order: an explicit login profile takes priority
    // over the optional preview window, while README capture remains its own
    // mutually exclusive launch mode.
    public bool UsesReadmePage => Demo || ReadmeScreen is not null
        || (PreviewWindow && LoginProfileId is null);

    public static WindowsLaunchOptions Parse(IReadOnlyList<string> arguments)
    {
        var themeArguments = arguments.Where(a => a.StartsWith("--demo-theme=", StringComparison.Ordinal)).ToArray();
        var theme = themeArguments.Length == 0 ? null : themeArguments[0]["--demo-theme=".Length..];
        if (themeArguments.Length > 1 || (theme is not null && theme is not ("light" or "dark")))
            throw new InvalidOperationException("--demo-theme은 light 또는 dark여야 합니다.");
        if (theme is not null && !arguments.Contains("--demo") && !arguments.Any(a => a.StartsWith("--readme-demo=")))
            throw new InvalidOperationException("--demo-theme은 데모 모드에서만 사용할 수 있습니다.");
        var background = arguments.Contains("--background");
        var demo = arguments.Contains("--demo");
        var settings = arguments.Contains("--settings")
            || arguments.Any(argument => argument.StartsWith("codexsyncbar://settings", StringComparison.OrdinalIgnoreCase));
        if (background && settings)
            throw new InvalidOperationException("--background와 --settings를 함께 사용할 수 없습니다.");
        if (demo && arguments.Any(argument => argument.StartsWith("--login-profile", StringComparison.Ordinal)))
            throw new InvalidOperationException("데모 모드에서는 실제 계정 로그인을 시작할 수 없습니다.");
        foreach (var flag in new[] { "--background", "--demo", "--settings" })
            if (arguments.Count(argument => argument == flag) > 1)
                throw new InvalidOperationException($"{flag}은 한 번만 지정할 수 있습니다.");

        var previewCount = arguments.Count(argument =>
            string.Equals(argument, "--preview-window", StringComparison.Ordinal));
        if (previewCount > 1)
        {
            throw new InvalidOperationException("--preview-window은 한 번만 지정할 수 있습니다.");
        }

        var demoValues = arguments
            .Where(argument => argument.StartsWith("--readme-demo=", StringComparison.Ordinal))
            .Select(argument => argument["--readme-demo=".Length..])
            .ToArray();
        var outputValues = arguments
            .Where(argument => argument.StartsWith("--readme-output=", StringComparison.Ordinal))
            .Select(argument => argument["--readme-output=".Length..])
            .ToArray();
        var hasReadmeArguments = demoValues.Length > 0 || outputValues.Length > 0;
        if (hasReadmeArguments)
        {
            if (previewCount > 0)
            {
                throw new InvalidOperationException("--preview-window과 README 캡처 옵션을 함께 사용할 수 없습니다.");
            }

            if (demoValues.Length != 1 || outputValues.Length != 1)
            {
                throw new InvalidOperationException(
                    "README 캡처에는 --readme-demo와 --readme-output을 각각 한 번씩 지정해야 합니다.");
            }

            var screen = demoValues[0] switch
            {
                "popover" => WindowsReadmeScreen.Popover,
                "settings" => WindowsReadmeScreen.Settings,
                "tray" => WindowsReadmeScreen.Tray,
                _ => throw new InvalidOperationException("README 화면은 popover, tray 또는 settings여야 합니다."),
            };
            var output = ReadmeCaptureOutputValidator.Validate(outputValues[0]);
            return new WindowsLaunchOptions(false, null, screen, output, background, true, settings, theme);
        }

        var loginValues = arguments
            .Where(argument => argument.StartsWith("--login-profile=", StringComparison.Ordinal))
            .Select(argument => argument["--login-profile=".Length..])
            .ToArray();
        int? loginProfileId = null;
        if (loginValues.Length > 0)
        {
            if (loginValues.Length != 1
                || !int.TryParse(loginValues[0], out var parsedProfileId)
                || parsedProfileId <= 0)
            {
                throw new InvalidOperationException("--login-profile에는 양의 정수 프로필 ID를 지정해야 합니다.");
            }

            loginProfileId = parsedProfileId;
        }

        return new WindowsLaunchOptions(
            PreviewWindow: previewCount == 1,
            LoginProfileId: loginProfileId,
            ReadmeScreen: null,
            ReadmeOutput: null,
            Background: background,
            Demo: demo,
            Settings: settings,
            DemoTheme: theme);
    }
}

public static class ReadmeCaptureOutputValidator
{
    public static string Validate(string requestedPath)
    {
        if (string.IsNullOrWhiteSpace(requestedPath)
            || !Path.IsPathFullyQualified(requestedPath)
            || !Path.GetExtension(requestedPath).Equals(".png", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("README 캡처 출력은 절대 경로의 PNG 파일이어야 합니다.");
        }

        var outputPath = Path.GetFullPath(requestedPath);
        var parent = Directory.GetParent(outputPath);
        if (parent is null
            || !parent.Exists
            || (parent.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException("README 캡처 출력 디렉터리가 안전하지 않습니다.");
        }

        if (Directory.Exists(outputPath))
        {
            throw new InvalidOperationException("README 캡처 출력 파일이 안전하지 않습니다.");
        }

        if (File.Exists(outputPath))
        {
            var attributes = File.GetAttributes(outputPath);
            if ((attributes & FileAttributes.ReparsePoint) != 0
                || (attributes & FileAttributes.Directory) != 0)
            {
                throw new InvalidOperationException("README 캡처 출력 파일이 안전하지 않습니다.");
            }
        }

        return outputPath;
    }
}
