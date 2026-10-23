using System.Text.Json.Nodes;
using ClaudeSyncBar.App.Controls;
using ClaudeSyncBar.App.Model;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ClaudeSyncBar.App.Pages;

/// <summary>
/// A page of the management window: a scrolling body under a title, and an
/// optional bar fixed at the bottom. It is drawn again only when what it shows
/// changed, so a poll never resets what the user is typing or has open.
/// </summary>
public abstract class SyncPage : Page
{
    protected static AppState State => App.Current!.State;
    protected static MainWindow Shell => App.Current!.Window!;
    protected StackPanel Body { get; } = new() { Spacing = 16 };
    protected Grid Layout { get; } = new();
    private string? _signature;
    private bool _wide = true;
    private readonly Dictionary<string, FrameworkElement> _kept = [];

    protected SyncPage()
    {
        Layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var scroll = new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Body.Padding = new Thickness(36, 24, 36, 32);
        Body.HorizontalAlignment = HorizontalAlignment.Center;
        scroll.Content = Body;
        Layout.Children.Add(scroll);
        Content = Layout;
        Loaded += (_, _) => { State.Changed += Refresh; Refresh(); };
        Unloaded += (_, _) => State.Changed -= Refresh;
        ActualThemeChanged += (_, _) => Redraw();
        SizeChanged += (_, args) =>
        {
            // The body is as wide as the page, up to 1100, and centred. Stretched under a MaxWidth
            // instead, it was centred by its desired width: right of centre, and past a narrow window's edge.
            Body.Width = Math.Min(1100, args.NewSize.Width);
            var wide = args.NewSize.Width >= 720;
            if (wide == _wide) return;
            _wide = wide;
            Redraw();
        };
    }

    /// <summary>Wide enough to put the quotas beside the account instead of under it.</summary>
    protected bool Wide => _wide;

    /// <summary>What the page shows; when it is unchanged the page is left as it is.</summary>
    protected virtual string Signature()
    {
        var snapshot = (JsonObject)State.Snapshot.DeepClone();
        snapshot.Remove("updatedAt");
        // Reset captions count down in minutes.
        return snapshot.ToJsonString() + State.Preview?.ToJsonString() + State.SelectedProfileId + State.RefreshRequested
            + State.ConnectionError + DateTime.Now.ToString("HH:mm");
    }

    private void Refresh()
    {
        var signature = Signature();
        if (signature == _signature) return;
        _signature = signature;
        // Kept controls leave their places first: taken from a part already removed, WinUI refused to place them again.
        foreach (var kept in _kept.Values) Ui.Detach(kept);
        Body.Children.Clear();
        Render();
    }

    /// <summary>Draws the page again now, whatever changed.</summary>
    protected void Redraw()
    {
        _signature = null;
        if (IsLoaded) Refresh();
    }

    protected abstract void Render();

    /// <summary>
    /// A control made once and placed again on every drawing; the caller sets what changes.
    /// Made anew while a job ran, a disabled accent button faded in from its enabled colour
    /// (its template animates the background) and a ring started over, as if the work paused.
    /// </summary>
    protected T Keep<T>(string key, Func<T> create) where T : FrameworkElement
    {
        if (_kept.TryGetValue(key, out var kept))
        {
            Ui.Detach(kept);
            return (T)kept;
        }

        var made = create();
        _kept[key] = made;
        return made;
    }

    protected void SetFooter(UIElement? footer)
    {
        foreach (var old in Layout.Children.Where(child => Grid.GetRow((FrameworkElement)child) == 1).ToArray()) Layout.Children.Remove(old);
        if (footer is FrameworkElement element)
        {
            Grid.SetRow(element, 1);
            Layout.Children.Add(element);
        }
    }

    /// <summary>The page title, its explanation and the page's commands on the right.</summary>
    protected void Header(string title, string? subtitle, params UIElement[] actions)
    {
        var grid = new Grid { ColumnSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var text = Ui.Stack(4, false, Ui.Title(title));
        if (subtitle is not null) text.Children.Add(Ui.Text(subtitle, secondary: true));
        grid.Children.Add(text);
        for (var i = 0; i < actions.Length; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            if (actions[i] is FrameworkElement element) element.VerticalAlignment = VerticalAlignment.Top;
            Grid.SetColumn((FrameworkElement)actions[i], i + 1);
            grid.Children.Add(actions[i]);
        }

        Body.Children.Add(grid);
    }

    protected void Add(UIElement element) => Body.Children.Add(element);

    protected void Group(string title, string? description = null)
    {
        var header = Ui.Text(title, strong: true);
        header.Margin = new Thickness(0, 8, 0, description is null ? 0 : -12);
        Body.Children.Add(header);
        if (description is not null) Body.Children.Add(Ui.Caption(description));
    }

    /// <summary>Rows with the 4px gap Windows Settings uses between cards of one group.</summary>
    protected StackPanel Rows()
    {
        var rows = new StackPanel { Spacing = 4 };
        Body.Children.Add(rows);
        return rows;
    }

    protected void ErrorBar()
    {
        if (State.Error is not { } error) return;
        Add(new InfoBar { IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Error, Message = error });
    }
}
