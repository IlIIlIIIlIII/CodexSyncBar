using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodexSyncBar.Windows.Core;

namespace CodexSyncBar.Windows.Core.Tests;

public sealed class WidgetContractTests
{
    private static DashboardSnapshot Fixture() => new()
    {
        ConfigurationRevision = "rev-1", ActiveProfileId = 1,
        Accounts = [new(1, "개인", "p***@example.test", false,
            new(new(15, DateTimeOffset.UtcNow.AddHours(1), 18000), new(62, null, 604800),
                3, [DateTimeOffset.UtcNow.AddDays(4)], DateTimeOffset.UtcNow)),
            new(2, "업무", "w***@example.test", false)],
        Devices = [new("windows", "Windows", "windows", 1, true), new("wsl:test", "Ubuntu", "wsl", 1, true)],
    };

    private static DashboardPipeHandlers Handlers(
        Func<int, string, CancellationToken, Task<SwitchOperation>>? switchAccount = null,
        DashboardSnapshot? snapshot = null) => new()
    {
        GetSnapshotAsync = _ => Task.FromResult(snapshot ?? Fixture()),
        RefreshUsageAsync = _ => Task.FromResult(snapshot ?? Fixture()),
        SwitchAccountAsync = switchAccount ?? ((id, _, _) => Task.FromResult(new SwitchOperation { Id = "operation-1", ProfileId = id, State = "completed" })),
        GetOperationStatusAsync = (_, _) => throw new NotSupportedException(),
        OpenSettingsAsync = _ => Task.CompletedTask,
    };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CardHasExplicitApplyAndRefreshAndSafeData(bool large)
    {
        var json = WidgetTemplates.Render(Fixture(), large);
        using var card = JsonDocument.Parse(json);
        var actions = card.RootElement.GetProperty("actions").EnumerateArray().ToArray();
        Assert.Contains(actions, a => a.GetProperty("verb").GetString() == "apply");
        Assert.Contains(actions, a => a.GetProperty("verb").GetString() == "refresh");
        Assert.DoesNotContain("access_token", json);
        Assert.DoesNotContain("refresh_token", json);
        if (large) Assert.Contains("p***@example.test", json); // Only the already masked secondary identity is exposed.
        Assert.Contains(card.RootElement.GetProperty("body").EnumerateArray(),
            e => e.GetProperty("type").GetString() == "Input.ChoiceSet");
        var apply = actions.Single(a => a.GetProperty("verb").GetString() == "apply");
        Assert.Equal("rev-1", apply.GetProperty("data").GetProperty("configurationRevision").GetString());
        Assert.Equal("auto", apply.GetProperty("associatedInputs").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FooterHasTwoShortActionsAndWindowsCountHasAUnit(bool large)
    {
        using var card = JsonDocument.Parse(WidgetTemplates.Render(Fixture(), large));
        var actions = card.RootElement.GetProperty("actions").EnumerateArray().ToArray();
        Assert.Equal(2, actions.Length);
        Assert.All(actions, a => Assert.True(a.GetProperty("title").GetString()!.Length <= 5));
        if (large)
        {
            Assert.Contains(TextValues(card.RootElement), t => t.Contains("Windows PC 1대"));
            Assert.DoesNotContain("Windows 1", TextValues(card.RootElement));
            Assert.Contains(Walk(card.RootElement), e => e.TryGetProperty("type", out var type) && type.GetString() == "ActionSet");
        }
    }

    [Fact]
    public void BusyCardRemovesAccountMutationControls()
    {
        using var card = JsonDocument.Parse(WidgetTemplates.Render(Fixture() with { IsBusy = true }, false));
        Assert.DoesNotContain(card.RootElement.GetProperty("actions").EnumerateArray(), a => a.GetProperty("verb").GetString() == "apply");
        Assert.DoesNotContain(card.RootElement.GetProperty("body").EnumerateArray(), e => e.GetProperty("type").GetString() == "Input.ChoiceSet");
    }

    [Fact]
    public void LargeCardShowsRemainingLimitsResetCreditsAndDeviceAggregate()
    {
        using var card = JsonDocument.Parse(WidgetTemplates.Render(Fixture(), true));
        var texts = TextValues(card.RootElement);
        Assert.Contains("85%", texts);
        Assert.Contains("38%", texts);
        Assert.Contains("3개", texts);
        Assert.Contains("장치 적용 · 2/2 적용됨", texts);
        Assert.DoesNotContain(texts, text => text.Contains("Spark", StringComparison.Ordinal));
        Assert.Contains(Walk(card.RootElement), action => action.TryGetProperty("verb", out var verb) && verb.GetString() == "settings");
    }

    [Fact]
    public void ErrorsKeepStableControlsAndShowSummaryWithAppAccess()
    {
        var message = new string('오', 180);
        var snapshot = Fixture() with
        {
            Accounts = Fixture().Accounts.Select(a => a with { DisplayName = new string('장', 70) }).ToArray(),
            Error = message,
        };
        using var card = JsonDocument.Parse(WidgetTemplates.Render(snapshot, false));
        var body = card.RootElement.GetProperty("body").EnumerateArray().ToArray();
        Assert.Equal(1, body[0].GetProperty("maxLines").GetInt32());
        Assert.Contains(TextValues(card.RootElement), text => text.Contains("연결 확인 필요"));
        Assert.DoesNotContain(message, TextValues(card.RootElement));
        var actions = card.RootElement.GetProperty("actions").EnumerateArray().ToArray();
        Assert.Equal(2, actions.Length);
        Assert.Contains(actions, a => a.GetProperty("verb").GetString() == "apply");
        Assert.Contains(actions, a => a.GetProperty("verb").GetString() == "refresh");
        Assert.Single(Walk(card.RootElement), element => element.TryGetProperty("type", out var type) && type.GetString() == "Input.ChoiceSet");
        using var large = JsonDocument.Parse(WidgetTemplates.Render(snapshot, true));
        Assert.Contains(TextValues(large.RootElement), text => text.Contains("연결 확인 필요"));
        Assert.Contains(Walk(large.RootElement), a => a.TryGetProperty("verb", out var verb) && verb.GetString() == "settings");
    }

    [Fact]
    public void BothSizesIncludeRelativeAndAbsoluteQuotaResetTimes()
    {
        var fixture = Fixture();
        using var medium = JsonDocument.Parse(WidgetTemplates.Render(fixture, false));
        var texts = TextValues(medium.RootElement);
        Assert.Contains("85%", texts);
        Assert.Contains("5시간 남음", texts);
        Assert.Contains(texts, text => text.Contains("갱신"));
        Assert.Contains(texts, text => text.Contains("후 초기화"));
        Assert.Contains(UsageFormatting.QuotaResetDate(fixture.Accounts[0].Usage!.Session!.ResetsAt), texts);
        using var large = JsonDocument.Parse(WidgetTemplates.Render(fixture, true));
        Assert.Contains(TextValues(large.RootElement), text => text.Contains("후 초기화"));
    }

    [Fact]
    public void OldUsageRetainsQuotaAndDateWithoutClaimingItIsFresh()
    {
        var fixture = Fixture();
        var account = fixture.Accounts[0];
        var old = account with { Usage = account.Usage! with { UpdatedAt = DateTimeOffset.UtcNow.AddDays(-1) } };
        using var card = JsonDocument.Parse(WidgetTemplates.Render(fixture with { Accounts = [old] }, false));
        var texts = TextValues(card.RootElement);
        Assert.Contains("85%", texts);
        Assert.Contains(texts, text => text.Contains("마지막 확인 값") && text.Contains(old.Usage!.UpdatedAt.ToLocalTime().ToString("MM/dd")));
        Assert.DoesNotContain(texts, text => text.Contains("최신 사용량"));
    }

    [Fact]
    public void MissingNonFiniteAndOutOfRangeUsageNeverInventsRemainingQuota()
    {
        var fixture = Fixture();
        var account = fixture.Accounts[0];
        var absent = account with { Usage = account.Usage! with { Session = null, Weekly = new(double.NaN, null, 604800) } };
        using var missing = JsonDocument.Parse(WidgetTemplates.Render(fixture with { Accounts = [absent] }, false));
        Assert.Single(TextValues(missing.RootElement), text => text == "—");
        Assert.DoesNotContain("5시간 남음", TextValues(missing.RootElement));
        Assert.DoesNotContain("0%", TextValues(missing.RootElement));
        var extreme = account with { Usage = account.Usage! with { Session = new(-10, null, 18000), Weekly = new(120, null, 604800) } };
        using var clamped = JsonDocument.Parse(WidgetTemplates.Render(fixture with { Accounts = [extreme] }, false));
        Assert.Contains("100%", TextValues(clamped.RootElement));
        Assert.Contains("0%", TextValues(clamped.RootElement));
        Assert.DoesNotContain(Walk(clamped.RootElement), element => element.TryGetProperty("type", out var type)
            && type.GetString() is "ProgressBar" or "Image");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WeeklyOnlyUsesOneFullWidthColumnWithoutFiveHourLabels(bool large)
    {
        var snapshot = Fixture();
        var account = snapshot.Accounts[0] with { Usage = snapshot.Accounts[0].Usage! with { Session = null } };
        using var card = JsonDocument.Parse(WidgetTemplates.Render(snapshot with { Accounts = [account] }, large));
        var meter = Walk(card.RootElement).Single(e => e.TryGetProperty("type", out var type) && type.GetString() == "ColumnSet"
            && e.GetProperty("columns")[0].GetProperty("items")[0].TryGetProperty("text", out var label) && label.GetString() == "주간 남음");
        Assert.Single(meter.GetProperty("columns").EnumerateArray());
        Assert.Equal("stretch", meter.GetProperty("columns")[0].GetProperty("width").GetString());
        Assert.DoesNotContain(TextValues(card.RootElement), text => text.Contains("5시간"));
        Assert.Contains("38%", TextValues(card.RootElement));
    }

    [Theory]
    [InlineData(0, "0개", "사용 가능한 초기화권이 없어요.")]
    [InlineData(null, "수량 미확인", "만료 정보 없음")]
    public void ResetCreditZeroAndUnknownAreDistinct(int? count, string expectedCount, string expectedDetail)
    {
        var snapshot = Fixture();
        var account = snapshot.Accounts[0] with { Usage = snapshot.Accounts[0].Usage! with { ResetCredits = count, ResetCreditExpirations = [] } };
        using var card = JsonDocument.Parse(WidgetTemplates.Render(snapshot with { Accounts = [account] }, true));
        var texts = TextValues(card.RootElement);
        Assert.Contains(expectedCount, texts);
        Assert.Contains(expectedDetail, texts);
        Assert.DoesNotContain(texts, text => text.Contains("개 ·") && text.Contains("일"));
        if (count is null) Assert.DoesNotContain("0개", texts);
    }

    [Fact]
    public void ResetCreditSectionShowsGroupedCountsCountdownAndAbsoluteExpiryWithBoundedOverflow()
    {
        var snapshot = Fixture();
        var first = DateTimeOffset.UtcNow.AddDays(2).AddHours(3).AddMinutes(14);
        var expirations = new[] { first, first, first.AddDays(2), first.AddDays(3) };
        var account = snapshot.Accounts[0] with { Usage = snapshot.Accounts[0].Usage! with { ResetCredits = 4, ResetCreditExpirations = expirations } };
        using var card = JsonDocument.Parse(WidgetTemplates.Render(snapshot with { Accounts = [account] }, true));
        var texts = TextValues(card.RootElement);
        Assert.Contains("4개", texts);
        Assert.Contains(texts, text => text.StartsWith("2개 · 2일 3시간 ") && text.EndsWith("분 남음"));
        Assert.Contains(UsageFormatting.ResetCreditExpiryGroups(expirations)[0].ExpiresAtText, texts);
        Assert.Contains("외 1개 만료 일정 · 앱에서 전체 보기", texts);
        Assert.Contains(Walk(card.RootElement), element => element.TryGetProperty("style", out var style) && style.GetString() == "emphasis");
        Assert.DoesNotContain(UsageFormatting.ResetCreditExpiryGroups(expirations)[2].ExpiresAtText, texts);
    }

    [Fact]
    public void UsageColorsAreSemanticAndKeepExplicitLabels()
    {
        var snapshot = Fixture();
        var account = snapshot.Accounts[0] with { Usage = snapshot.Accounts[0].Usage! with { Weekly = new(92, null, 604800) } };
        using var card = JsonDocument.Parse(WidgetTemplates.Render(snapshot with { Accounts = [account] }, false));
        var textBlocks = Walk(card.RootElement).Where(element => element.TryGetProperty("type", out var type) && type.GetString() == "TextBlock").ToArray();
        Assert.Equal("Good", textBlocks.Single(e => e.GetProperty("text").GetString() == "85%").GetProperty("color").GetString());
        Assert.Equal("Warning", textBlocks.Single(e => e.GetProperty("text").GetString() == "8%").GetProperty("color").GetString());
        Assert.Contains("주간 남음", TextValues(card.RootElement));
        Assert.All(textBlocks, e => Assert.DoesNotContain("#", e.GetProperty("color").GetString()));
    }

    private static string[] TextValues(JsonElement root) => Walk(root)
        .Where(element => element.TryGetProperty("type", out var type) && type.GetString() == "TextBlock")
        .Select(element => element.GetProperty("text").GetString()!).ToArray();

    private static IEnumerable<JsonElement> Walk(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            yield return element;
            foreach (var property in element.EnumerateObject())
                foreach (var child in Walk(property.Value)) yield return child;
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray())
                foreach (var child in Walk(item)) yield return child;
    }

    [Fact]
    public void EmptyCardOpensLoginAndEmailAliasIsMasked()
    {
        using var card = JsonDocument.Parse(WidgetTemplates.Render(new(), false));
        Assert.Contains(card.RootElement.GetProperty("actions").EnumerateArray(), a => a.GetProperty("verb").GetString() == "settings");
        var snapshot = Fixture() with { Accounts = [new(1, "sensitive@example.test", "s***@example.test", false)] };
        var json = WidgetTemplates.Render(snapshot, false);
        Assert.DoesNotContain("sensitive@example.test", json);
        Assert.Contains("s***@example.test", json);
    }

    [Fact]
    public void ApplyActionRoundTripsRevisionAndSelection()
    {
        var card = JsonNode.Parse(WidgetTemplates.Render(Fixture(), false))!;
        var data = card["actions"]![0]!["data"]!.DeepClone().AsObject();
        data["profileId"] = "2";
        var action = WidgetTemplates.ParseAction("apply", data.ToJsonString());
        Assert.Equal(2, action.ProfileId);
        Assert.Equal("rev-1", action.ConfigurationRevision);
    }

    [Theory]
    [InlineData("apply", "{}")]
    [InlineData("apply", "[]")]
    [InlineData("executeShell", "{}")]
    [InlineData("apply", "{\"requestId\":\"aabbccddeeff00112233445566778899\",\"profileId\":\"-1\",\"configurationRevision\":\"a\"}")]
    public void MalformedWidgetActionsCannotTriggerSwitch(string verb, string data) =>
        Assert.Throws<InvalidDataException>(() => WidgetTemplates.ParseAction(verb, data));

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(262145)]
    public async Task ProtocolRejectsLengthBeforeAllocatingPayload(int size)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, size);
        using var stream = new MemoryStream(bytes);
        await Assert.ThrowsAsync<InvalidDataException>(() => DashboardPipeProtocol.ReadAsync<DashboardPipeRequest>(stream, 4096, default));
    }

    [Fact]
    public async Task ProtocolRejectsUnknownInputFields()
    {
        var json = Encoding.UTF8.GetBytes("{\"method\":\"GetSnapshot\",\"requestId\":\"aabbccddeeff00112233445566778899\",\"command\":\"unsafe\"}");
        using var stream = new MemoryStream();
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, json.Length);
        stream.Write(header); stream.Write(json); stream.Position = 0;
        await Assert.ThrowsAsync<JsonException>(() => DashboardPipeProtocol.ReadAsync<DashboardPipeRequest>(stream, 4096, default));
    }

    [Fact]
    public async Task PipeRoundTripsAndRejectsStaleConfiguration()
    {
        var name = "CodexSyncBar.Test." + Guid.NewGuid().ToString("N");
        var invoked = 0;
        await using var server = new DashboardPipeServer(Handlers((id, _, _) =>
        {
            Interlocked.Increment(ref invoked);
            return Task.FromResult(new SwitchOperation { Id = "one", ProfileId = id, State = "completed" });
        }), name);
        server.Start();
        var client = new DashboardPipeClient(name);
        Assert.Equal("rev-1", (await client.GetSnapshotAsync()).ConfigurationRevision);
        var failure = await Assert.ThrowsAsync<DashboardPipeException>(() => client.SwitchAccountAsync(2, "old"));
        Assert.Equal("stale_configuration", failure.Code);
        Assert.Equal(0, invoked);
        Assert.Equal(2, (await client.SwitchAccountAsync(2, "rev-1")).ProfileId);
        Assert.Equal(1, invoked);
    }

    [Fact]
    public async Task ConcurrentDuplicateRequestInvokesSwitchOnlyOnce()
    {
        var name = "CodexSyncBar.Test." + Guid.NewGuid().ToString("N");
        var invoked = 0;
        await using var server = new DashboardPipeServer(Handlers(async (id, _, ct) =>
        {
            Interlocked.Increment(ref invoked);
            await Task.Delay(80, ct);
            return new() { Id = "receipt", ProfileId = id, State = "completed" };
        }), name);
        server.Start();
        var client = new DashboardPipeClient(name);
        var request = new DashboardPipeRequest("SwitchAccount", Guid.NewGuid().ToString("N"), 2, "rev-1");
        var results = await Task.WhenAll(client.SendAsync(request), client.SendAsync(request));
        Assert.All(results, r => Assert.Equal("receipt", r.Operation!.Id));
        Assert.Equal(1, invoked);
        var conflict = await Assert.ThrowsAsync<DashboardPipeException>(() => client.SendAsync(request with { ProfileId = 1 }));
        Assert.Equal("request_conflict", conflict.Code);
    }

    [Fact]
    public async Task MalformedClientDoesNotStopPipeServer()
    {
        var name = "CodexSyncBar.Test." + Guid.NewGuid().ToString("N");
        await using var server = new DashboardPipeServer(Handlers(), name);
        server.Start();
        await using (var raw = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous))
        {
            await raw.ConnectAsync(2000);
            await raw.WriteAsync(new byte[] { 255, 255, 255, 127 });
        }
        Assert.Equal(1, (await new DashboardPipeClient(name).GetSnapshotAsync()).ActiveProfileId);
    }

    [Fact]
    public async Task ServerErrorDoesNotExposeRawExceptionOrCredentials()
    {
        var name = "CodexSyncBar.Test." + Guid.NewGuid().ToString("N");
        await using var server = new DashboardPipeServer(Handlers((_, _, _) => throw new Exception("access_token=super-secret")), name);
        server.Start();
        var failure = await Assert.ThrowsAsync<DashboardPipeException>(() => new DashboardPipeClient(name).SwitchAccountAsync(2, "rev-1"));
        Assert.DoesNotContain("super-secret", failure.Message);
        Assert.Equal("operation_failed", failure.Code);
    }

    [Fact]
    public async Task ClientDisconnectDoesNotCancelAnAcceptedMutation()
    {
        var name = "CodexSyncBar.Test." + Guid.NewGuid().ToString("N");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new DashboardPipeServer(Handlers(async (id, _, ct) =>
        {
            started.SetResult();
            await release.Task;
            completed.SetResult(ct.IsCancellationRequested);
            return new() { Id = "accepted", ProfileId = id, State = "completed" };
        }), name);
        server.Start();
        using var cancellation = new CancellationTokenSource();
        var action = new DashboardPipeClient(name).SwitchAccountAsync(2, "rev-1", cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => action);
        release.SetResult();
        Assert.False(await completed.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("rev-1", (await new DashboardPipeClient(name).GetSnapshotAsync()).ConfigurationRevision);
    }

    [Theory]
    [InlineData(true, false, "busy")]
    [InlineData(false, true, "account_unavailable")]
    public async Task BusyOrLoggedOutAccountsDoNotReachMutation(bool busy, bool login, string code)
    {
        var name = "CodexSyncBar.Test." + Guid.NewGuid().ToString("N");
        var fixture = Fixture() with { IsBusy = busy, Accounts = [new(2, "업무", "w***@example.test", login)] };
        var invoked = 0;
        await using var server = new DashboardPipeServer(Handlers((_, _, _) =>
        {
            invoked++;
            throw new InvalidOperationException();
        }, fixture), name);
        server.Start();
        var error = await Assert.ThrowsAsync<DashboardPipeException>(() => new DashboardPipeClient(name).SwitchAccountAsync(2, "rev-1"));
        Assert.Equal(code, error.Code);
        Assert.Equal(0, invoked);
    }
}
