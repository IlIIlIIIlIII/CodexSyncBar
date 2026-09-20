using CodexSyncBar.Windows.Core;
using CodexSyncBar_Windows;

if (args.Length is < 1 or > 3)
    throw new ArgumentException("Usage: WidgetPreviewExporter <output JSON path> [medium|large] [normal|stress|noaccount|busy|error|weekly|credits-multiple|credits-zero|credits-unknown]");
var large = args.Length > 1 && args[1] == "large";
var scenario = args.Length > 2 ? args[2] : "normal";
// Memory-only QA fixture: no controller, auth store, IPC or device adapters.
var snapshot = await new DemoDashboardRuntime().GetSnapshotAsync();
var now = DateTimeOffset.UtcNow;
var expiry = now.AddDays(7).AddHours(3).AddMinutes(14);
snapshot = snapshot with { Accounts = snapshot.Accounts.Select(a => a with
{
    Usage = a.Usage is null ? null : a.Usage with { ResetCreditExpirations = [expiry, expiry, expiry] },
}).ToArray() };
if (scenario == "stress")
    snapshot = snapshot with
    {
        Accounts = snapshot.Accounts.Select(account => account with
        {
            DisplayName = new string('장', 70),
            Usage = account.Usage is null ? null : account.Usage with
            {
                Session = new(92, now.AddHours(1), 18000), ResetCredits = 4,
                ResetCreditExpirations = [now.AddHours(3).AddMinutes(20), expiry, expiry, now.AddDays(14)],
            },
        }).ToArray(),
        Error = string.Concat(Enumerable.Repeat("오프라인 상태입니다. 앱에서 오류와 복구 결과를 확인해 주세요. ", 6))[..180],
        Devices = Enumerable.Range(0, 20).Select(i => new DashboardDevice($"wsl:{i}", new string('장', 70), "wsl", i % 2 == 0 ? 1 : 2, i % 3 != 0)).ToArray(),
    };
else if (scenario == "noaccount") snapshot = new();
else if (scenario == "busy") snapshot = snapshot with { IsBusy = true, Operation = new() { State = "applying", Message = "원격 장치에 적용하고 있습니다." } };
else if (scenario == "error") snapshot = snapshot with
{
    Error = "사용량을 갱신하지 못했습니다. 마지막으로 확인한 정보를 표시합니다.",
    Accounts = snapshot.Accounts.Select(a => a with { Usage = a.Usage is null ? null : a.Usage with { ResetCredits = null, ResetCreditExpirations = [] } }).ToArray(),
};
else if (scenario == "weekly") snapshot = snapshot with { Accounts = snapshot.Accounts.Select(a => a with
{
    Usage = a.Usage is null ? null : a.Usage with { Session = null, ResetCredits = 0, ResetCreditExpirations = [] },
}).ToArray() };
else if (scenario is "credits-multiple" or "credits-zero" or "credits-unknown")
    snapshot = snapshot with { Accounts = snapshot.Accounts.Select(a => a with
    {
        Usage = a.Usage is null ? null : a.Usage with
        {
            ResetCredits = scenario == "credits-unknown" ? null : scenario == "credits-zero" ? 0 : 3,
            ResetCreditExpirations = scenario == "credits-multiple" ? [now.AddMinutes(-1), now.AddSeconds(30), expiry] : [],
        },
    }).ToArray() };
else if (scenario != "normal") throw new ArgumentException("Unknown preview scenario.");
var output = Path.GetFullPath(args[0]);
Directory.CreateDirectory(Path.GetDirectoryName(output)!);
await File.WriteAllTextAsync(output, WidgetTemplates.Render(snapshot, large));
Console.WriteLine($"Exported the production {(large ? "large" : "medium")} card, synthetic scenario: {scenario}.");
