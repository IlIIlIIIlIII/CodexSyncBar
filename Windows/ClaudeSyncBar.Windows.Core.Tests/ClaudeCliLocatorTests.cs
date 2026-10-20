using ClaudeSyncBar.Windows.Core;

namespace ClaudeSyncBar.Windows.Core.Tests;

public sealed class ClaudeCliLocatorTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "syncbar-cli-locator-" + Guid.NewGuid().ToString("N"));
    private string Roaming => Path.Combine(root, "roaming");
    private string Local => Path.Combine(root, "local");
    private string User => Path.Combine(root, "user");
    private string Standalone => CreateFile(User, ".claude-syncbar", "Tools", "codex.exe");

    [Fact]
    public void ManagedVersionPointerIsUsedWithoutAllowingTraversal()
    {
        var fallback = Standalone;
        var version = "rust-v1.2.3-" + new string('a', 32);
        var current = CreateFile(User, ".claude-syncbar", "Tools", version, "codex.exe");
        var pointer = Path.Combine(User, ".claude-syncbar", "Tools", "current.txt");
        File.WriteAllText(pointer, version);
        Assert.Equal(current, ClaudeCliLocator.Find(null, Roaming, Local, User));
        File.WriteAllText(pointer, "../other");
        Assert.Equal(fallback, ClaudeCliLocator.Find(null, Roaming, Local, User));
    }

    [Fact]
    public void StandaloneCliIsPreferredToDesktopPackageExecutable()
    {
        var desktop = CreateFile(root, "desktop", "codex.exe");
        var standalone = Standalone;
        Assert.Equal(standalone, ClaudeCliLocator.Find(Path.GetDirectoryName(desktop), Roaming, Local, User));
    }

    [Fact]
    public void ExistingGlobalNpmShimKeepsPriority()
    {
        _ = Standalone;
        var npm = CreateFile(Roaming, "npm", "codex.cmd");
        Assert.Equal(npm, ClaudeCliLocator.Find(null, Roaming, Local, User));
    }

    [Fact]
    public void CustomPathNpmShimKeepsPriority()
    {
        _ = Standalone;
        var npm = CreateFile(root, "custom-npm", "codex.cmd");
        Assert.Equal(npm, ClaudeCliLocator.Find(Path.GetDirectoryName(npm), Roaming, Local, User));
    }

    [Fact]
    public void OtherPathExecutableRemainsFallbackWhenStandaloneIsAbsent()
    {
        var executable = CreateFile(root, "path", "codex.exe");
        Assert.Equal(executable, ClaudeCliLocator.Find(Path.GetDirectoryName(executable), Roaming, Local, User));
    }

    private static string CreateFile(params string[] parts)
    {
        var path = Path.Combine(parts);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "test fixture; never executed");
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
