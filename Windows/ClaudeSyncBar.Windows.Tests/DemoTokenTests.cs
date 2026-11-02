using System.Text.Json;
using ClaudeSyncBar.Ubuntu.Backend;
using ClaudeSyncBar.Windows.Core;

namespace ClaudeSyncBar.Windows.Tests;

/// <summary>The demo's made-up logs and readings go through the real calculation and show what the page can show.</summary>
public sealed class DemoTokenTests
{
    private static async Task<TokenPeriodView> ViewAsync(DemoService service, int hours, string basis = TokenBurnBasis.Uncached)
    {
        using var parameters = JsonDocument.Parse($"{{\"hours\":{hours},\"basis\":\"{basis}\"}}");
        var result = await service.DispatchAsync("tokens.view", parameters.RootElement, CancellationToken.None);
        return (TokenPeriodView)result!.GetType().GetProperty("view")!.GetValue(result)!;
    }

    private static DemoService Service()
    {
        var now = DateTimeOffset.UtcNow;
        DashboardUsage Usage(double week) => new(new(week, now.AddDays(2), 604800), now);
        return new DemoService(
        [
            new(1, "main@example.com", "개인 계정", false, false, Usage(32), "Max 20x"),
            new(2, "work@example.com", "업무 계정", false, false, Usage(76), "Max 20x"),
            new(3, "lab@example.com", "실험 계정", false, false, Usage(95), "Max 5x"),
            new(5, "sub@example.com", "보조 계정", false, true, null, "Pro"),
        ], activeProfileId: 2);
    }

    [Fact]
    public async Task TheCalculationFindsTheRatesTheDemoWasMadeWith()
    {
        var service = Service();
        var month = await ViewAsync(service, 720);

        Assert.True(month.Clipped);
        Assert.InRange(month.Throughput.Total / 1e6, 75, 88);
        Assert.InRange(month.Burn.AttributedTokens / month.Burn.BasisTokens, 0.8, 0.97);
        var work = month.Burn.Accounts[0];
        Assert.True(work.IsCurrent);
        Assert.Equal("ok", work.Weekly.State);
        // Made with 0.26% of the week per million uncached tokens, and 1.04% on the smaller plan.
        Assert.InRange(work.Weekly.RatePerMillion!.Value, 0.23, 0.29);
        Assert.InRange(month.Burn.Accounts.Single(account => account.Name == "실험 계정").Weekly.RatePerMillion!.Value, 0.9, 1.2);
        Assert.Equal(4, work.Weekly.UntrackedPercent);
        Assert.Contains("보조 계정", month.Burn.IdleAccounts);

        var hour = await ViewAsync(service, 1);
        Assert.Equal("collecting", hour.Burn.Accounts[0].Weekly.State);
        Assert.False(hour.Clipped);
        var total = (await ViewAsync(service, 720, TokenBurnBasis.Total)).Burn.Accounts[0].Weekly.RatePerMillion!.Value;
        Assert.InRange(total, 0.009, 0.014);
    }
}
