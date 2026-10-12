using CodexSyncBar.Windows.Core;

namespace CodexSyncBar.Ubuntu.Tests;

public sealed class TermuxCliTests
{
    [Fact]
    public void RepositoryNoOpIsSuccessfulAndDoesNotClaimAnUpstreamUpgrade()
    {
        var result = CliUpdateResult.Parse(new ProcessResult(0,
            "Reading package lists...\nbefore=0.161.0 after=0.161.0 manager=termux restart=not-needed\n", ""));
        Assert.Equal("termux", result.Manager);
        Assert.Contains("Termux 저장소 기준 최신 버전", result.DisplayText);
        Assert.Contains("실행 중인 CLI를 유지", result.DisplayText);
        Assert.Contains("공식 CLI보다 늦을 수", result.DisplayText);
        Assert.DoesNotContain("재연결 대기", result.DisplayText);
    }

    [Fact]
    public void TermuxUpgradeExplainsInteractiveCliRestart()
    {
        var result = new CliUpdateResult("0.160.0", "0.161.0", "termux", "not-running");
        Assert.Contains("Termux 패키지 업데이트 완료", result.DisplayText);
        Assert.Contains("종료 후 다시 실행", result.DisplayText);
    }

    [Fact]
    public async Task BatchUpdateCountsRepositoryNoOpAsCompleted()
    {
        var service = new RemoteCliManagementService((update, _) => Task.FromResult(update
            ? new ProcessResult(0, "before=0.161.0 after=0.161.0 manager=termux restart=not-needed\n", "")
            : new ProcessResult(0, "{\"version\":\"0.161.0\",\"path\":\"/termux/bin/codex\",\"manager\":\"termux\",\"canUpdate\":true,\"notice\":\"\"}", "")));
        var results = await new CliBatchUpdater().RunAsync([new("tablet", "tablet", service)]);
        Assert.Equal(CliDeviceUpdateState.Completed, Assert.Single(results).State);
    }
}
