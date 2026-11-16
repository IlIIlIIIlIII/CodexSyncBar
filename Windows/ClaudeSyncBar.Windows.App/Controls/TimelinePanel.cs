using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace ClaudeSyncBar.App.Controls;

/// <summary>How a child of a <see cref="TimelinePanel"/> sits on the axis.</summary>
public enum TimelinePlace
{
    /// <summary>From its start for its span, as wide as that part of the axis.</summary>
    Band,

    /// <summary>Centred on its point at its own width: a tick or the now line.</summary>
    Mark,

    /// <summary>A label at its own size to the right of its point.</summary>
    After,

    /// <summary>A label at its own size to the left of its point.</summary>
    Before,

    /// <summary>The words of a band, inside it from its left edge.</summary>
    Inside,
}

/// <summary>
/// Places its children along one horizontal axis by fractions of its width, so a week keeps its
/// proportions at any window size. Bands fill their span and marks sit on a point. Words are placed
/// at their own size, inside a band or beside a point, and never under a line: they step aside for
/// a mark or the point to avoid, are cut short where there is no room, and are hidden when too
/// little of them would show. The one exception is a later label that may lie over the marks of
/// earlier points, which then fade under it. Every child gets the panel's whole height.
/// </summary>
public sealed class TimelinePanel : Panel
{
    public static readonly DependencyProperty AtProperty = Attached("At", 0d);
    public static readonly DependencyProperty SpanProperty = Attached("Span", 0d);
    // Kept as a number: XAML's type system does not know an enum the app defines.
    public static readonly DependencyProperty PlaceProperty = Attached("Place", (int)TimelinePlace.Band);
    public static readonly DependencyProperty OffsetProperty = Attached("Offset", 0d);
    public static readonly DependencyProperty MinTextProperty = Attached("MinText", 0d);
    public static readonly DependencyProperty CollideProperty = Attached("Collide", false);
    public static readonly DependencyProperty DodgeProperty = Attached("Dodge", false);
    public static readonly DependencyProperty WholeProperty = Attached("Whole", false);
    public static readonly DependencyProperty InsteadProperty =
        DependencyProperty.RegisterAttached("Instead", typeof(UIElement), typeof(TimelinePanel), new PropertyMetadata(null, Moved));
    public static readonly DependencyProperty OfProperty =
        DependencyProperty.RegisterAttached("Of", typeof(UIElement), typeof(TimelinePanel), new PropertyMetadata(null, Moved));

    // Words cut to less than this are not worth showing.
    private const double Least = 24;
    // The room kept between words and a line.
    private const double Clear = 4;
    // The room kept inside a band too narrow for its words and their usual padding.
    private const double NarrowPadding = 3;
    // A mark under a later label that lies over it.
    private const double Faded = 0.3;
    private readonly Dictionary<UIElement, Rect> _slots = [];
    // The width of each piece of words at its own size, as last measured.
    private readonly Dictionary<UIElement, double> _natural = [];
    private double _width = double.NaN;

    /// <summary>The room left between a band and the next; a band that reaches the end of the axis keeps its whole width.</summary>
    public double Gap { get; set; }

    /// <summary>
    /// A point of the axis, as a fraction, that words keep clear of: where another panel draws the now line
    /// over this one. NaN when there is none.
    /// </summary>
    public double Avoid { get; set; } = double.NaN;

    private static DependencyProperty Attached<T>(string name, T initial) =>
        DependencyProperty.RegisterAttached(name, typeof(T), typeof(TimelinePanel), new PropertyMetadata(initial, Moved));

    private static void Moved(DependencyObject element, DependencyPropertyChangedEventArgs args)
    {
        if (element is FrameworkElement { Parent: TimelinePanel panel }) panel.InvalidateMeasure();
    }

    /// <summary>
    /// Marks a line of words that shows whole or not at all, where the others are cut short with an ellipsis:
    /// a time or an amount that lost its end would say another.
    /// </summary>
    public static TextBlock Whole(TextBlock line)
    {
        line.SetValue(WholeProperty, true);
        return line;
    }

    private static IEnumerable<TextBlock> Lines(UIElement words) =>
        words is Panel panel ? panel.Children.OfType<TextBlock>() : words is TextBlock single ? [single] : [];

    /// <summary>A band from <paramref name="at"/> for <paramref name="span"/> of the axis.</summary>
    public T Band<T>(T element, double at, double span) where T : UIElement
    {
        element.SetValue(PlaceProperty, (int)TimelinePlace.Band);
        element.SetValue(AtProperty, at);
        element.SetValue(SpanProperty, span);
        Children.Add(element);
        return element;
    }

    /// <summary>
    /// The words of <paramref name="band"/>, <paramref name="padding"/> inside its edges, or a little less in a band
    /// too narrow for them. They are hidden in a band narrower than minText, and the pointer goes through them to
    /// the band and its tooltip.
    /// </summary>
    public T Text<T>(T element, UIElement band, double padding, double minText = 0) where T : UIElement
    {
        element.SetValue(PlaceProperty, (int)TimelinePlace.Inside);
        element.SetValue(OfProperty, band);
        element.SetValue(OffsetProperty, padding);
        element.SetValue(MinTextProperty, minText);
        element.IsHitTestVisible = false;
        Children.Add(element);
        return element;
    }

    /// <summary>A tick or line centred on <paramref name="at"/>, kept inside the axis.</summary>
    public T Mark<T>(T element, double at) where T : UIElement
    {
        element.SetValue(PlaceProperty, (int)TimelinePlace.Mark);
        element.SetValue(AtProperty, at);
        Children.Add(element);
        return element;
    }

    /// <summary>
    /// A label beside <paramref name="at"/>, <paramref name="offset"/> away from it. Cut short of a line or the edge,
    /// it is hidden once narrower than minText. With collide, it gives way to a label added after it, and left of its
    /// point it may lie over the marks of earlier points, whose labels then give way to it. With dodge, it moves past a
    /// line that would run through it instead of being cut. With instead, it shows only while those words are hidden.
    /// </summary>
    public T Label<T>(T element, double at, bool before, double offset, double minText = 0, bool collide = false,
        bool dodge = false, UIElement? instead = null) where T : UIElement
    {
        element.SetValue(PlaceProperty, (int)(before ? TimelinePlace.Before : TimelinePlace.After));
        element.SetValue(AtProperty, at);
        element.SetValue(OffsetProperty, offset);
        element.SetValue(MinTextProperty, minText);
        element.SetValue(CollideProperty, collide);
        element.SetValue(DodgeProperty, dodge);
        if (instead is not null) element.SetValue(InsteadProperty, instead);
        Children.Add(element);
        return element;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsFinite(availableSize.Width) ? availableSize.Width : 0;
        var height = Place(width, availableSize.Height, measure: true);
        return new Size(width, double.IsFinite(availableSize.Height) ? Math.Min(height, availableSize.Height) : height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        // Layout rounding may give a width a fraction off the one measured against: the places follow the width given,
        // from the sizes already measured. Measuring a child here would ask for another pass, and that one for another.
        if (double.IsNaN(_width) || Math.Abs(_width - finalSize.Width) > 0.01) Place(finalSize.Width, finalSize.Height, measure: false);
        foreach (var child in Children)
        {
            var slot = _slots.TryGetValue(child, out var found) ? found : default;
            child.Arrange(new Rect(slot.X, 0, slot.Width, finalSize.Height));
        }

        return finalSize;
    }

    private static TimelinePlace PlaceOf(UIElement child) => (TimelinePlace)(int)child.GetValue(PlaceProperty);

    // The width words ask for when nothing limits them.
    private double Natural(UIElement child, double height, bool measure)
    {
        if (measure)
        {
            child.Measure(new Size(double.PositiveInfinity, height));
            _natural[child] = child.DesiredSize.Width;
        }

        return _natural.TryGetValue(child, out var known) ? known : child.DesiredSize.Width;
    }

    // Where every child goes for this width; returns the height the tallest asks for.
    private double Place(double width, double height, bool measure)
    {
        _width = width;
        _slots.Clear();
        if (measure) _natural.Clear();
        double tallest = 0;
        // What words keep clear of: the marks here and the point another panel draws its line on.
        var lines = new List<Rect>();
        var marks = new List<(UIElement Mark, Rect Slot)>();
        var hidden = new HashSet<UIElement>();
        foreach (var child in Children)
        {
            var at = Math.Clamp((double)child.GetValue(AtProperty), 0, 1);
            switch (PlaceOf(child))
            {
                case TimelinePlace.Band:
                {
                    var end = Math.Clamp(at + (double)child.GetValue(SpanProperty), at, 1);
                    var left = at * width;
                    var room = Math.Max(0, end * width - left - (end < 1 - 1e-6 ? Gap : 0));
                    if (measure) child.Measure(new Size(room, height));
                    _slots[child] = new Rect(left, 0, room, 0);
                    tallest = Math.Max(tallest, child.DesiredSize.Height);
                    break;
                }
                case TimelinePlace.Mark:
                {
                    if (measure) child.Measure(new Size(double.PositiveInfinity, height));
                    var size = child.DesiredSize.Width;
                    var slot = new Rect(Math.Clamp(at * width - size / 2, 0, Math.Max(0, width - size)), 0, size, 0);
                    _slots[child] = slot;
                    lines.Add(slot);
                    marks.Add((child, slot));
                    tallest = Math.Max(tallest, child.DesiredSize.Height);
                    break;
                }
            }
        }

        var avoid = double.IsFinite(Avoid) ? Math.Clamp(Avoid, 0, 1) * width : double.NaN;
        // Left of its point a later label lies over earlier marks; the point to avoid still stops it.
        var avoided = new List<Rect>();
        if (double.IsFinite(avoid)) avoided.Add(new Rect(Math.Max(0, avoid - 1), 0, 2, 0));
        lines.AddRange(avoided);

        // The words of each band: from its left edge, or on the roomier side of the point to avoid when that runs through them.
        foreach (var child in Children)
        {
            if (PlaceOf(child) != TimelinePlace.Inside) continue;
            var natural = Natural(child, height, measure);
            var band = child.GetValue(OfProperty) is UIElement of && _slots.TryGetValue(of, out var found) ? found : default;
            var padding = (double)child.GetValue(OffsetProperty);
            if (band.Width < natural + 2 * padding) padding = Math.Min(padding, NarrowPadding);
            var start = band.X + padding;
            var end = band.X + band.Width - padding;
            var given = Math.Min(natural, Math.Max(0, end - start));
            var show = band.Width > 0 && band.Width >= (double)child.GetValue(MinTextProperty) && given > 0;
            if (show && double.IsFinite(avoid) && avoid > start - Clear && avoid < start + given + Clear)
            {
                var after = end - (avoid + Clear + 1);
                var ahead = avoid - Clear - 1 - start;
                if (after >= natural || after >= ahead) (start, given) = (avoid + Clear + 1, Math.Min(natural, after));
                else given = Math.Min(natural, ahead);
                show = given >= Math.Min(natural, Least);
            }

            tallest = Math.Max(tallest, Settle(child, show, start, given, natural, height, measure, false));
            if (!show) hidden.Add(child);
        }

        // Labels from the last to the first: a later one keeps its place.
        var kept = new List<Rect>();
        for (var i = Children.Count - 1; i >= 0; i--)
        {
            var child = Children[i];
            var place = PlaceOf(child);
            if (place is not (TimelinePlace.After or TimelinePlace.Before)) continue;
            var natural = Natural(child, height, measure);
            var point = Math.Clamp((double)child.GetValue(AtProperty), 0, 1) * width;
            var offset = (double)child.GetValue(OffsetProperty);
            var least = Math.Max(1, (double)child.GetValue(MinTextProperty));
            var collide = (bool)child.GetValue(CollideProperty);
            var before = place == TimelinePlace.Before;
            var show = !(child.GetValue(InsteadProperty) is UIElement words && !hidden.Contains(words));
            double start = point, given = 0;
            if (show && (bool)child.GetValue(DodgeProperty))
            {
                // Past each line that stands in it, then as far as the edge.
                var near = before ? point - offset : point + offset;
                foreach (var line in before ? lines.OrderByDescending(line => line.Left) : lines.OrderBy(line => line.Left))
                {
                    if (before && line.Left < near + Clear && line.Right > near - natural - Clear) near = line.Left - Clear;
                    else if (!before && line.Right > near - Clear && line.Left < near + natural + Clear) near = line.Right + Clear;
                }

                given = Math.Min(natural, before ? near : width - near);
                start = before ? near - given : near;
                show = given >= natural - 0.5 || given >= least;
            }
            else if (show)
            {
                // Whole on its side, else cut short, and only where no later label is.
                var room = Room(before, point, offset, width, collide && before ? avoided : lines);
                given = Math.Min(natural, room.Size);
                var (left, right) = before ? (room.Near - given, room.Near) : (room.Near, room.Near + given);
                show = (given >= natural - 0.5 || given >= least) && !(collide && kept.Any(other => Overlaps(other, left, right)));
                if (show) start = left;
            }

            tallest = Math.Max(tallest, Settle(child, show, start, given, natural, height, measure, before));
            if (!show) hidden.Add(child);
            else if (collide) kept.Add(_slots[child]);
        }

        // The marks a later label lies over fade under it.
        foreach (var (mark, slot) in marks)
            mark.Opacity = kept.Any(label => slot.Left < label.Right && slot.Right > label.Left) ? Faded : 1;
        return tallest;
    }

    // Where a label starts on one side of its point, and how far it can run before a line or the edge.
    private static (double Near, double Size) Room(bool before, double point, double offset, double width, List<Rect> lines)
    {
        if (before)
        {
            var near = point - offset;
            var limit = lines.Where(line => line.Right <= near + 0.5).Select(line => line.Right + Clear).DefaultIfEmpty(0).Max();
            return (near, near - Math.Max(0, limit));
        }
        else
        {
            var near = point + offset;
            var limit = lines.Where(line => line.Left >= near - 0.5).Select(line => line.Left - Clear).DefaultIfEmpty(width).Min();
            return (near, Math.Min(width, limit) - near);
        }
    }

    // Shows or hides a piece of words in its place, cut to the room given; returns the height it asks for.
    private double Settle(UIElement child, bool show, double start, double given, double natural, double height, bool measure, bool right)
    {
        child.Opacity = show ? 1 : 0;
        if (PlaceOf(child) != TimelinePlace.Inside) child.IsHitTestVisible = show;
        _slots[child] = show ? new Rect(start, 0, given, 0) : new Rect(Math.Clamp(start, 0, Math.Max(0, _width)), 0, 0, 0);
        if (!show) return 0;
        if (measure)
        {
            // Lines of a label left of its point end at it.
            var alignment = right ? TextAlignment.Right : TextAlignment.Left;
            var turned = false;
            foreach (var line in Lines(child))
            {
                // Each line still has the width it asked for with nothing limiting it.
                if ((bool)line.GetValue(WholeProperty)) line.Opacity = line.DesiredSize.Width <= given + 0.5 ? 1 : 0;
                if (line.TextAlignment == alignment) continue;
                line.TextAlignment = alignment;
                turned = true;
            }

            // Measured again here when cut or turned, so nothing is left to ask for another pass.
            if (turned || given < natural - 0.5) child.Measure(new Size(given < natural - 0.5 ? given : double.PositiveInfinity, height));
        }

        return child.DesiredSize.Height;
    }

    private static bool Overlaps(Rect kept, double left, double right) => left < kept.Right + Clear && right > kept.Left - Clear;
}

/// <summary>
/// A part of a chart that takes the keyboard focus, so its tooltip opens from the keyboard too, and that a screen
/// reader finds by its automation name. It fills the place its panel gives it.
/// </summary>
public sealed class TimelineSpot : ContentControl
{
    public TimelineSpot(UIElement content, string text)
    {
        Content = content;
        IsTabStop = true;
        UseSystemFocusVisuals = true;
        // The focus rectangle stands clear of a tick only two pixels wide.
        FocusVisualMargin = new Thickness(-3);
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
        ToolTipService.SetToolTip(this, text);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(this, text);
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new FrameworkElementAutomationPeer(this);
}
