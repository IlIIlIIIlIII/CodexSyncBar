using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using ClaudeSyncBar.Windows.Core;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace ClaudeSyncBar.Ubuntu.Tests;

internal static class TestIsolation
{
    private static IDisposable? _keyProvider;

    [ModuleInitializer]
    internal static void Initialize()
    {
        // Linked cross-platform contract tests use the production encrypted store.
        // Keep their key in this test process; never read or write the login keyring.
        var key = RandomNumberGenerator.GetBytes(32);
        _keyProvider = LinuxSecretKeyProvider.OverrideForTests(() => key);
    }
}
