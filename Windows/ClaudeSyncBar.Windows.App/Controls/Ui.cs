using System.Text.Json.Nodes;
using ClaudeSyncBar.App.Model;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace ClaudeSyncBar.App.Controls;

/// <summary>Segoe Fluent Icons code points used by the app.</summary>
public static class Glyphs
{
    public const string Refresh = "", Settings = "", Add = "", Sync = "", Check = "",
        Completed = "", Warning = "", Error = "", Info = "", Lock = "", Stopwatch = "",
        Calendar = "", Pc = "", Terminal = "", Server = "", Code = "", Download = "",
        Send = "", OpenWindow = "", Power = "", Delete = "", Folder = "", Shield = "",
        Back = "", ChevronRight = "", ChevronDown = "", ChevronUp = "", ArrowRight = "",
        ArrowDown = "", Up = "", Down = "", People = "", Devices = "", Chart = "",
        Speed = "", Lightning = "", Import = "", Window = "", Play = "", History = "",
        List = "", Contact = "", Clock = "";
}

/// <summary>More Segoe Fluent Icons code points, written as escapes.</summary>
public static class MoreGlyphs
{
    public const string OpenFile = "", Fingerprint = "";
}

/// <summary>Small builders for the pieces every screen repeats, so their look stays one.</summary>
public static class Ui
{
    private static readonly string[] AccountColors = ["Blue", "Teal", "Violet", "Amber", "Rose", "Green"];

    public static Style Style(string key) => (Style)Application.Current.Resources[key];

    public static TextBlock Text(string text, double size = 14, bool strong = false, bool secondary = false, bool wrap = true)
    {
        var block = new TextBlock
        {
            Text = text, FontSize = size, TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
            TextTrimming = wrap ? TextTrimming.None : TextTrimming.CharacterEllipsis,
        };
        if (strong) block.FontWeight = FontWeights.SemiBold;
        if (secondary) block.Style = Style("SyncBarSecondaryText");
        return block;
    }

    public static TextBlock Caption(string text, bool wrap = true) => Text(text, 12, secondary: true, wrap: wrap);

    public static TextBlock Title(string text) => new() { Text = text, Style = Style("TitleTextBlockStyle") };

    public static FontIcon Glyph(string glyph, double size = 16, string? style = null)
    {
        var icon = new FontIcon { Glyph = glyph, FontSize = size };
        if (style is not null) icon.Style = Style(style);
        return icon;
    }

    public static string ToneKey(Tone tone) => tone switch
    {
        Tone.Healthy => "Healthy", Tone.Warning => "Warning", Tone.Critical => "Critical", _ => "Muted",
    };

    public static TextBlock ToneText(string text, Tone tone, double size, bool strong = true)
    {
        var block = Text(text, size, strong, wrap: false);
        block.Style = Style("SyncBarTone" + ToneKey(tone));
        return block;
    }

    /// <summary>Ten segments, one per 10% left; colour by how much is left, outline only when unknown.</summary>
    public static Grid Meter(Quota quota, double height = 6)
    {
        var grid = new Grid { Height = height, ColumnSpacing = 2 };
        var filled = quota.Remaining is not { } remaining || remaining <= 0 ? 0 : Math.Max(1, (int)Math.Round(remaining / 10, MidpointRounding.AwayFromZero));
        var on = quota.Remaining is null ? "SyncBarSegmentUnknown" : "SyncBarSegment" + ToneKey(quota.Tone);
        for (var i = 0; i < 10; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var segment = new Rectangle { RadiusX = 2, RadiusY = 2, Style = Style(quota.Remaining is null ? on : i < filled ? on : "SyncBarSegmentOff") };
            Grid.SetColumn(segment, i);
            grid.Children.Add(segment);
        }

        AutomationProperties.SetName(grid, quota.Spoken);
        return grid;
    }

    /// <summary>The glyph that says "low" or "almost none" next to a number, so colour is never the only sign.</summary>
    public static FontIcon? ToneGlyph(Tone tone, double size = 14) => tone switch
    {
        Tone.Warning => Glyph(Glyphs.Warning, size, "SyncBarGlyphWarning"),
        Tone.Critical => Glyph(Glyphs.Error, size, "SyncBarGlyphCritical"),
        _ => null,
    };

    /// <summary>An account's colour follows its id, so reordering or renaming never changes it.</summary>
    public static Border Avatar(int? profileId, string name, double size = 40)
    {
        var color = AccountColors[Math.Abs((profileId ?? 0) % AccountColors.Length)];
        var initial = name.Trim() is { Length: > 0 } trimmed ? char.ConvertFromUtf32(char.ConvertToUtf32(trimmed, 0)) : "?";
        var text = new TextBlock
        {
            Text = initial, FontWeight = FontWeights.SemiBold, FontSize = Math.Round(size * 0.4),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Style = Style($"SyncBarAvatar{color}Text"),
        };
        return new Border
        {
            Width = size, Height = size, CornerRadius = new CornerRadius(size / 2), Child = text,
            Style = Style("SyncBarAvatar" + color), VerticalAlignment = VerticalAlignment.Center,
        };
    }

    public enum PillKind { Accent, Success, Caution, Critical, Neutral }

    public static Border Pill(string text, PillKind kind, string? glyph = null)
    {
        var (border, foreground, glyphStyle) = kind switch
        {
            PillKind.Accent => ("SyncBarPillAccent", "SyncBarAccentText", "SyncBarGlyphAccent"),
            PillKind.Success => ("SyncBarPillSuccess", "SyncBarToneHealthy", "SyncBarGlyphHealthy"),
            PillKind.Caution => ("SyncBarPillCaution", "SyncBarToneWarning", "SyncBarGlyphWarning"),
            PillKind.Critical => ("SyncBarPillCritical", "SyncBarToneCritical", "SyncBarGlyphCritical"),
            _ => ("SyncBarPillNeutral", "SyncBarSecondaryText", "SyncBarGlyphMuted"),
        };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        if (glyph is not null) row.Children.Add(Glyph(glyph, 11, glyphStyle));
        var label = Text(text, 12, wrap: false);
        label.Style = Style(foreground);
        row.Children.Add(label);
        return new Border
        {
            Style = Style(border), CornerRadius = new CornerRadius(10), Padding = new Thickness(glyph is null ? 8 : 6, 1, 8, 2),
            Child = row, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center,
        };
    }

    public static Border Row(UIElement child, bool current = false) => new()
    {
        Style = Style(current ? "SyncBarCurrentRowStyle" : "SyncBarRowStyle"), Child = child,
    };

    public static Border Card(UIElement child) => new() { Style = Style("SyncBarCardStyle"), Child = child };

    public static Border KindTile(string glyph) => new()
    {
        Style = Style("SyncBarKindTileStyle"), Child = Glyph(glyph, 16), VerticalAlignment = VerticalAlignment.Center,
    };

    public static StackPanel Stack(double spacing = 8, bool horizontal = false, params UIElement[] children)
    {
        var panel = new StackPanel { Spacing = spacing, Orientation = horizontal ? Orientation.Horizontal : Orientation.Vertical };
        foreach (var child in children) panel.Children.Add(child);
        return panel;
    }

    public static Button Button(string text, string? glyph = null, bool accent = false, RoutedEventHandler? click = null, string? tip = null)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        if (glyph is not null) content.Children.Add(Glyph(glyph, 14));
        content.Children.Add(new TextBlock { Text = text });
        var button = new Button { Content = content };
        if (accent) button.Style = Style("AccentButtonStyle");
        if (click is not null) button.Click += click;
        if (tip is not null) ToolTipService.SetToolTip(button, tip);
        AutomationProperties.SetName(button, text);
        return button;
    }

    /// <summary>Changes the words of a button made by <see cref="Button"/>, for one kept from drawing to drawing.</summary>
    public static void Relabel(Button button, string text)
    {
        if (button.Content is StackPanel content && content.Children.OfType<TextBlock>().FirstOrDefault() is { } label && label.Text != text)
        {
            label.Text = text;
            AutomationProperties.SetName(button, text);
        }
    }

    /// <summary>Takes an element out of where it was placed, so it can be placed again.</summary>
    public static void Detach(FrameworkElement element)
    {
        switch (element.Parent)
        {
            case Panel panel: panel.Children.Remove(element); break;
            case Border border when ReferenceEquals(border.Child, element): border.Child = null; break;
            case ContentControl control when ReferenceEquals(control.Content, element): control.Content = null; break;
        }
    }

    public static Button IconButton(string glyph, string name, RoutedEventHandler? click = null, bool subtle = true)
    {
        var button = new Button { Content = Glyph(glyph, 14), Padding = new Thickness(8), MinWidth = 32, MinHeight = 32 };
        if (subtle) button.Style = Style("SubtleButtonStyle");
        if (click is not null) button.Click += click;
        ToolTipService.SetToolTip(button, name);
        AutomationProperties.SetName(button, name);
        return button;
    }

    /// <summary>A Windows Settings style row: icon, title and description, and a control at the end.</summary>
    public static Border SettingRow(string? glyph, string title, string? description, UIElement? control, bool current = false)
    {
        var grid = new Grid { ColumnSpacing = 16 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        if (glyph is not null) grid.Children.Add(Glyph(glyph, 20));
        var text = Stack(1, false, Text(title));
        if (description is not null) text.Children.Add(Caption(description));
        text.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        if (control is FrameworkElement element)
        {
            element.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(element, 2);
            grid.Children.Add(element);
        }

        return Row(grid, current);
    }

    public static ToggleSwitch Toggle(bool on, string name, Action<bool>? changed = null, bool enabled = true)
    {
        var toggle = new ToggleSwitch { IsOn = on, OnContent = "켬", OffContent = "끔", IsEnabled = enabled, MinWidth = 0 };
        AutomationProperties.SetName(toggle, name);
        if (changed is not null) toggle.Toggled += (_, _) => changed(toggle.IsOn);
        return toggle;
    }

    public static TextBlock Mono(string text, double size = 12)
    {
        var block = Text(text, size, wrap: false);
        block.FontFamily = new FontFamily("Cascadia Mono, Consolas");
        return block;
    }

    /// <summary>The account's avatar, name and a detail line, as rows of a list show them.</summary>
    public static Grid Identity(JsonNode account, double avatar, string? detail = null, UIElement? badge = null)
    {
        var name = AppState.AccountName(account);
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(Avatar(AppState.Int(account["id"]), name, avatar));
        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        title.Children.Add(Text(name, strong: true, wrap: false));
        if (badge is not null) title.Children.Add(badge);
        detail ??= string.Join(" · ", new[] { AppState.Str(account["email"]), AppState.Str(account["plan"]) }.Where(value => !string.IsNullOrEmpty(value) && value != name));
        var text = Stack(1, false, title);
        if (detail.Length > 0) text.Children.Add(Caption(detail, wrap: false));
        text.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        return grid;
    }

    /// <summary>Parts side by side, centred on one line; the part at index fill takes the room left. Null parts are left out.</summary>
    public static Grid Columns(double spacing, int fill, params UIElement?[] parts)
    {
        var grid = new Grid { ColumnSpacing = spacing };
        var column = 0;
        for (var i = 0; i < parts.Length; i++)
        {
            if (parts[i] is not FrameworkElement element) continue;
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = i == fill ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
            element.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(element, column++);
            grid.Children.Add(element);
        }

        return grid;
    }

    /// <summary>
    /// A row card that opens onto rows of details below it, as Windows Settings
    /// does. The rows are given; the card only joins them.
    /// </summary>
    public static StackPanel Expander(Border header, IReadOnlyList<UIElement> rows)
    {
        var stack = new StackPanel();
        stack.Children.Add(header);
        if (rows.Count == 0) return stack;
        header.CornerRadius = new CornerRadius(4, 4, 0, 0);
        for (var i = 0; i < rows.Count; i++)
        {
            if (rows[i] is Border border && i == rows.Count - 1) border.CornerRadius = new CornerRadius(0, 0, 4, 4);
            stack.Children.Add(rows[i]);
        }

        return stack;
    }

    /// <summary>One line under an open row card: a short label on the left and what it is about.</summary>
    public static Border SubRow(string? label, UIElement content, double indent = 72)
    {
        var grid = new Grid { ColumnSpacing = 12 };
        if (label is not null)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(72) });
            var text = Text(label, wrap: false);
            text.VerticalAlignment = VerticalAlignment.Center;
            grid.Children.Add(text);
        }

        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        if (content is FrameworkElement element)
        {
            element.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(element, label is null ? 0 : 1);
            grid.Children.Add(element);
        }

        return new Border { Style = Style("SyncBarSubRowStyle"), Padding = new Thickness(indent, 8, 16, 8), Child = grid };
    }

    /// <summary>The chevron that opens or closes a row card.</summary>
    public static Button ExpandButton(bool open, string name, Action toggle)
    {
        var button = IconButton(open ? Glyphs.ChevronUp : Glyphs.ChevronDown, $"{name} {(open ? "접기" : "펼치기")}", (_, _) => toggle());
        AutomationProperties.SetItemStatus(button, open ? "펼쳐짐" : "접힘");
        return button;
    }

    public static WrapPanel Wrap(double spacing, params UIElement?[] children)
    {
        var panel = new WrapPanel { Spacing = spacing, LineSpacing = 6 };
        foreach (var child in children) if (child is not null) panel.Children.Add(child);
        return panel;
    }

    /// <summary>"인증 정상" in the success colour with its check, so colour is not the only sign.</summary>
    public static StackPanel Status(string text, Tone tone, string glyph)
    {
        var caption = Caption(text, wrap: false);
        caption.Style = Style("SyncBarTone" + ToneKey(tone));
        caption.FontSize = 12;
        return Stack(4, true, Glyph(glyph, 12, tone switch
        {
            Tone.Healthy => "SyncBarGlyphHealthy", Tone.Warning => "SyncBarGlyphWarning", Tone.Critical => "SyncBarGlyphCritical", _ => "SyncBarGlyphMuted",
        }), caption);
    }
}
