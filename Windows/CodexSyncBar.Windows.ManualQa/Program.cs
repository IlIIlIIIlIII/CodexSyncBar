using System.Text.Json;
using CodexSyncBar.Windows.Core;

if (args.Length >= 3 && args[0] == "--device") return await DeviceQa.RunAsync(args);
if (args.Length == 1 && args[0] == "--inspect-local-cli")
{
    var inspectPaths = new WindowsPaths();
    var info = await new WindowsCliManagementService(inspectPaths, new LocalSwitchService(new AuthStore(inspectPaths), inspectPaths)).InspectAsync();
    Console.WriteLine(JsonSerializer.Serialize(info));
    return 0;
}

// Explicit opt-in only: manual OAuth uses the same service as the app, but
// cannot resolve the real user's active authentication through CODEX_HOME.
if (!OperatingSystem.IsWindows() || args.Length != 1 || args[0] != "--login")
{
    Console.Error.WriteLine("Windows에서 --login을 지정하세요. 전용 Chrome 창에서 사용자가 직접 로그인합니다.");
    return 2;
}

var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
    ".codex-syncbar", "ManualQa", Guid.NewGuid().ToString("N"));
var paths = new WindowsPaths(Path.Combine(root, "home"), Path.Combine(root, "local"));
paths.EnsureDirectories();
Console.WriteLine("QA_ROOT=" + root);
var store = new AuthStore(paths);
var login = new CodexLoginService(paths, store, new BrowserLoginService(paths));
using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(15));
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
var checks = new Dictionary<string, bool>();
try
{
    await login.LoginAsync(1, false, new Progress<string>(Console.WriteLine), cancellation.Token);
    checks["official_cli_login_and_account_validation"] = true;
    checks["profile_is_dpapi_protected"] = File.ReadAllText(paths.ProfileAuthFile(1)).Contains("protectedAuth");
    checks["no_active_auth_written"] = !File.Exists(paths.ActiveAuthFile);
    var credentials = store.ReadCredentials(1);
    checks["access_only_export_omits_refresh_token"] = string.IsNullOrEmpty(store.CreateAccessOnlyCopy(1).Tokens.RefreshToken);
    var usage = await new UsageService().FetchAsync(credentials, cancellation.Token);
    checks["usage_api_responded"] = usage.UpdatedAt > DateTimeOffset.UtcNow.AddMinutes(-1);
    checks["usage_has_at_least_one_limit"] = usage.Session is not null || usage.Weekly is not null;
    Console.WriteLine("MANUAL_LOGIN_QA_SUCCEEDED");
    return checks.Values.All(value => value) ? 0 : 1;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("MANUAL_LOGIN_QA_CANCELLED");
    return 3;
}
catch (Exception error)
{
    // Keep OAuth URLs, tokens, email and raw server output out of reports.
    Console.Error.WriteLine("MANUAL_LOGIN_QA_FAILED: " + error.GetType().Name);
    return 1;
}
finally
{
    var report = Path.Combine(root, "results.json");
    File.WriteAllText(report, JsonSerializer.Serialize(new { checkedAt = DateTimeOffset.UtcNow, checks },
        new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine("QA_REPORT=" + report);
}
