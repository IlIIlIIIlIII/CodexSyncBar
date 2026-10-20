using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeSyncBar.App.Controls;
using ClaudeSyncBar.App.Model;
using ClaudeSyncBar.Windows.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace ClaudeSyncBar.App.Pages;

/// <summary>
/// Tokens recorded in Claude Code's session logs on each device, priced at API
/// rates as an estimate. It is collected when the page opens and on request.
/// </summary>
public sealed class TokensPage : SyncPage
{
    private static readonly HashSet<string> Open = ["local"];
    private JsonObject? _result;
    private string? _error;
    private bool _loading;

    public TokensPage()
    {
        Loaded += (_, _) => { if (_result is null && !_loading) _ = LoadAsync(); };
    }

    protected override string Signature() => string.Join("|", _result?.ToJsonString(), _error, _loading, State.Error);

    private async Task LoadAsync()
    {
        _loading = true;
        Redraw();
        try
        {
            _result = await State.Client.CallAsync("tokens", timeout: TimeSpan.FromSeconds(120)) as JsonObject;
            _error = null;
        }
        catch (ServiceError error)
        {
            _error = error.Message;
        }
        finally
        {
            _loading = false;
            Redraw();
        }
    }

    protected override void Render()
    {
        var again = Ui.Button(_loading ? "집계 중…" : "다시 집계", Glyphs.Refresh, click: async (_, _) => await LoadAsync());
        again.IsEnabled = !_loading;
        Header("토큰 집계", "Claude Code 세션 기록을 API 요금으로 환산한 추정치예요. 구독 요금이나 실제 청구 금액과 달라요.", again);
        if (_error is not null) Add(new InfoBar { IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Error, Message = _error });
        if (_result is null)
        {
            if (_loading)
                Add(Ui.Card(Ui.Stack(12, true, new ProgressRing { IsActive = true, Width = 20, Height = 20 }, Ui.Text("세션 기록을 집계하고 있어요…", secondary: true))));
            return;
        }

        var devices = (_result["devices"] as JsonArray)?.OfType<JsonObject>().ToList() ?? [];
        Add(Totals(devices));
        var list = new StackPanel { Spacing = 4 };
        AutomationProperties.SetName(list, "장치별 토큰");
        foreach (var device in devices) list.Children.Add(Device(device));
        if (devices.Count == 0) list.Children.Add(Ui.Card(Ui.Text("집계된 기록이 없어요.", secondary: true)));
        Add(list);
        if (QuotaFormat.Time(_result["collectedAt"]) is { } collected)
            Add(Ui.Caption($"{collected.ToLocalTime():M월 d일 HH:mm} 집계 · 요금을 모르는 모델은 비용에서 빠져요."));
    }

    private static long Count(JsonNode? summary, string key) => summary?[key] is JsonValue value && value.TryGetValue<long>(out var count) ? count : 0;

    private Border Totals(IReadOnlyList<JsonObject> devices)
    {
        long Sum(string key) => devices.Sum(device => Count(device["summary"], key));
        var cost = QuotaFormat.Number(_result?["estimatedCostUsd"]) ?? devices.Sum(device => QuotaFormat.Number(device["estimatedCostUsd"]) ?? 0);
        static StackPanel Cell(string title, string value, double size, string? unit = null)
        {
            var line = Ui.Stack(6, true, Ui.Text(value, size, strong: size > 20, wrap: false));
            if (unit is not null)
            {
                var caption = Ui.Caption(unit, wrap: false);
                caption.VerticalAlignment = VerticalAlignment.Bottom;
                caption.Margin = new Thickness(0, 0, 0, 5);
                line.Children.Add(caption);
            }

            var cell = Ui.Stack(2, false, Ui.Caption(title, wrap: false), line);
            cell.MinWidth = size > 20 ? 120 : 72;
            return cell;
        }

        var cells = Ui.Wrap(28,
            Cell("전체 토큰", Tokens(Sum("totalTokens")), 28, "토큰"),
            Cell("추정 비용 (USD)", Dollars(cost), 28),
            Cell("입력", Tokens(Sum("inputTokens")), 18),
            Cell("캐시 입력", Tokens(Sum("cachedInputTokens")), 18),
            Cell("출력", Tokens(Sum("outputTokens")), 18));
        cells.LineSpacing = 16;
        AutomationProperties.SetName(cells, "전체 합계");
        return Ui.Card(cells);
    }

    private UIElement Device(JsonObject device)
    {
        var id = AppState.Str(device["id"]) ?? "";
        var local = id == "local";
        var name = AppState.Str(device["displayName"]) ?? "장치";
        var summary = device["summary"] as JsonObject;
        var failed = AppState.Str(device["error"]) is not null || !QuotaFormat.Flag(device["isReachable"]);
        var buckets = (summary?["buckets"] as JsonArray)?.OfType<JsonObject>()
            .Select(bucket => bucket.Deserialize<ModelTokenUsage>()).OfType<ModelTokenUsage>()
            .OrderByDescending(bucket => bucket.TotalTokens).ToList() ?? [];
        var open = Open.Contains(id) && buckets.Count > 0 && !failed;
        UIElement detail;
        if (failed)
        {
            var reason = Ui.Caption(AppState.Str(device["error"]) ?? "연결 실패 · 집계하지 못했어요");
            reason.Style = Ui.Style("SyncBarToneWarning");
            reason.FontSize = 12;
            detail = Ui.Columns(4, 1, Ui.Glyph(Glyphs.Warning, 12, "SyncBarGlyphWarning"), reason);
        }
        else
        {
            detail = Ui.Caption((local ? "Windows" : "SSH") + (summary is null ? " · 기록 없음" : " · 집계 완료"));
        }

        var text = Ui.Stack(1, false, Ui.Text(name, wrap: false), detail);
        UIElement amount = failed ? Ui.Text("—", secondary: true)
            : Ui.Text($"{Tokens(Count(summary, "totalTokens"))} 토큰 · {Dollars(QuotaFormat.Number(device["estimatedCostUsd"]) ?? 0)}", wrap: false);
        UIElement? action = failed
            ? Ui.Button("다시 시도", click: async (_, _) => await LoadAsync())
            : buckets.Count > 0 ? Ui.ExpandButton(open, name, () => { if (!Open.Remove(id)) Open.Add(id); Redraw(); }) : null;
        if (action is Button retry && failed) retry.IsEnabled = !_loading;
        var header = Ui.Row(Ui.Columns(16, 1, Ui.KindTile(local ? Glyphs.Pc : Glyphs.Server), text, amount, action));
        return open ? Ui.Expander(header, [Ui.SubRow(null, Models(buckets), 64)]) : header;
    }

    // Each model's share drawn against the largest; the bar is neutral, as colours here speak only of what is left.
    private static Grid Models(IReadOnlyList<ModelTokenUsage> buckets)
    {
        var grid = new Grid { ColumnSpacing = 16, RowSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(72) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
        var largest = Math.Max(1, buckets.Max(bucket => bucket.TotalTokens));
        for (var row = 0; row < buckets.Count; row++)
        {
            var bucket = buckets[row];
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var estimate = TokenUsagePricing.EstimateUsd(bucket);
            var model = Ui.Mono(bucket.Model);
            model.TextTrimming = TextTrimming.CharacterEllipsis;
            ToolTipService.SetToolTip(model, bucket.ServiceTier == "default" ? bucket.Model : $"{bucket.Model} · {bucket.ServiceTier}");
            var bar = new Grid { Height = 6, VerticalAlignment = VerticalAlignment.Center, CornerRadius = new CornerRadius(3) };
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(bucket.TotalTokens, GridUnitType.Star) });
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(largest - bucket.TotalTokens, GridUnitType.Star) });
            bar.Children.Add(new Border { Style = Ui.Style("SyncBarTokenBar") });
            var track = new Border { Style = Ui.Style("SyncBarTokenTrack") };
            Grid.SetColumn(track, 1);
            bar.Children.Add(track);
            AutomationProperties.SetName(bar, $"{bucket.Model} {Tokens(bucket.TotalTokens)} 토큰");
            var tokens = Ui.Text(Tokens(bucket.TotalTokens), 12, wrap: false);
            tokens.HorizontalAlignment = HorizontalAlignment.Right;
            var cost = Ui.Caption(estimate.IsPriced ? Dollars((double)estimate.PricedUsd) : "요금 미상", wrap: false);
            cost.HorizontalAlignment = HorizontalAlignment.Right;
            var column = 0;
            foreach (var cell in new FrameworkElement[] { model, bar, tokens, cost })
            {
                cell.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetRow(cell, row);
                Grid.SetColumn(cell, column++);
                grid.Children.Add(cell);
            }
        }

        return grid;
    }

    /// <summary>"48.62M": two decimals for millions and billions, as the design reads them.</summary>
    public static string Tokens(long count) => count switch
    {
        >= 1_000_000_000 => (count / 1e9).ToString("0.00", CultureInfo.InvariantCulture) + "B",
        >= 1_000_000 => (count / 1e6).ToString("0.00", CultureInfo.InvariantCulture) + "M",
        >= 100_000 => (count / 1e3).ToString("0", CultureInfo.InvariantCulture) + "K",
        >= 10_000 => (count / 1e3).ToString("0.0", CultureInfo.InvariantCulture) + "K",
        _ => count.ToString("N0", CultureInfo.InvariantCulture),
    };

    /// <summary>"$212", "$49.6", "$1.32": three significant figures.</summary>
    public static string Dollars(double amount) => "$" + amount switch
    {
        >= 100 => Math.Round(amount).ToString("N0", CultureInfo.InvariantCulture),
        >= 10 => amount.ToString("0.0", CultureInfo.InvariantCulture),
        _ => amount.ToString("0.00", CultureInfo.InvariantCulture),
    };
}
