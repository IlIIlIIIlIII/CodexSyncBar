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
        DashboardUsage Usage(double week) => new(new(week, now.AddDays(2), 604800), now, new(10, now.AddHours(1), 18000));
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
        Assert.Equal("ok", work.FiveHour.State);
        // Made with 4.6% of five hours and 0.26% of the week per million uncached tokens.
        Assert.InRange(work.FiveHour.RatePerMillion!.Value, 4.3, 4.9);
        Assert.InRange(work.Weekly.RatePerMillion!.Value, 0.23, 0.29);
        Assert.Equal(4, work.FiveHour.UntrackedPercent);
        Assert.Contains("보조 계정", month.Burn.IdleAccounts);

        var hour = await ViewAsync(service, 1);
        Assert.Equal("collecting", hour.Burn.Accounts[0].Weekly.State);
        Assert.False(hour.Clipped);
        var total = (await ViewAsync(service, 720, TokenBurnBasis.Total)).Burn.Accounts[0].FiveHour.RatePerMillion!.Value;
        Assert.InRange(total, 0.15, 0.25);
    }
}
