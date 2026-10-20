using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace ClaudeSyncBar.App.Controls;

/// <summary>Puts its children in a row and starts another row when one is full; each row is centred vertically.</summary>
public sealed class WrapPanel : Panel
{
    public double Spacing { get; set; } = 8;
    public double LineSpacing { get; set; } = 8;

    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (var child in Children) child.Measure(new Size(availableSize.Width, double.PositiveInfinity));
        var lines = Lines(availableSize.Width);
        var width = lines.Count == 0 ? 0 : lines.Max(line => line.Width);
        var height = lines.Sum(line => line.Height) + Math.Max(0, lines.Count - 1) * LineSpacing;
        return new Size(double.IsInfinity(availableSize.Width) ? width : Math.Min(width, availableSize.Width), height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double y = 0;
        foreach (var line in Lines(finalSize.Width))
        {
            double x = 0;
            foreach (var child in line.Children)
            {
                var size = child.DesiredSize;
                child.Arrange(new Rect(x, y + (line.Height - size.Height) / 2, size.Width, size.Height));
                x += size.Width + Spacing;
            }

            y += line.Height + LineSpacing;
        }

        return finalSize;
    }

    private sealed record Line(List<UIElement> Children, double Width, double Height);

    private List<Line> Lines(double available)
    {
        var lines = new List<Line>();
        var current = new List<UIElement>();
        double width = 0, height = 0;
        foreach (var child in Children)
        {
            var size = child.DesiredSize;
            // Collapsed children take no room and no gap.
            if (size.Width <= 0 && size.Height <= 0) continue;
            var needed = current.Count == 0 ? size.Width : width + Spacing + size.Width;
            if (current.Count > 0 && needed > available)
            {
                lines.Add(new(current, width, height));
                current = [];
                width = height = 0;
                needed = size.Width;
            }

            current.Add(child);
            width = needed;
            height = Math.Max(height, size.Height);
        }

        if (current.Count > 0) lines.Add(new(current, width, height));
        return lines;
    }
}
