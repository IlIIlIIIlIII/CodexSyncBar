using CodexSyncBar.Windows.Core;

namespace CodexSyncBar.Windows.Core.Tests;

public sealed class LaunchOptionTests
{
    [Fact]
    public void PreviewWindowUsesTheQaLaunchPath()
    {
        var options = WindowsLaunchOptions.Parse(["--preview-window"]);

        Assert.True(options.PreviewWindow);
        Assert.True(options.UsesReadmePage);
        Assert.Null(options.LoginProfileId);
        Assert.Null(options.ReadmeScreen);
        Assert.Null(options.ReadmeOutput);
    }

    [Fact]
    public void LoginProfileRequiresAndPreservesPositiveId()
    {
        var options = WindowsLaunchOptions.Parse(["--login-profile=17"]);

        Assert.Equal(17, options.LoginProfileId);
        Assert.False(options.UsesReadmePage);
    }

    [Fact]
    public void LoginProfileTakesPriorityOverPreviewWindow()
    {
        var options = WindowsLaunchOptions.Parse(["--preview-window", "--login-profile=17"]);

        Assert.True(options.PreviewWindow);
        Assert.Equal(17, options.LoginProfileId);
        Assert.False(options.UsesReadmePage);
    }

    [Fact]
    public void ReadmeCaptureRequiresOneScreenAndAValidatedPngPath()
    {
        var root = Path.Combine(Path.GetTempPath(), $"codex-syncbar-readme-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(root);
            var requested = Path.Combine(root, "popover.png");
            var options = WindowsLaunchOptions.Parse([
                "--readme-demo=popover",
                $"--readme-output={requested}",
            ]);

            Assert.Equal(WindowsReadmeScreen.Popover, options.ReadmeScreen);
            Assert.Equal(Path.GetFullPath(requested), options.ReadmeOutput);
            Assert.True(options.UsesReadmePage);
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
    public void ReadmeSettingsScreenIsSupported()
    {
        var root = Path.Combine(Path.GetTempPath(), $"codex-syncbar-readme-settings-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(root);
            var options = WindowsLaunchOptions.Parse([
                "--readme-demo=settings",
                $"--readme-output={Path.Combine(root, "settings.PNG")}",
            ]);

            Assert.Equal(WindowsReadmeScreen.Settings, options.ReadmeScreen);
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
    public void DuplicatePreviewArgumentsAreRejected()
    {
        Assert.Throws<InvalidOperationException>(() =>
            WindowsLaunchOptions.Parse(["--preview-window", "--preview-window"]));
    }

    [Theory]
    [InlineData("--readme-demo=popover")]
    [InlineData("--readme-output=/tmp/demo.png")]
    [InlineData("--readme-demo=unknown")]
    [InlineData("--login-profile=0")]
    [InlineData("--login-profile=-3")]
    [InlineData("--login-profile=not-an-int")]
    public void InvalidLaunchArgumentIsRejected(string argument)
    {
        Assert.Throws<InvalidOperationException>(() => WindowsLaunchOptions.Parse([argument]));
    }

    [Fact]
    public void ReadmeOutputRejectsRelativeAndNonPngPaths()
    {
        Assert.Throws<InvalidOperationException>(() =>
            ReadmeCaptureOutputValidator.Validate("relative/demo.png"));
        Assert.Throws<InvalidOperationException>(() =>
            ReadmeCaptureOutputValidator.Validate(Path.Combine(Path.GetTempPath(), "demo.jpg")));
    }

    [Fact]
    public void ReadmeOutputRejectsMissingParentAndDirectoryCollision()
    {
        var root = Path.Combine(Path.GetTempPath(), $"codex-syncbar-readme-invalid-{Guid.NewGuid():N}");
        try
        {
            var missingParent = Path.Combine(root, "missing", "demo.png");
            Assert.Throws<InvalidOperationException>(() =>
                ReadmeCaptureOutputValidator.Validate(missingParent));

            Directory.CreateDirectory(root);
            var directoryCollision = Path.Combine(root, "demo.png");
            Directory.CreateDirectory(directoryCollision);
            Assert.Throws<InvalidOperationException>(() =>
                ReadmeCaptureOutputValidator.Validate(directoryCollision));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
