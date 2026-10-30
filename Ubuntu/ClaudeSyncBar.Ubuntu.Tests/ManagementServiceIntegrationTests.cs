using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ClaudeSyncBar.Ubuntu.Backend;
using ClaudeSyncBar.Windows.Core;
using Contracts = ClaudeSyncBar.Windows.Core.Tests.TestAuth;

namespace ClaudeSyncBar.Ubuntu.Tests;

public sealed class ManagementServiceIntegrationTests
{
    [Fact]
    public async Task SettingsShowBothClaudeQuotasAndNeverEchoTheRetiredCodexKeys()
    {
        await using var fixture = new Fixture(withRemote: false);
        var settings = JsonSerializer.SerializeToElement(await fixture.Call<object>("settings.get"), Wire.Json);
        Assert.True(settings.GetProperty("claudeFiveHour").GetBoolean());
        Assert.True(settings.GetProperty("claudeWeekly").GetBoolean());
        // The Codex version's retired key is not this quota and is never stored.
        await fixture.Call<object>("settings.save", new { fiveHour = true, claudeFiveHour = false, claudeWeekly = true });
        settings = JsonSerializer.SerializeToElement(await fixture.Call<object>("settings.get"), Wire.Json);
        Assert.False(settings.TryGetProperty("fiveHour", out _));
        Assert.False(settings.GetProperty("claudeFiveHour").GetBoolean());
        Assert.True(settings.GetProperty("claudeWeekly").GetBoolean());
        Assert.DoesNotContain("\"fiveHour\"", File.ReadAllText(fixture.Paths.UsageDisplayPreferencesFile));
    }

    [Fact]
    public async Task TheTaskbarReadoutIsOnUntilTurnedOffAndStaysApartFromTheQuotas()
    {
        await using var fixture = new Fixture(withRemote: false);
        var settings = JsonSerializer.SerializeToElement(await fixture.Call<object>("settings.get"), Wire.Json);
        Assert.True(settings.GetProperty("taskbarUsage").GetBoolean());
        await fixture.Call<object>("settings.save", new { taskbarUsage = false });
        settings = JsonSerializer.SerializeToElement(await fixture.Call<object>("settings.get"), Wire.Json);
        Assert.False(settings.GetProperty("taskbarUsage").GetBoolean());
        Assert.True(settings.GetProperty("claudeFiveHour").GetBoolean());
        Assert.True(settings.GetProperty("claudeWeekly").GetBoolean());
    }

    private static string Quotas(double fiveHour, double weekly) =>
        "{\"five_hour\":{\"utilization\":" + fiveHour + ",\"resets_at\":\"2099-01-01T05:00:00Z\"},"
        + "\"seven_day\":{\"utilization\":" + weekly + ",\"resets_at\":\"2099-01-07T00:00:00Z\"}}";

    private static async Task RefreshUsage(Fixture fixture) =>
        Assert.Equal("completed", (await fixture.Wait(await fixture.Call<OperationView>("refresh", new { kind = "usage" }))).State);

    [Fact]
    public async Task TokensAreCutToTheChosenPeriodAndEveryUsageLookIsKept()
    {
        await using var fixture = new Fixture(withRemote: false, usage: _ => Quotas(12, 30));
        await RefreshUsage(fixture);
        var history = File.ReadAllLines(Path.Combine(fixture.Paths.StateRoot, "quota-history.jsonl"));
        Assert.Contains(history, line => line.Contains("\"Samples\":[{\"ProfileId\"") && line.Contains("\"FiveHourUsed\":12") && line.Contains("\"DeviceId\":\"local\""));

        var log = Path.Combine(fixture.Paths.ClaudeHome, "projects", "-work", "a.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(log)!);
        File.WriteAllText(log, "{\"type\":\"assistant\",\"requestId\":\"r1\",\"timestamp\":\"" + DateTimeOffset.UtcNow.AddMinutes(-30).UtcDateTime.ToString("o")
            + "\",\"message\":{\"id\":\"m1\",\"model\":\"claude-opus-5-5\",\"usage\":{\"input_tokens\":10,\"cache_read_input_tokens\":1000,"
            + "\"cache_creation_input_tokens\":200,\"output_tokens\":40}}}\n");
        var collected = JsonSerializer.SerializeToElement(await fixture.Call<object>("tokens"), Wire.Json);
        var month = collected.GetProperty("view");
        Assert.Equal(720, month.GetProperty("hours").GetInt32());
        Assert.Equal(1250, month.GetProperty("totals").GetProperty("totalTokens").GetInt64());
        // The minutes stay in the backend; the screens get the period cut from them.
        Assert.Equal(0, collected.GetProperty("devices")[0].GetProperty("summary").GetProperty("minutes").GetArrayLength());

        var hour = JsonSerializer.SerializeToElement(await fixture.Call<object>("tokens.view", new { hours = 1, basis = "total" }), Wire.Json).GetProperty("view");
        Assert.Equal(("total", 1), (hour.GetProperty("basis").GetString(), hour.GetProperty("hours").GetInt32()));
        Assert.Equal(1250, hour.GetProperty("totals").GetProperty("totalTokens").GetInt64());
        // The log began half an hour ago: the hour is divided by the time since then.
        Assert.True(hour.GetProperty("clipped").GetBoolean());
        Assert.Equal(1250 / hour.GetProperty("elapsedHours").GetDouble(), hour.GetProperty("throughput").GetProperty("total").GetDouble(), 6);
        var settings = JsonSerializer.SerializeToElement(await fixture.Call<object>("settings.get"), Wire.Json);
        Assert.Equal(1, settings.GetProperty("tokenPeriodHours").GetInt32());
        Assert.Equal("total", settings.GetProperty("tokenRateBasis").GetString());
    }

    [Fact]
    public async Task AutomaticSwitchLeavesAnAccountWithNothingLeftForTheNextInPriorityOrder()
    {
        var usage = new Dictionary<string, string>
        {
            ["synthetic-access-1"] = Quotas(100, 40),
            ["synthetic-access-2"] = Quotas(10, 20),
        };
        var notices = new List<string>();
        await using var fixture = new Fixture(withRemote: false, usage: token => usage[token], notify: notices.Add);
        await RefreshUsage(fixture);

        // Off unless the user turns it on.
        Assert.False(await fixture.WatchOnce());
        Assert.Equal("fixture-account-1", fixture.Auth.ReadActiveAccountId());
        await fixture.Call<object>("settings.save", new { autoSwitchEnabled = true });

        Assert.True(await fixture.WatchOnce());
        Assert.Equal("fixture-account-2", fixture.Auth.ReadActiveAccountId());
        var snapshot = await fixture.Call<ManagementSnapshot>("snapshot");
        Assert.Equal(2, snapshot.ActiveProfileId);
        Assert.Equal("auto-switch", snapshot.Operation!.Kind);
        var settings = JsonSerializer.SerializeToElement(snapshot.Settings, Wire.Json);
        Assert.True(settings.GetProperty("autoSwitchEnabled").GetBoolean());
        var last = settings.GetProperty("autoSwitchLast");
        Assert.True(last.GetProperty("succeeded").GetBoolean());
        Assert.Equal("계정1의 5시간 한도가 소진되어 계정2(으)로 자동 전환했습니다.", last.GetProperty("message").GetString());
        Assert.Equal(last.GetProperty("message").GetString(), Assert.Single(notices));

        // The account switched to has room left; there is nothing more to do.
        Assert.False(await fixture.WatchOnce());
        Assert.Single(notices);

        // A weekly quota used up counts the same, and the order of the account list is the priority.
        usage["synthetic-access-2"] = Quotas(10, 100);
        usage["synthetic-access-1"] = Quotas(0, 40);
        await RefreshUsage(fixture);
        Assert.True(await fixture.WatchOnce());
        Assert.Equal("fixture-account-1", fixture.Auth.ReadActiveAccountId());
        Assert.Contains("주간 한도가 소진되어", notices[^1]);
    }

    [Fact]
    public async Task AutomaticSwitchNeverActsOnOldValuesAndSaysOnceWhenNoAccountIsLeft()
    {
        var usage = new Dictionary<string, string>
        {
            ["synthetic-access-1"] = Quotas(100, 40),
            ["synthetic-access-2"] = Quotas(30, 100),
        };
        var notices = new List<string>();
        await using var fixture = new Fixture(withRemote: false, usage: token => usage[token], notify: notices.Add);
        await RefreshUsage(fixture);
        await fixture.Call<object>("settings.save", new { autoSwitchEnabled = true });

        Assert.False(await fixture.WatchOnce());
        Assert.Equal("fixture-account-1", fixture.Auth.ReadActiveAccountId());
        Assert.Equal("계정1의 5시간 한도가 소진됐지만 지금 쓸 수 있는 다른 계정이 없습니다.", Assert.Single(notices));
        // Said once, not on every look.
        Assert.False(await fixture.WatchOnce());
        Assert.Single(notices);

        // Twenty minutes on, the values are too old to leave an account or choose one on.
        usage["synthetic-access-2"] = Quotas(30, 20);
        Assert.False(await fixture.WatchOnce(DateTimeOffset.UtcNow.AddMinutes(20)));
        Assert.Single(notices);
        Assert.Equal("fixture-account-1", fixture.Auth.ReadActiveAccountId());
    }

    [Fact]
    public async Task StoppedSessionsContinueOncePerStopOnlyWhileTheAccountHasRoom()
    {
        var usage = new Dictionary<string, string> { ["synthetic-access-1"] = Quotas(100, 40), ["synthetic-access-2"] = Quotas(10, 20) };
        var stops = new List<StoppedSession> { new("aaaaaaaa-0000-4000-8000-000000000001", "/work/one", "error-1", DateTimeOffset.UtcNow.AddMinutes(-3)) };
        var open = new HashSet<string>();
        var launched = new List<StoppedSession>();
        var notices = new List<string>();
        await using var fixture = new Fixture(withRemote: false, usage: token => usage[token], notify: notices.Add,
            resume: new(_ => stops, stop => open.Contains(stop.SessionId), stop => { launched.Add(stop); return true; }));
        await RefreshUsage(fixture);
        await fixture.Call<object>("settings.save", new { autoResumeEnabled = true });

        // The account in use has nothing left: continuing now would only stop again.
        Assert.False(await fixture.WatchOnce());
        Assert.Empty(launched);

        usage["synthetic-access-1"] = Quotas(0, 40);
        await RefreshUsage(fixture);
        var now = DateTimeOffset.UtcNow;
        await fixture.WatchOnce(now);
        Assert.Equal("aaaaaaaa-0000-4000-8000-000000000001", Assert.Single(launched).SessionId);
        Assert.Equal("한도로 멈춘 세션 1개를 이어서 실행했습니다: one", Assert.Single(notices));
        var settings = JsonSerializer.SerializeToElement((await fixture.Call<ManagementSnapshot>("snapshot")).Settings, Wire.Json);
        Assert.True(settings.GetProperty("autoResumeEnabled").GetBoolean());
        Assert.False(settings.GetProperty("autoSwitchEnabled").GetBoolean());
        Assert.Equal(notices[0], settings.GetProperty("autoResumeLast").GetProperty("message").GetString());

        // The same stop is continued once; a session still open somewhere is left alone.
        stops.Add(new("bbbbbbbb-0000-4000-8000-000000000002", "/work/two", "error-9", now));
        open.Add("bbbbbbbb-0000-4000-8000-000000000002");
        await fixture.WatchOnce(now.AddMinutes(2));
        Assert.Single(launched);

        // A later stop of the same session is continued again, up to three times a day.
        for (var stop = 2; stop <= 4; stop++)
        {
            stops[0] = stops[0] with { ErrorId = "error-" + stop };
            await fixture.WatchOnce(now.AddMinutes(2 * stop));
        }
        Assert.Equal(3, launched.Count);
        // A day later the count starts over.
        stops[0] = stops[0] with { ErrorId = "error-5" };
        usage["synthetic-access-1"] = Quotas(0, 40);
        Assert.Equal(1, fixture.Resume(now.AddHours(25)));
        Assert.Equal(4, launched.Count);
    }

    [Fact]
    public void SmartSwitchPicksTheCandidateWhoseWeekResetsFirstAmongThoseWithBothQuotasLeft()
    {
        var now = DateTimeOffset.UtcNow;
        DashboardUsage Usage(double fiveHourLeft, double weeklyLeft, double resetDays, double minutesOld = 0) => new(
            new(100 - weeklyLeft, now.AddDays(resetDays), 604800), now.AddMinutes(-minutesOld), new(100 - fiveHourLeft, now.AddHours(2), 18000));
        AccountView Account(int id, DashboardUsage? usage, bool candidate = true, bool needsLogin = false) =>
            new(id, $"{id}@example.invalid", $"계정{id}", false, needsLogin, usage, SmartSwitch.Plan, candidate);
        var accounts = new[]
        {
            Account(1, Usage(80, 90, 5)),
            Account(2, Usage(0, 99, 1)),                       // five-hour used up
            Account(3, Usage(90, 0, 1)),                       // week used up
            Account(4, Usage(90, 95, 0.5), candidate: false),  // not ticked on the account list
            Account(5, Usage(90, 96, 0.5), needsLogin: true),
            Account(6, Usage(90, 97, 0.5, minutesOld: 20)),    // too old to trust
            Account(7, Usage(26, 0.5, 2)),                     // a sliver of the week is room; resets first of the rest
            Account(8, Usage(60, 70, 4)),
            Account(9, null),
            Account(16, Usage(25, 99, 1)),                     // 75% of five hours used
        };
        var choice = SmartSwitch.Choose(accounts, now)!;
        Assert.Equal((7, 26d, 0.5), (choice.Account.Id, Math.Round(choice.FiveHour, 6), Math.Round(choice.Weekly, 6)));
        Assert.Equal(now.AddDays(2), choice.WeeklyReset);
        Assert.Null(SmartSwitch.Choose(accounts[1..6], now));
        // The same reset: the most weekly left, then the most five-hour left, then the list order.
        Assert.Equal(11, SmartSwitch.Choose([Account(10, Usage(90, 40, 2)), Account(11, Usage(50, 60, 2))], now)!.Account.Id);
        Assert.Equal(13, SmartSwitch.Choose([Account(12, Usage(50, 60, 2)), Account(13, Usage(70, 60, 2))], now)!.Account.Id);
        Assert.Equal(14, SmartSwitch.Choose([Account(14, Usage(50, 60, 2)), Account(15, Usage(50, 60, 2))], now)!.Account.Id);
        // A five-hour quota whose reset time has passed is whole again.
        var fiveHourReset = new DashboardUsage(new(10, now.AddDays(3), 604800), now, new(100, now.AddMinutes(-1), 18000));
        Assert.Equal(1, SmartSwitch.Choose([Account(1, fiveHourReset)], now)!.Account.Id);
        // A week past its reset is whole too, but when it next resets is unknown, so a known reset comes first.
        var weekReset = new DashboardUsage(new(100, now.AddMinutes(-1), 604800), now, new(10, now.AddHours(1), 18000));
        var unknown = SmartSwitch.Choose([Account(1, weekReset), accounts[7]], now)!;
        Assert.Equal(8, unknown.Account.Id);
        Assert.Null(SmartSwitch.Choose([Account(1, weekReset)], now)!.WeeklyReset);

        // Untouched, only the $200 Max plan is a candidate; a tick or a cleared box wins over the plan.
        Assert.True(SmartSwitch.IsCandidate(SmartSwitch.Plan, null));
        Assert.False(SmartSwitch.IsCandidate("Max 5x", null));
        Assert.False(SmartSwitch.IsCandidate(null, null));
        Assert.True(SmartSwitch.IsCandidate("Pro", true));
        Assert.False(SmartSwitch.IsCandidate(SmartSwitch.Plan, false));
    }

    [Fact]
    public async Task SmartSwitchCandidatesAreTickedOnTheAccountListAndKeptApartFromTheAutomaticSwitch()
    {
        var usage = new Dictionary<string, string> { ["synthetic-access-1"] = Quotas(20, 60), ["synthetic-access-2"] = Quotas(50, 10) };
        await using var fixture = new Fixture(withRemote: false, usage: token => usage[token]);
        async Task<bool[]> Candidates() => (await fixture.Call<ManagementSnapshot>("snapshot")).Accounts.Select(account => account.SmartCandidate).ToArray();
        async Task<JsonElement> Smart() => JsonSerializer.SerializeToElement(await fixture.Call<object>("smart.preview"), Wire.Json);

        // Both fixture accounts are on the $200 Max plan, so both start ticked; the wire names the field for the screens.
        Assert.Equal(new[] { true, true }, await Candidates());
        Assert.True(JsonSerializer.SerializeToElement(await fixture.Call<ManagementSnapshot>("snapshot"), Wire.Json)
            .GetProperty("accounts")[0].GetProperty("smartCandidate").GetBoolean());
        Assert.Equal(2, (await Smart()).GetProperty("profileId").GetInt32());

        // Clearing account 2 leaves account 1, the one in use.
        await fixture.Call<object>("account.smart", new { profileId = 2, enabled = false });
        Assert.Equal(new[] { true, false }, await Candidates());
        var smart = await Smart();
        Assert.Equal(1, smart.GetProperty("profileId").GetInt32());
        Assert.True(smart.GetProperty("alreadyActive").GetBoolean());

        // With nothing ticked, the switch says so before looking anything up.
        await fixture.Call<object>("account.smart", new { profileId = 1, enabled = false });
        smart = await Smart();
        Assert.False(smart.GetProperty("found").GetBoolean());
        Assert.Equal(SmartSwitch.NoCandidateMessage, smart.GetProperty("message").GetString());

        // The choice stays in its own file, which the automatic switch never rewrites.
        await fixture.Call<object>("settings.save", new { autoSwitchEnabled = true });
        var file = Path.Combine(fixture.Paths.StateRoot, "smart-switch.json");
        Assert.Equal(new Dictionary<int, bool> { [1] = false, [2] = false }, new SmartSwitchStore(fixture.Paths).Load().Candidates);
        Assert.DoesNotContain("candidates", File.ReadAllText(Path.Combine(fixture.Paths.StateRoot, "auto-switch.json")), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));

        // Ticking it again brings it back; the setting needs a yes or no and a registered account.
        await fixture.Call<object>("account.smart", new { profileId = 2, enabled = true });
        Assert.Equal(new[] { false, true }, await Candidates());
        var missing = await Assert.ThrowsAsync<RpcException>(() => fixture.Call<object>("account.smart", new { profileId = 2 }));
        Assert.Equal("invalid_request", missing.Code);
        var unknown = await Assert.ThrowsAsync<RpcException>(() => fixture.Call<object>("account.smart", new { profileId = 9, enabled = true }));
        Assert.Equal("account_unavailable", unknown.Code);
        Assert.Equal(new[] { false, true }, await Candidates());
    }

    [Fact]
    public async Task ADeletedAccountTakesItsSmartSwitchChoiceWithIt()
    {
        await using var fixture = new Fixture(withRemote: false);
        var store = new SmartSwitchStore(fixture.Paths);
        // A choice left over from an account removed some other way is dropped at the next tick.
        store.Save(new() { Candidates = { [7] = true } });
        await fixture.Call<object>("account.smart", new { profileId = 2, enabled = false });
        Assert.Equal(new Dictionary<int, bool> { [2] = false }, store.Load().Candidates);

        // A logged-out account is the only kind that can be deleted.
        fixture.Auth.DeleteProfile(2);
        await fixture.Call<object>("account.delete", new { profileId = 2 });
        Assert.Empty(store.Load().Candidates);
        Assert.Equal(new[] { 1 }, (await fixture.Call<ManagementSnapshot>("snapshot")).Accounts.Select(account => account.Id));
    }

    [Fact]
    public async Task SmartPreviewNamesTheChoiceAndAppliesLikeAnyOtherSwitch()
    {
        var usage = new Dictionary<string, string> { ["synthetic-access-1"] = Quotas(20, 60), ["synthetic-access-2"] = Quotas(50, 10) };
        await using var fixture = new Fixture(withRemote: false, usage: token => usage[token]);
        // Usage that was never looked up is looked up first.
        var smart = JsonSerializer.SerializeToElement(await fixture.Call<object>("smart.preview"), Wire.Json);
        Assert.True(smart.GetProperty("found").GetBoolean());
        Assert.False(smart.GetProperty("alreadyActive").GetBoolean());
        Assert.Equal(2, smart.GetProperty("profileId").GetInt32());
        // Both weeks reset at the same time here, so the one with more left is chosen.
        Assert.Equal($"주간 90% · 5시간 50% 남음 · 주간 초기화 {SmartSwitch.When(DateTimeOffset.Parse("2099-01-07T00:00:00Z"))}", smart.GetProperty("reason").GetString());
        var preview = smart.GetProperty("preview").Deserialize<SwitchPreview>(Wire.Json)!;
        Assert.Equal(2, preview.ProfileId);
        Assert.True(preview.CanApply, preview.BlockingReason);
        Assert.Equal("completed", (await fixture.Apply(preview)).State);
        Assert.Equal("fixture-account-2", fixture.Auth.ReadActiveAccountId());
        var snapshot = await fixture.Call<ManagementSnapshot>("snapshot");
        Assert.All(snapshot.Accounts, account => Assert.Equal(SmartSwitch.Plan, account.Plan));

        // The account in use is already the best one: nothing to apply.
        smart = JsonSerializer.SerializeToElement(await fixture.Call<object>("smart.preview"), Wire.Json);
        Assert.True(smart.GetProperty("alreadyActive").GetBoolean());
        Assert.False(smart.TryGetProperty("preview", out _));

        // Just under 75% of five hours used still counts.
        usage["synthetic-access-1"] = Quotas(74, 10);
        usage["synthetic-access-2"] = Quotas(100, 10);
        await RefreshUsage(fixture);
        smart = JsonSerializer.SerializeToElement(await fixture.Call<object>("smart.preview"), Wire.Json);
        Assert.Equal(1, smart.GetProperty("profileId").GetInt32());

        // 75% of five hours used passes an account over, so none is left.
        usage["synthetic-access-1"] = Quotas(75, 10);
        await RefreshUsage(fixture);
        smart = JsonSerializer.SerializeToElement(await fixture.Call<object>("smart.preview"), Wire.Json);
        Assert.False(smart.GetProperty("found").GetBoolean());
        Assert.Equal("스마트 전환 후보 중 주간 한도가 남고 5시간 한도를 75% 미만 쓴 계정이 없습니다.", smart.GetProperty("message").GetString());
    }

    [Fact]
    public void AutomaticSwitchPolicyTrustsOnlyFreshValuesAndTreatsAPassedResetAsWhole()
    {
        var now = DateTimeOffset.UtcNow;
        UsageWindow Window(double used, double resetHours) => new(used, now.AddHours(resetHours), 18000);
        var empty = new DashboardUsage(Window(30, 24), now, Window(100, 2));
        Assert.Equal("5시간", AutoSwitchPolicy.UsedUp(empty, now));
        Assert.Null(AutoSwitchPolicy.UsedUp(empty with { Error = "사용량 갱신 실패" }, now));
        Assert.Null(AutoSwitchPolicy.UsedUp(empty, now.AddMinutes(16)));
        // Its reset time has passed: the five-hour quota is whole again.
        Assert.Null(AutoSwitchPolicy.UsedUp(empty, now.AddHours(3)));
        Assert.Equal("주간", AutoSwitchPolicy.UsedUp(new DashboardUsage(Window(100, 24), now, null), now));
        // A sliver left is not used up, and an account with a sliver left can be switched to.
        var sliver = new DashboardUsage(Window(99.8, 24), now, Window(99.8, 2));
        Assert.Null(AutoSwitchPolicy.UsedUp(sliver, now));
        Assert.True(AutoSwitchPolicy.HasRoom(sliver, now));
        Assert.Equal("5시간", AutoSwitchPolicy.UsedUp(new DashboardUsage(Window(30, 24), now, Window(100.4, 2)), now));
        Assert.False(AutoSwitchPolicy.HasRoom(new DashboardUsage(null, now, Window(0, 2)), now));
        Assert.True(AutoSwitchPolicy.HasRoom(new DashboardUsage(Window(99, 24), now, null), now));
        Assert.Equal(1, AutoSwitchPolicy.Lowest(new DashboardUsage(Window(99, 24), now, Window(10, 2)), now)!.Value, 3);
        var accounts = new[]
        {
            new AccountView(3, "c", "c", false, true, new DashboardUsage(Window(0, 24), now, null)),
            new AccountView(1, "a", "a", false, false, new DashboardUsage(Window(0, 24), now, Window(100, 2))),
            new AccountView(4, "d", "d", false, false, new DashboardUsage(Window(100, 24), now, null)),
            new AccountView(2, "b", "b", false, false, new DashboardUsage(Window(50, 24), now, Window(0, 2))),
        };
        // In list order, skipping the account in use, one needing login and one used up.
        Assert.Equal(2, AutoSwitchPolicy.NextAccount(accounts, active: 1, now));
        Assert.Null(AutoSwitchPolicy.NextAccount(accounts[..3], active: 1, now));
    }

    [Fact]
    public async Task UbuntuAccountAndPreviewLabelsUseFullEmailWhenAliasIsAbsent()
    {
        await using var fixture = new Fixture(withRemote: false);
        foreach (var profile in new[] { 1, 2 })
            await fixture.Call<object>("account.rename", new { profileId = profile, alias = "" });
        var snapshot = await fixture.Call<ManagementSnapshot>("snapshot");
        foreach (var account in snapshot.Accounts)
        {
            Assert.Equal($"fixture{account.Id}@example.invalid", account.Email);
            Assert.Equal(account.Email, account.Alias);
        }
        var preview = await fixture.Call<SwitchPreview>("preview", new { profileId = 2 });
        var target = Assert.Single(preview.Targets, item => item.Included);
        Assert.Equal("fixture1@example.invalid", target.CurrentAccountLabel);
        Assert.Equal("fixture2@example.invalid", target.TargetAccountLabel);
    }

    [Fact]
    public async Task CanonicalIdentityExportReturnsOnlyExpectedHashesWithoutChangingAuth()
    {
        await using var fixture = new Fixture(withRemote: false, mixedCaseEmail: true);
        await fixture.Call<ManagementSnapshot>("snapshot");
        var paths = new[] { fixture.Paths.ActiveAuthFile, fixture.Paths.ConfigurationFile,
            fixture.Paths.ProfileAuthFile(1), fixture.Paths.ProfileAuthFile(2) };
        var before = paths.ToDictionary(path => path, path => SHA256.HashData(File.ReadAllBytes(path)));
        var identities = await fixture.Call<AccountIdentityHashes>("account.identityHashes");
        Assert.Equal(new[] { 1, 2 }, identities.Profiles.Select(profile => profile.ProfileId));
        foreach (var profile in identities.Profiles)
        {
            Assert.Equal(Hash("fixture-account-" + profile.ProfileId), profile.AccountIdSha256);
            Assert.Equal(Hash($"fixture{profile.ProfileId}@example.invalid"), profile.EmailSha256);
        }
        Assert.Equal(Hash(fixture.Paths.ClaudeHome), identities.ClaudeHomeSha256);
        Assert.False(string.IsNullOrWhiteSpace(identities.ConfigurationRevision));
        var json = JsonSerializer.Serialize(identities, Wire.Json);
        foreach (var raw in new[] { "fixture-account", "example.invalid", "synthetic-refresh", "synthetic-access", fixture.Paths.ClaudeHome })
            Assert.DoesNotContain(raw, json);
        using var document = JsonDocument.Parse(json);
        Assert.Equal(new[] { "profiles", "configurationRevision", "claudeHomeSha256" }, document.RootElement.EnumerateObject().Select(property => property.Name));
        Assert.All(document.RootElement.GetProperty("profiles").EnumerateArray(), profile =>
            Assert.Equal(new[] { "profileId", "accountIdSha256", "emailSha256" }, profile.EnumerateObject().Select(property => property.Name)));
        foreach (var path in paths) Assert.Equal(before[path], SHA256.HashData(File.ReadAllBytes(path)));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task CanonicalIdentityExportRefusesAccountsRequiringLogin(bool pending, bool needsLogin)
    {
        await using var fixture = new Fixture(withRemote: false);
        await fixture.Call<ManagementSnapshot>("snapshot");
        var store = new ConfigurationStore(fixture.Paths);
        var configuration = store.LoadOrCreate();
        configuration.Accounts[1].IsPending = pending;
        configuration.Accounts[1].NeedsLogin = needsLogin;
        store.Save(configuration);
        var failure = await Assert.ThrowsAsync<RpcException>(() => fixture.Call<AccountIdentityHashes>("account.identityHashes"));
        Assert.Equal("account_unavailable", failure.Code);
        Assert.Equal("fixture-account-1", fixture.Auth.ReadActiveAccountId());
    }

    [Fact]
    public async Task FailedNewLoginStillReportsItsReservedProfileAndPreservesActiveAuth()
    {
        // This fixture's claude executable exits before any URL/browser callback.
        await using var fixture = new Fixture(withRemote: true);
        var result = await fixture.Wait(await fixture.Call<OperationView>("account.add"));
        Assert.Equal("failed", result.State);
        Assert.Equal("login", result.Kind);
        Assert.Equal(3, result.ProfileId);
        Assert.Equal("fixture-account-1", fixture.Auth.ReadActiveAccountId());
        Assert.DoesNotContain(new ConfigurationStore(fixture.Paths).LoadOrCreate().Accounts, account => account.Id == 3);
        Assert.False(new WeeklyAnchorStore(fixture.Paths).Load().Preferences.IsEnabled(3));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FirstSuccessfulRegistrationEnablesWeeklyMessagesAndReimportPreservesOptOut(bool existingPending)
    {
        await using var fixture = new Fixture(withRemote: false);
        await fixture.Call<ManagementSnapshot>("snapshot");
        var weekly = new WeeklyAnchorStore(fixture.Paths);
        var state = weekly.Load();
        state.Preferences.SetEnabled(2, true);
        state.Records[2] = new() { LastSuccessAt = DateTimeOffset.UtcNow.AddDays(-1) };
        weekly.Save(state);
        if (existingPending)
        {
            var config = new ConfigurationStore(fixture.Paths);
            Assert.Equal(3, config.ReserveAccount(config.LoadOrCreate()).Id);
        }
        var source = Path.Combine(fixture.Paths.LoginSessionsDirectory, "source-3.json");
        WindowsPathSafety.WritePrivateBytes(source, Contracts.Bytes(Contracts.Create(
            "fixture-account-3", "synthetic-access-3", "synthetic-refresh-3", email: "fixture3@example.invalid",
            expiresAt: DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeMilliseconds())));
        var parameters = existingPending ? (object)new { path = source, profileId = 3 } : new { path = source };
        var result = await fixture.Wait(await fixture.Call<OperationView>("account.import", parameters));
        Assert.True(result.State == "completed", result.Message);
        var saved = weekly.Load();
        Assert.Equal(new[] { 2, 3 }, saved.Preferences.EnabledProfileIds.Order());
        Assert.Equal(state.Records[2].LastSuccessAt, saved.Records[2].LastSuccessAt);
        Assert.False(new ConfigurationStore(fixture.Paths).LoadOrCreate().Accounts.Single(a => a.Id == 3).IsPending);
        var settings = JsonSerializer.SerializeToElement(await fixture.Call<object>("settings.get"), Wire.Json);
        Assert.Contains(3, settings.GetProperty("weekly").GetProperty("preferences").GetProperty("enabledProfileIds")
            .EnumerateArray().Select(id => id.GetInt32()));

        Assert.Equal("completed", (await fixture.Wait(await fixture.Call<OperationView>("weekly.set",
            new { profileId = 3, enabled = false }))).State);
        Assert.Equal("completed", (await fixture.Wait(await fixture.Call<OperationView>("account.import",
            new { path = source, profileId = 3 }))).State);
        Assert.Equal(new[] { 2 }, weekly.Load().Preferences.EnabledProfileIds);
        Assert.Equal("fixture-account-1", fixture.Auth.ReadActiveAccountId());
    }

    [Fact]
    public async Task FailedImportDoesNotEnableWeeklyMessages()
    {
        await using var fixture = new Fixture(withRemote: false);
        var source = Path.Combine(fixture.Paths.LoginSessionsDirectory, "invalid.json");
        File.WriteAllText(source, "{}");
        var result = await fixture.Wait(await fixture.Call<OperationView>("account.import", new { path = source }));
        Assert.Equal("failed", result.State);
        Assert.Empty(new WeeklyAnchorStore(fixture.Paths).Load().Preferences.EnabledProfileIds);
    }

    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    [Fact]
    public async Task ProductionLocalPreviewApplyAndDuplicateRequestRoundTripRealAuthFiles()
    {
        await using var fixture = new Fixture(withRemote: false);
        var before = await fixture.Call<ManagementSnapshot>("snapshot");
        Assert.False(before.IsDemo);
        Assert.Equal(1, before.ActiveProfileId);
        foreach (var target in new[] { 2, 1 })
        {
            var preview = await fixture.Call<SwitchPreview>("preview", new { profileId = target });
            Assert.True(preview.CanApply, preview.BlockingReason);
            Assert.Equal("fixture-account-" + (target == 2 ? 1 : 2), fixture.Auth.ReadActiveAccountId());
            var request = new { previewId = preview.PreviewId, requestId = Guid.NewGuid().ToString("N") };
            var operation = await fixture.Call<OperationView>("apply", request);
            var duplicate = await fixture.Call<OperationView>("apply", request);
            Assert.Equal(operation.Id, duplicate.Id);
            Assert.Equal("completed", (await fixture.Wait(operation)).State);
            Assert.Equal("fixture-account-" + target, fixture.Auth.ReadActiveAccountId());
            Assert.Equal(target, (await fixture.Call<ManagementSnapshot>("snapshot")).ActiveProfileId);
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(fixture.Paths.ActiveAuthFile));
        }
    }

    [Fact]
    public async Task ProductionStalePreviewCannotWriteAfterAliasChanges()
    {
        await using var fixture = new Fixture(withRemote: false);
        var preview = await fixture.Call<SwitchPreview>("preview", new { profileId = 2 });
        await fixture.Call<object>("account.rename", new { profileId = 2, alias = "변경" });
        var result = await Assert.ThrowsAsync<RpcException>(() => fixture.Call<OperationView>("apply",
            new { previewId = preview.PreviewId, requestId = Guid.NewGuid().ToString("N") }));
        Assert.Equal("stale_preview", result.Code);
        Assert.Equal("fixture-account-1", fixture.Auth.ReadActiveAccountId());
    }

    [Fact]
    public async Task ProductionSshBoundaryRunsRealHelperAndRollsBackVerificationFailure()
    {
        await using var fixture = new Fixture(withRemote: true);
        var preview = await fixture.Call<SwitchPreview>("preview", new { profileId = 2 });
        Assert.True(preview.CanApply, preview.BlockingReason);
        Assert.Equal(2, preview.Targets.Count(t => t.Included));
        var result = await fixture.Apply(preview);
        Assert.Equal("completed", result.State);
        Assert.Equal("fixture-account-2", fixture.Auth.ReadActiveAccountId());
        Assert.Equal("fixture-account-2", fixture.RemoteAccount());
        using (var auth = JsonDocument.Parse(File.ReadAllText(fixture.RemoteAuth)))
            Assert.True(string.IsNullOrEmpty(auth.RootElement.GetProperty("claudeAiOauth").GetProperty("refreshToken").GetString()));
        Assert.Equal("completed", (await fixture.Apply(await fixture.Call<SwitchPreview>("preview", new { profileId = 1 }))).State);

        // Failure after the remote write exercises real coordinator rollback on both sides.
        File.WriteAllText(fixture.FailVerification, "fail once");
        result = await fixture.Apply(await fixture.Call<SwitchPreview>("preview", new { profileId = 2 }));
        Assert.Equal("failed", result.State);
        Assert.All(result.Targets, target => Assert.Equal("restored", target.State));
        Assert.Equal("fixture-account-1", fixture.Auth.ReadActiveAccountId());
        Assert.Equal("fixture-account-1", fixture.RemoteAccount());

        // The token page's history learns when each device changed account, from completed switches only,
        // and what account the remote device was seen to use.
        var history = File.ReadAllLines(Path.Combine(fixture.Paths.StateRoot, "quota-history.jsonl"));
        var switches = history.Where(line => line.Contains("\"Kind\":\"switch\"")).ToArray();
        Assert.Equal(2, switches.Length);
        Assert.Contains("\"ProfileId\":2", switches[0]);
        Assert.Contains("\"local\"", switches[0]);
        Assert.Contains("\"ssh:", switches[0]);
        Assert.Contains(history, line => line.Contains("\"DeviceId\":\"ssh:") && line.Contains("\"ProfileId\":1"));
    }

    [Fact]
    public async Task ProductionPreviewStreamsBundledReadonlyHelperWithoutUsingInstalledOldHelper()
    {
        await using var fixture = new Fixture(withRemote: true);
        File.WriteAllText(fixture.RemoteHelper, "#!/bin/sh\nexit 91\n");
        var before = File.ReadAllText(fixture.RemoteAuth);
        var preview = await fixture.Call<SwitchPreview>("preview", new { profileId = 2 });
        Assert.True(preview.CanApply, preview.BlockingReason);
        Assert.True(File.Exists(fixture.StreamedStatus));
        Assert.Equal(before, File.ReadAllText(fixture.RemoteAuth));
        Assert.Equal("#!/bin/sh\nexit 91\n", File.ReadAllText(fixture.RemoteHelper));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OptionalOfflineDeviceDoesNotBlockPreviewOrBecomeAStalePreview(bool offlineBeforePreview)
    {
        await using var fixture = new Fixture(withRemote: true);
        var devices = await fixture.Call<List<SshDeviceConfiguration>>("device.list");
        var device = Assert.Single(devices);
        device.SkipOnSwitchFailure = true;
        var saved = await fixture.Call<SshDeviceConfiguration>("device.save", new { device });
        Assert.True(saved.Enabled);
        Assert.True(saved.SkipOnSwitchFailure);
        Assert.True(Assert.Single(new ConfigurationStore(fixture.Paths).LoadOrCreate().Devices).SkipOnSwitchFailure);
        if (offlineBeforePreview) File.WriteAllText(fixture.Offline, "offline");
        var preview = await fixture.Call<SwitchPreview>("preview", new { profileId = 2 });
        Assert.True(preview.CanApply, preview.BlockingReason);
        var remote = Assert.Single(preview.Targets, target => target.Kind == "ssh");
        Assert.True(remote.SkipOnSwitchFailure);
        Assert.Equal(!offlineBeforePreview, remote.IsReachable);
        File.WriteAllText(fixture.Offline, "offline");
        var result = await fixture.Apply(preview);
        Assert.Equal("completed", result.State);
        Assert.Equal("fixture-account-2", fixture.Auth.ReadActiveAccountId());
        Assert.Equal("fixture-account-1", fixture.RemoteAccount());
        Assert.Equal("skipped", result.Targets.Single(target => target.Id == "ssh:fixture").State);
        Assert.Equal("verified", result.Targets.Single(target => target.Id == "local").State);
        // A new switch remains available, and required devices still block when offline.
        Assert.True((await fixture.Call<SwitchPreview>("preview", new { profileId = 1 })).CanApply);
        device.SkipOnSwitchFailure = false;
        await fixture.Call<SshDeviceConfiguration>("device.save", new { device });
        Assert.False((await fixture.Call<SwitchPreview>("preview", new { profileId = 1 })).CanApply);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OptionalVerificationFailureRestoresOnlyRemoteAndLeavesLocalApplied(bool failRestore)
    {
        await using var fixture = new Fixture(withRemote: true, optionalRemote: true);
        File.WriteAllText(fixture.FailVerification, "fail once");
        if (failRestore) File.WriteAllText(fixture.FailRestore, "reject remote restore");
        var result = await fixture.Apply(await fixture.Call<SwitchPreview>("preview", new { profileId = 2 }));
        Assert.Equal("completed", result.State);
        Assert.Equal("fixture-account-2", fixture.Auth.ReadActiveAccountId());
        Assert.Equal("fixture-account-" + (failRestore ? 2 : 1), fixture.RemoteAccount());
        Assert.Equal("skipped", result.Targets.Single(target => target.Id == "ssh:fixture").State);
        Assert.True((await fixture.Call<SwitchPreview>("preview", new { profileId = 1 })).CanApply);
    }

    [Fact]
    public async Task SingleSshAndLocalPreviewsChangeOnlyTheirExplicitTarget()
    {
        await using var fixture = new Fixture(withRemote: true);
        var preview = await fixture.Call<SwitchPreview>("preview", new { profileId = 2, deviceId = "ssh:fixture" });
        Assert.Equal("ssh:fixture", Assert.Single(preview.Targets, t => t.Included).Id);
        Assert.Equal("completed", (await fixture.Apply(preview)).State);
        Assert.Equal("fixture-account-1", fixture.Auth.ReadActiveAccountId());
        Assert.Equal("fixture-account-2", fixture.RemoteAccount());
        preview = await fixture.Call<SwitchPreview>("preview", new { profileId = 2, deviceId = "local" });
        Assert.Equal("local", Assert.Single(preview.Targets, t => t.Included).Id);
        Assert.Equal("completed", (await fixture.Apply(preview)).State);
        Assert.Equal("fixture-account-2", fixture.Auth.ReadActiveAccountId());
        Assert.Equal("fixture-account-2", fixture.RemoteAccount());
    }

    [Fact]
    public async Task ForegroundSwitchInterruptsBlockedBackgroundSyncBeforeLockTimeout()
    {
        await using var fixture = new Fixture(withRemote: true, blockBackgroundSync: true);
        var preview = await fixture.Call<SwitchPreview>("preview", new { profileId = 2, deviceId = "local" });
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!File.Exists(fixture.SyncEntered))
        {
            Assert.True(DateTime.UtcNow < deadline, "Background sync did not reach the lock-holding phase.");
            await Task.Delay(25);
        }
        var started = System.Diagnostics.Stopwatch.StartNew();
        var result = await fixture.Apply(preview).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("completed", result.State);
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(10));
        Assert.Equal("fixture-account-2", fixture.Auth.ReadActiveAccountId());
        Assert.Equal("fixture-account-1", fixture.RemoteAccount());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QuitDuringSwitchDrainsRollbackAndPreservesRecoveryResult(bool failRollback)
    {
        await using var fixture = new Fixture(withRemote: true);
        var preview = await fixture.Call<SwitchPreview>("preview", new { profileId = 2 });
        File.WriteAllText(fixture.BlockVerification, "pause after both targets were written");
        if (failRollback) File.WriteAllText(fixture.FailRestore, "reject synthetic remote restore");
        var accepted = await fixture.Call<OperationView>("apply", new
        {
            previewId = preview.PreviewId, requestId = Guid.NewGuid().ToString("N"),
        });
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!File.Exists(fixture.VerificationEntered))
        {
            Assert.True(DateTime.UtcNow < deadline, "Synthetic remote verification was not reached.");
            await Task.Delay(25);
        }
        Assert.Equal("fixture-account-2", fixture.Auth.ReadActiveAccountId());
        Assert.Equal("fixture-account-2", fixture.RemoteAccount());
        Assert.True((await fixture.Call<ManagementSnapshot>("snapshot")).IsBusy);

        var response = JsonSerializer.SerializeToElement(await fixture.Call<object>("quit"), Wire.Json);
        Assert.True(response.GetProperty("shutdownRequested").GetBoolean());
        Assert.False(response.TryGetProperty("stopped", out _));
        Assert.True(fixture.ShutdownRequested);
        foreach (var method in new[] { "refresh", "account.rename" })
        {
            var rejected = await Assert.ThrowsAsync<RpcException>(() => fixture.Call<object>(method,
                new { profileId = 1, alias = "must not be saved" }));
            Assert.Equal("shutting_down", rejected.Code);
        }
        await fixture.StopAsync().WaitAsync(TimeSpan.FromSeconds(20));

        var result = await fixture.Call<OperationView>("operation", new { operationId = accepted.Id });
        Assert.Equal(failRollback ? "recoveryRequired" : "failed", result.State);
        Assert.Equal("fixture-account-1", fixture.Auth.ReadActiveAccountId());
        Assert.Equal("fixture-account-" + (failRollback ? 2 : 1), fixture.RemoteAccount());
        Assert.Equal("restored", result.Targets.Single(target => target.Id == "local").State);
        Assert.Equal(failRollback ? "recoveryRequired" : "restored",
            result.Targets.Single(target => target.Id == "ssh:fixture").State);
        var persisted = new AccountSwitchCoordinator(fixture.Paths).ReadOperation();
        Assert.NotNull(persisted);
        Assert.Equal(result.State, persisted.State);
        Assert.Equal(accepted.Id, persisted.Id);
        Assert.Equal("계정1", new ConfigurationStore(fixture.Paths).LoadOrCreate().Accounts[0].CustomAlias);
    }

    [Fact]
    public async Task QuitRejectsMutationAlreadyWaitingForControllerLock()
    {
        await using var fixture = new Fixture(withRemote: false);
        await fixture.Call<ManagementSnapshot>("snapshot");
        var lease = await ControllerMutationLock.AcquireAsync(fixture.Paths);
        Task<object> mutation;
        try
        {
            mutation = fixture.Call<object>("account.rename", new { profileId = 1, alias = "too late" });
            Assert.False(mutation.IsCompleted);
            await fixture.Call<object>("quit");
        }
        finally { lease.Dispose(); }
        var rejected = await Assert.ThrowsAsync<RpcException>(() => mutation);
        Assert.Equal("shutting_down", rejected.Code);
        Assert.Equal("계정1", new ConfigurationStore(fixture.Paths).LoadOrCreate().Accounts[0].CustomAlias);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "syncbar-production-integration-" + Guid.NewGuid().ToString("N"));
        private readonly Dictionary<string, string?> _previous = [];
        private readonly ManagementService _service;
        public WindowsPaths Paths { get; }
        public AuthStore Auth { get; }
        public string RemoteAuth => Path.Combine(_root, "remote", ".claude", ".credentials.json");
        public string RemoteConfig => Path.Combine(_root, "remote", ".claude.json");
        public string FailVerification => Path.Combine(_root, "fail-verification");
        public string RemoteHelper => Path.Combine(_root, "remote", ".local", "bin", "claude-switch");
        public string StreamedStatus => Path.Combine(_root, "streamed-status");
        public string BlockVerification => Path.Combine(_root, "block-verification");
        public string VerificationEntered => BlockVerification + ".entered";
        public string FailRestore => Path.Combine(_root, "fail-restore");
        public string Offline => Path.Combine(_root, "offline");
        public string BlockSync => Path.Combine(_root, "block-sync");
        public string SyncEntered => BlockSync + ".entered";
        public bool ShutdownRequested => _service.ShutdownRequested;
        public Task StopAsync() => _service.StopAsync();

        public Fixture(bool withRemote, bool mixedCaseEmail = false, bool optionalRemote = false, bool blockBackgroundSync = false,
            Func<string, string>? usage = null, Action<string>? notify = null, ResumeHooks? resume = null)
        {
            Paths = new(Path.Combine(_root, "home"), Path.Combine(_root, "data"), Path.Combine(_root, "runtime"));
            Paths.EnsureDirectories();
            Directory.CreateDirectory(Paths.RuntimeDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var repository = FindRepository();
            foreach (var name in new[] { "claude-switch", "claude-syncbar-askpass", "usage-summary.mjs" })
            {
                var destination = Path.Combine(Paths.RuntimeDirectory, name);
                File.Copy(Path.Combine(repository, "Support", name), destination);
                File.SetUnixFileMode(destination, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            Auth = new(Paths);
            var accounts = new List<AccountProfile>();
            for (var id = 1; id <= 2; id++)
            {
                var email = mixedCaseEmail ? $"  FIXTURE{id}@Example.Invalid  " : $"fixture{id}@example.invalid";
                var source = Path.Combine(Paths.LoginSessionsDirectory, $"source-{id}.json");
                WindowsPathSafety.WritePrivateBytes(source, Contracts.Bytes(Contracts.Create(
                    "fixture-account-" + id, "synthetic-access-" + id, "synthetic-refresh-" + id, email: email,
                    expiresAt: DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeMilliseconds())));
                Auth.ImportAuth(source, id);
                accounts.Add(new() { Id = id, Email = $"fixture{id}@example.invalid", CustomAlias = "계정" + id });
            }
            Auth.SwitchActive(1);
            var bin = Path.Combine(_root, "bin");
            Directory.CreateDirectory(bin, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            // No test may reach a real Claude Code install: this stand-in is first on PATH.
            Script(Path.Combine(bin, "claude"), "#!/bin/sh\n[ \"$1 $2\" = 'auth status' ] && exit 0\nexit 90\n");
            SetEnvironment("PATH", bin + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH"));
            var configuration = new AppConfiguration { Accounts = accounts, NextAccountId = 3 };
            if (withRemote)
            {
                PrepareRemote();
                configuration.Devices.Add(new() { Id = "fixture", DisplayName = "시험 서버", Host = "fixture.invalid", Username = "fixture", Authentication = "openSSHConfig", Enabled = true, SkipOnSwitchFailure = optionalRemote });
            }
            new ConfigurationStore(Paths).Save(configuration);
            if (blockBackgroundSync) File.WriteAllText(BlockSync, "pause background install-access");
            // The automatic switch watcher is driven step by step by the tests instead of by its timer.
            _service = new(Paths, new UsageService(new HttpClient(new UsageHandler(usage))), notify ?? (_ => { }), watch: false,
                resume ?? new ResumeHooks(_ => [], _ => false, _ => throw new InvalidOperationException("No test may continue a real session.")));
        }

        private void PrepareRemote()
        {
            var remote = Path.Combine(_root, "remote");
            var profiles = Path.Combine(remote, ".local", "share", "claude-switch", "profiles");
            foreach (var directory in new[] { profiles, Path.GetDirectoryName(RemoteAuth)!, Path.Combine(remote, ".local", "bin") })
                Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            for (var id = 1; id <= 2; id++)
                WindowsPathSafety.WritePrivateBytes(Path.Combine(profiles, id + ".auth.json"), JsonSerializer.SerializeToUtf8Bytes(Auth.CreateAccessOnlyCopy(id)));
            // The device already runs account 1, stored the way Claude Code stores a login.
            var remoteLogin = Auth.CreateAccessOnlyCopy(1);
            WindowsPathSafety.WritePrivateBytes(RemoteAuth, JsonSerializer.SerializeToUtf8Bytes(new { mcpOAuth = new { }, claudeAiOauth = remoteLogin.Oauth }));
            WindowsPathSafety.WritePrivateBytes(RemoteConfig, JsonSerializer.SerializeToUtf8Bytes(new { numStartups = 5, oauthAccount = remoteLogin.Account }));
            File.Copy(Paths.BundledClaudeSwitch, Path.Combine(remote, ".local", "bin", "claude-switch"));
            var bin = Path.Combine(_root, "bin");
            Script(Path.Combine(bin, "pgrep"), "#!/bin/sh\nexit 1\n");
            Script(Path.Combine(bin, "ssh"), """
                #!/usr/bin/python3
                import os, pathlib, shlex, subprocess, sys, time
                remote = pathlib.Path(os.environ['SYNCBAR_FIXTURE_REMOTE'])
                if pathlib.Path(os.environ['SYNCBAR_FIXTURE_OFFLINE']).exists():
                    raise SystemExit(255)
                command = sys.argv[-1]
                blocked_sync = pathlib.Path(os.environ['SYNCBAR_FIXTURE_BLOCK_SYNC'])
                if '__node install-access ' in command and blocked_sync.exists():
                    pathlib.Path(str(blocked_sync) + '.entered').write_text('background sync entered')
                    while blocked_sync.exists():
                        time.sleep(0.025)
                failure = pathlib.Path(os.environ['SYNCBAR_FIXTURE_FAIL'])
                blocked = pathlib.Path(os.environ['SYNCBAR_FIXTURE_BLOCK_VERIFY'])
                if command.endswith('__node verify 2') and blocked.exists():
                    pathlib.Path(str(blocked) + '.entered').write_text('verification reached')
                    while blocked.exists():
                        time.sleep(0.025)
                if command.endswith('__node switch 1 1') and pathlib.Path(os.environ['SYNCBAR_FIXTURE_FAIL_RESTORE']).exists():
                    raise SystemExit(45)
                if command.endswith('__node verify 2') and failure.exists():
                    failure.unlink()
                    raise SystemExit(44)
                environment = dict(os.environ, HOME=str(remote),
                                   CLAUDE_SWITCH_STATE_ROOT=str(remote / '.local/share/claude-switch'))
                environment.pop('CLAUDE_SWITCH_CONFIG_FILE', None)
                # The device uses Claude Code's default file layout below its own HOME.
                environment.pop('CLAUDE_CONFIG_DIR', None)
                arguments = shlex.split(command)
                if arguments == ['bash', '-l', '-s', '--', '__node', 'status-readonly']:
                    helper = sys.stdin.buffer.read()
                    assert b'status-readonly' in helper and b'node_status_readonly' in helper
                    pathlib.Path(os.environ['SYNCBAR_FIXTURE_STREAMED']).write_text('bundled helper received')
                    # Preserve this temporary HOME instead of sourcing the host login profile.
                    result = subprocess.run(['/bin/bash', '--noprofile', '--norc', '-s', '--',
                                             '__node', 'status-readonly'], input=helper, env=environment)
                    raise SystemExit(result.returncode)
                os.execve('/bin/bash', ['bash', '-c', command], environment)
                """ + "\n");
            Script(Path.Combine(bin, "scp"), """
                #!/usr/bin/python3
                import os, pathlib, shutil, sys
                remote = pathlib.Path(os.environ['SYNCBAR_FIXTURE_REMOTE'])
                relative = sys.argv[-1].split(':', 1)[1]
                assert relative.startswith('~/')
                destination = (remote / relative[2:]).resolve()
                assert destination.is_relative_to(remote.resolve())
                shutil.copyfile(sys.argv[-2], destination)
                """ + "\n");
            SetEnvironment("SYNCBAR_FIXTURE_REMOTE", remote);
            SetEnvironment("SYNCBAR_FIXTURE_FAIL", FailVerification);
            SetEnvironment("SYNCBAR_FIXTURE_STREAMED", StreamedStatus);
            SetEnvironment("SYNCBAR_FIXTURE_BLOCK_VERIFY", BlockVerification);
            SetEnvironment("SYNCBAR_FIXTURE_FAIL_RESTORE", FailRestore);
            SetEnvironment("SYNCBAR_FIXTURE_OFFLINE", Offline);
            SetEnvironment("SYNCBAR_FIXTURE_BLOCK_SYNC", BlockSync);
        }
        private static void Script(string path, string contents)
        {
            File.WriteAllText(path, contents);
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        private void SetEnvironment(string key, string value)
        {
            _previous[key] = Environment.GetEnvironmentVariable(key);
            Environment.SetEnvironmentVariable(key, value);
        }
        public string RemoteAccount()
        {
            using var document = JsonDocument.Parse(File.ReadAllText(RemoteConfig));
            return document.RootElement.GetProperty("oauthAccount").GetProperty("accountUuid").GetString()!;
        }
        public Task<bool> WatchOnce(DateTimeOffset? at = null) => _service.WatchOnceAsync(at ?? DateTimeOffset.UtcNow, CancellationToken.None);
        public int Resume(DateTimeOffset at) => _service.ResumeStopped(new AutoSwitchStore(Paths).Load(), at);
        public async Task<T> Call<T>(string method, object? parameters = null)
        {
            var result = await _service.DispatchAsync(method, JsonSerializer.SerializeToElement(parameters ?? new { }, Wire.Json), CancellationToken.None);
            return Assert.IsAssignableFrom<T>(result);
        }
        public async Task<OperationView> Apply(SwitchPreview preview) => await Wait(await Call<OperationView>("apply",
            new { previewId = preview.PreviewId, requestId = Guid.NewGuid().ToString("N") }));
        public async Task<OperationView> Wait(OperationView operation)
        {
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (!operation.IsComplete)
            {
                Assert.True(DateTime.UtcNow < deadline, "Production operation timed out.");
                await Task.Delay(25);
                operation = await Call<OperationView>("operation", new { operationId = operation.Id });
            }
            // The public completion and service busy flag must agree before the next action.
            while ((await Call<ManagementSnapshot>("snapshot")).IsBusy)
            {
                Assert.True(DateTime.UtcNow < deadline);
                await Task.Delay(25);
            }
            return operation;
        }
        public async ValueTask DisposeAsync()
        {
            await _service.StopAsync();
            _service.Dispose();
            foreach (var pair in _previous) Environment.SetEnvironmentVariable(pair.Key, pair.Value);
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }
        private static string FindRepository()
        {
            for (var directory = new DirectoryInfo(Environment.CurrentDirectory); directory is not null; directory = directory.Parent)
                if (File.Exists(Path.Combine(directory.FullName, "Support", "claude-switch"))) return directory.FullName;
            throw new InvalidOperationException("Run from this repository.");
        }
    }
    // Usage by access token when given; otherwise a payload with no Claude quotas in it.
    private sealed class UsageHandler(Func<string, string>? usage) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(usage is not null && request.RequestUri!.AbsolutePath.EndsWith("/usage", StringComparison.Ordinal)
                    ? usage(request.Headers.Authorization!.Parameter!)
                    : """{"plan_type":"plus","rate_limit":{"primary_window":{"used_percent":25,"limit_window_seconds":604800}}}"""),
            });
    }
}
