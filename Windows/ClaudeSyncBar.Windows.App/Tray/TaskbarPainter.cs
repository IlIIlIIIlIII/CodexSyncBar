using System.Runtime.InteropServices;
using ClaudeSyncBar.App.Model;
using static ClaudeSyncBar.App.Tray.TaskbarNative;

namespace ClaudeSyncBar.App.Tray;

/// <summary>A 32-bit top-down bitmap selected into its own memory DC.</summary>
internal sealed class TaskbarFrame : IDisposable
{
    public IntPtr Dc { get; }
    public IntPtr Bits { get; }
    public int Width { get; }
    public int Height { get; }
    private readonly IntPtr _bitmap, _previous;

    public TaskbarFrame(int width, int height)
    {
        Width = Math.Max(1, width);
        Height = Math.Max(1, height);
        Dc = CreateCompatibleDC(IntPtr.Zero);
        var header = new BitmapInfoHeader { Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(), Width = Width, Height = -Height, Planes = 1, BitCount = 32 };
        _bitmap = CreateDIBSection(Dc, ref header, 0, out var bits, IntPtr.Zero, 0);
        if (_bitmap == IntPtr.Zero)
        {
            DeleteDC(Dc);
            throw new InvalidOperationException("작업 표시줄 그림을 만들지 못했어요.");
        }

        Bits = bits;
        _previous = SelectObject(Dc, _bitmap);
    }

    public uint[] Read()
    {
        GdiFlush();
        var pixels = new int[Width * Height];
        Marshal.Copy(Bits, pixels, 0, pixels.Length);
        return Array.ConvertAll(pixels, pixel => unchecked((uint)pixel));
    }

    public void Write(uint[] pixels)
    {
        GdiFlush();
        Marshal.Copy(Array.ConvertAll(pixels, pixel => unchecked((int)pixel)), 0, Bits, pixels.Length);
    }

    public void Dispose()
    {
        SelectObject(Dc, _previous);
        DeleteObject(_bitmap);
        DeleteDC(Dc);
    }
}

/// <summary>
/// Draws the taskbar readout and its tooltip with GDI, which falls back to
/// Malgun Gothic for Korean the way the shell does. The readout lets the
/// taskbar show through, so its text is drawn as a coverage mask and coloured
/// afterwards; the tooltip is opaque and uses ClearType. Colours are the app's:
/// numbers say how much is left, never the accent.
/// </summary>
internal static class TaskbarPainter
{
    // ARGB, as the app's WinUI theme resources on a dark or light taskbar.
    private sealed record Palette(uint Primary, uint Secondary, uint Healthy, uint Warning, uint Critical, uint Divider, uint Hover, uint HoverStroke);

    private static readonly Palette DarkBar = new(0xFFFFFFFF, 0xC5FFFFFF, 0xFF6CCB5F, 0xFFFCE100, 0xFFFF99A4, 0x33FFFFFF, 0x15FFFFFF, 0x0FFFFFFF);
    private static readonly Palette LightBar = new(0xE4000000, 0x9E000000, 0xFF0F7B0F, 0xFF9D5D00, 0xFFC42B1C, 0x29000000, 0xB3FFFFFF, 0x0F000000);
    // The same, made opaque on the tooltip's own background.
    private static readonly Palette DarkTip = new(0xFFFFFF, 0xCFCFCF, 0x6CCB5F, 0xFCE100, 0xFF99A4, 0x454545, 0x2C2C2C, 0);
    private static readonly Palette LightTip = new(0x1B1B1B, 0x5F5F5F, 0x0F7B0F, 0x9D5D00, 0xC42B1C, 0xE5E5E5, 0xF9F9F9, 0);

    private sealed record Run(string Text, bool Strong, uint Color, int X, int Width);

    private sealed class Fonts : IDisposable
    {
        public IntPtr Regular { get; }
        public IntPtr Strong { get; }

        public Fonts(double scale, uint quality)
        {
            var height = -(int)Math.Round(12 * scale);
            Regular = CreateFont(height, 0, 0, 0, FwNormal, 0, 0, 0, 1, 0, 0, quality, 0, "Segoe UI");
            Strong = CreateFont(height, 0, 0, 0, FwSemiBold, 0, 0, 0, 1, 0, 0, quality, 0, "Segoe UI");
        }

        public void Dispose()
        {
            DeleteObject(Regular);
            DeleteObject(Strong);
        }
    }

    private static uint ToneColor(Palette palette, Tone tone) => tone switch
    {
        Tone.Healthy => palette.Healthy,
        Tone.Warning => palette.Warning,
        Tone.Critical => palette.Critical,
        _ => palette.Secondary,
    };

    private static int Measure(IntPtr dc, IntPtr font, string text)
    {
        SelectObject(dc, font);
        var rect = new Rect();
        DrawText(dc, text, -1, ref rect, DtCalcRect | DtSingleLine | DtNoPrefix);
        return rect.Width;
    }

    /// <summary>"5시간 62% │ 전체 주간 45%" with premultiplied alpha, 40 px tall at 100%.</summary>
    public static TaskbarFrame Readout(TaskbarUsage usage, double scale, bool light, bool hover)
    {
        var palette = light ? LightBar : DarkBar;
        int S(double value) => (int)Math.Round(value * scale);
        var line = Math.Max(1, S(1));
        var height = S(40);
        // Natural ClearType leaves stems where the outline puts them instead of snapping them to whole pixels,
        // which keeps Segoe UI's digits as light as Malgun Gothic's Hangul beside them; the three subpixels are averaged below.
        using var fonts = new Fonts(scale, CleartypeNaturalQuality);
        var runs = new List<Run>();
        var separators = new List<int>();
        var x = S(10);
        var measure = CreateCompatibleDC(IntPtr.Zero);
        try
        {
            for (var i = 0; i < usage.Parts.Count; i++)
            {
                var part = usage.Parts[i];
                if (i > 0)
                {
                    x += S(8);
                    separators.Add(x);
                    x += line + S(8);
                }

                var label = Measure(measure, fonts.Regular, part.Label);
                runs.Add(new(part.Label, false, palette.Secondary, x, label));
                x += label + S(5);
                var value = Measure(measure, fonts.Strong, part.Value);
                runs.Add(new(part.Value, true, ToneColor(palette, part.Tone), x, value));
                x += value;
            }
        }
        finally
        {
            DeleteDC(measure);
        }

        var width = x + S(10);
        // White text on black: each pixel's brightness is how much of it the glyphs cover.
        uint[] coverage;
        using (var mask = new TaskbarFrame(width, height))
        {
            SetBkMode(mask.Dc, Transparent);
            SetTextColor(mask.Dc, 0xFFFFFF);
            foreach (var run in runs)
            {
                SelectObject(mask.Dc, run.Strong ? fonts.Strong : fonts.Regular);
                var rect = new Rect { Left = run.X, Top = 0, Right = run.X + run.Width + S(3), Bottom = height };
                DrawText(mask.Dc, run.Text, -1, ref rect, DtLeft | DtVCenter | DtSingleLine | DtNoPrefix);
            }

            coverage = mask.Read();
        }

        // Alpha 1 everywhere, so the gaps between the words still take the mouse.
        var pixels = new uint[width * height];
        Array.Fill(pixels, 0x01000000u);
        if (hover) Rounded(pixels, width, height, S(4), line, palette.Hover, palette.HoverStroke);
        foreach (var separator in separators)
        {
            var top = (height - S(16)) / 2;
            for (var y = top; y < top + S(16); y++)
                for (var column = separator; column < separator + line; column++)
                    Blend(ref pixels[y * width + column], palette.Divider, 1);
        }

        foreach (var run in runs)
            for (var y = 0; y < height; y++)
                for (var column = run.X; column < Math.Min(width, run.X + run.Width + S(3)); column++)
                {
                    var covered = coverage[y * width + column];
                    var amount = (((covered >> 16) & 0xFF) + ((covered >> 8) & 0xFF) + (covered & 0xFF)) / 3.0;
                    if (amount > 0) Blend(ref pixels[y * width + column], run.Color, amount / 255.0);
                }

        var frame = new TaskbarFrame(width, height);
        frame.Write(pixels);
        return frame;
    }

    // Source over, premultiplied: dst = src * a + dst * (1 - a).
    private static void Blend(ref uint destination, uint argb, double coverage)
    {
        var alpha = ((argb >> 24) & 0xFF) / 255.0 * coverage;
        if (alpha <= 0) return;
        var keep = 1 - alpha;
        var below = destination;
        uint Channel(int shift, double source) => (uint)Math.Clamp(Math.Round(source + ((below >> shift) & 0xFF) * keep), 0, 255);
        destination = Channel(24, alpha * 255) << 24 | Channel(16, ((argb >> 16) & 0xFF) * alpha) << 16
            | Channel(8, ((argb >> 8) & 0xFF) * alpha) << 8 | Channel(0, (argb & 0xFF) * alpha);
    }

    // The taskbar buttons' hover plate: a rounded fill and a hairline edge, anti-aliased.
    private static void Rounded(uint[] pixels, int width, int height, int radius, int stroke, uint fill, uint edge)
    {
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var qx = Math.Abs(x + 0.5 - width / 2.0) - (width / 2.0 - radius);
                var qy = Math.Abs(y + 0.5 - height / 2.0) - (height / 2.0 - radius);
                var distance = Math.Sqrt(Math.Pow(Math.Max(qx, 0), 2) + Math.Pow(Math.Max(qy, 0), 2)) + Math.Min(Math.Max(qx, qy), 0) - radius;
                var inside = Math.Clamp(0.5 - distance, 0, 1);
                var inner = Math.Clamp(0.5 - (distance + stroke), 0, 1);
                if (inside > 0) Blend(ref pixels[y * width + x], fill, inside);
                if (inside - inner > 0) Blend(ref pixels[y * width + x], edge, inside - inner);
            }
    }

    /// <summary>Measures the tooltip and, given a DC, draws it there at (0, 0).</summary>
    public static (int Width, int Height) Tip(IntPtr dc, TaskbarUsage usage, double scale, bool light, bool draw)
    {
        var palette = light ? LightTip : DarkTip;
        int S(double value) => (int)Math.Round(value * scale);
        using var fonts = new Fonts(scale, CleartypeQuality);
        var lineHeight = S(17);
        var left = S(10);
        var lines = new List<(string Text, bool Strong, uint Color, int X, int Y)>();
        var y = S(7);
        lines.Add((usage.Title, true, palette.Primary, left, y));
        y += lineHeight + S(4);
        var labelWidth = usage.Rows.Count == 0 ? 0 : usage.Rows.Max(row => Measure(dc, fonts.Regular, row.Label)) + S(12);
        var right = left + Measure(dc, fonts.Strong, usage.Title);
        foreach (var row in usage.Rows)
        {
            lines.Add((row.Label, false, palette.Secondary, left, y));
            var x = left + labelWidth;
            lines.Add((row.Value, true, ToneColor(palette, row.Tone), x, y));
            x += Measure(dc, fonts.Strong, row.Value);
            if (row.Detail.Length > 0)
            {
                var detail = " · " + row.Detail;
                lines.Add((detail, false, palette.Primary, x, y));
                x += Measure(dc, fonts.Regular, detail);
            }

            right = Math.Max(right, x);
            y += lineHeight + S(2);
        }

        var rule = -1;
        if (usage.Notes.Count > 0)
        {
            y += S(4);
            rule = y;
            y += Math.Max(1, S(1)) + S(6);
            foreach (var note in usage.Notes)
            {
                lines.Add((note, false, palette.Secondary, left, y));
                right = Math.Max(right, left + Measure(dc, fonts.Regular, note));
                y += lineHeight;
            }
        }

        var width = right + S(12);
        var height = y + S(8);
        if (!draw) return (width, height);

        var background = CreateSolidBrush(ColorRef(palette.Hover));
        var all = new Rect { Right = width, Bottom = height };
        FillRect(dc, ref all, background);
        DeleteObject(background);
        if (rule >= 0)
        {
            var divider = CreateSolidBrush(ColorRef(palette.Divider));
            var line = new Rect { Left = left, Top = rule, Right = width - S(10), Bottom = rule + Math.Max(1, S(1)) };
            FillRect(dc, ref line, divider);
            DeleteObject(divider);
        }

        SetBkMode(dc, Transparent);
        foreach (var (text, strong, color, x, top) in lines)
        {
            SelectObject(dc, strong ? fonts.Strong : fonts.Regular);
            SetTextColor(dc, ColorRef(color));
            var rect = new Rect { Left = x, Top = top, Right = width, Bottom = top + lineHeight };
            DrawText(dc, text, -1, ref rect, DtLeft | DtVCenter | DtSingleLine | DtNoPrefix);
        }

        return (width, height);
    }
}
