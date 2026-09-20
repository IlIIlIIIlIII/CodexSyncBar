using CodexSyncBar.Windows.Core;

namespace CodexSyncBar.Windows.Core.Tests;

public sealed class CodexCliLocatorTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "syncbar-cli-locator-" + Guid.NewGuid().ToString("N"));
    private string Roaming => Path.Combine(root, "roaming");
    private string Local => Path.Combine(root, "local");
    private string User => Path.Combine(root, "user");
    private string Standalone => CreateFile(User, ".codex-syncbar", "Tools", "codex.exe");

    [Fact]
    public void ManagedVersionPointerIsUsedWithoutAllowingTraversal()
    {
        var fallback = Standalone;
        var version = "rust-v1.2.3-" + new string('a', 32);
        var current = CreateFile(User, ".codex-syncbar", "Tools", version, "codex.exe");
        var pointer = Path.Combine(User, ".codex-syncbar", "Tools", "current.txt");
        File.WriteAllText(pointer, version);
        Assert.Equal(current, CodexCliLocator.Find(null, Roaming, Local, User));
        File.WriteAllText(pointer, "../other");
        Assert.Equal(fallback, CodexCliLocator.Find(null, Roaming, Local, User));
    }

    [Fact]
    public void StandaloneCliIsPreferredToDesktopPackageExecutable()
    {
        var desktop = CreateFile(root, "desktop", "codex.exe");
        var standalone = Standalone;
        Assert.Equal(standalone, CodexCliLocator.Find(Path.GetDirectoryName(desktop), Roaming, Local, User));
    }

    [Fact]
    public void ExistingGlobalNpmShimKeepsPriority()
    {
        _ = Standalone;
        var npm = CreateFile(Roaming, "npm", "codex.cmd");
        Assert.Equal(npm, CodexCliLocator.Find(null, Roaming, Local, User));
    }

    [Fact]
    public void CustomPathNpmShimKeepsPriority()
    {
        _ = Standalone;
        var npm = CreateFile(root, "custom-npm", "codex.cmd");
        Assert.Equal(npm, CodexCliLocator.Find(Path.GetDirectoryName(npm), Roaming, Local, User));
    }

    [Fact]
    public void OtherPathExecutableRemainsFallbackWhenStandaloneIsAbsent()
    {
        var executable = CreateFile(root, "path", "codex.exe");
        Assert.Equal(executable, CodexCliLocator.Find(Path.GetDirectoryName(executable), Roaming, Local, User));
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
