using ClaudeSyncBar.Windows.Core;

namespace ClaudeSyncBar.Ubuntu.Tests;

public sealed class CliUpdateResultTests
{
    [Fact]
    public void NoNewerVersionIsSuccessfulAndDoesNotClaimAnUpgrade()
    {
        var result = CliUpdateResult.Parse(new ProcessResult(0,
            "up to date\nbefore=2.1.0 after=2.1.0 manager=npm restart=not-needed\n", ""));
        Assert.Equal("npm", result.Manager);
        Assert.Contains("이미 최신 버전", result.DisplayText);
        Assert.Contains("실행 중인 CLI를 유지", result.DisplayText);
        Assert.DoesNotContain("업데이트 완료", result.DisplayText);
    }

    [Fact]
    public void UpgradeExplainsThatRunningSessionsKeepTheOldVersion()
    {
        var result = new CliUpdateResult("2.1.0", "2.2.0", "native", "not-running");
        Assert.Contains("업데이트 완료: 2.1.0 → 2.2.0", result.DisplayText);
        Assert.Contains("다시 시작", result.DisplayText);
        Assert.Contains("재시작 대기", new CliUpdateResult("2.1.0", "2.2.0", "native", "reconnect-pending").DisplayText);
    }

    [Fact]
    public async Task BatchUpdateCountsNoOpAsCompleted()
    {
        var service = new RemoteCliManagementService((update, _) => Task.FromResult(update
            ? new ProcessResult(0, "before=2.1.0 after=2.1.0 manager=npm restart=not-needed\n", "")
            : new ProcessResult(0, "{\"version\":\"2.1.0\",\"path\":\"/termux/bin/claude\",\"manager\":\"npm\",\"canUpdate\":true,\"notice\":\"\"}", "")));
        var results = await new CliBatchUpdater().RunAsync([new("tablet", "tablet", service)]);
        Assert.Equal(CliDeviceUpdateState.Completed, Assert.Single(results).State);
    }
}
