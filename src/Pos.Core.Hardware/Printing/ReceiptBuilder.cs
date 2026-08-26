using System.Text;

namespace Pos.Core.Hardware.Printing;

/// <summary>
/// Lays out a receipt for a fixed-width thermal printer and renders it as ESC/POS bytes, as drawn
/// bitmaps, or as plain text.
/// </summary>
/// <remarks>
/// <para>
/// The plain-text rendering is not a debugging afterthought. Receipt bugs are layout bugs — a
/// column that does not line up, a long item name that swallows the price — and those are far
/// easier to see and to assert as text than as a byte array. The tests check layout against
/// <see cref="ToPlainText"/> and check the command bytes against <see cref="EscPos"/> directly, so
/// each is tested by whichever form makes the failure obvious. The diagnostics CLI prints the same
/// text as a preview.
/// </para>
/// <para>
/// Every line is kept twice over: as the composed, space-padded string the character path prints,
/// and as the segments it was composed from. The two are not redundant. Character padding aligns
/// columns by counting characters, which is exactly right for a monospaced printer font and
/// meaningless for Tamil drawn in a proportional face — there, a column lands where its segment
/// says it lands, measured in dots. Keeping both means the same layout call produces a correct
/// receipt down either path.
/// </para>
/// </remarks>
public sealed class ReceiptBuilder
{
    /// <summary>Characters per line on 80mm paper in the default font.</summary>
    public const int Width80Mm = 48;

    /// <summary>Characters per line on 58mm paper in the default font.</summary>
    public const int Width58Mm = 32;

    private readonly List<Directive> _directives = [];

    public ReceiptBuilder(int paperWidthChars = Width80Mm)
    {
        if (paperWidthChars < 16)
            throw new ArgumentOutOfRangeException(nameof(paperWidthChars), paperWidthChars, "A receipt narrower than 16 characters cannot lay out a line and a price.");

        PaperWidthChars = paperWidthChars;
    }

    public int PaperWidthChars { get; }

    /// <summary>One line of text, wrapped if it does not fit.</summary>
    public ReceiptBuilder Text(
        string? text,
        TextAlignment alignment = TextAlignment.Left,
        bool bold = false,
        int widthMultiplier = 1,
        int heightMultiplier = 1)
    {
        // Double-width characters take two cells, so a scaled line fits half as much.
        var usable = Math.Max(1, PaperWidthChars / widthMultiplier);

        foreach (var line in Wrap(text ?? string.Empty, usable))
        {
            // The segment spans the whole paper and carries its own alignment, so the drawn form
            // does not inherit the character path's padding — which was counted at the scaled
            // width and would put a centred heading in the wrong place on a proportional face.
            _directives.Add(new Directive.Line(
                Justify(line, usable, alignment),
                [new Segment(line, 0, PaperWidthChars, alignment)],
                alignment,
                bold,
                widthMultiplier,
                heightMultiplier));
        }

        return this;
    }

    /// <summary>A label on the left and a figure hard against the right margin.</summary>
    public ReceiptBuilder Columns(string left, string right, bool bold = false)
    {
        var rightText = right ?? string.Empty;
        var leftText = left ?? string.Empty;

        // The figure is what must survive; the label gives way when the line is tight.
        var leftRoom = Math.Max(0, PaperWidthChars - rightText.Length - 1);
        leftText = Truncate(leftText, leftRoom);

        var gap = Math.Max(1, PaperWidthChars - leftText.Length - rightText.Length);

        _directives.Add(new Directive.Line(
            leftText + new string(' ', gap) + rightText,
            [
                new Segment(leftText, 0, PaperWidthChars, TextAlignment.Left),
                new Segment(rightText, 0, PaperWidthChars, TextAlignment.Right),
            ],
            TextAlignment.Left,
            bold,
            1,
            1));

        return this;
    }

    /// <summary>
    /// A line divided into cells of stated widths, each with its own alignment.
    /// </summary>
    /// <remarks>
    /// What a label/value pair repeated across a line needs — a bill number and a date on one line,
    /// a customer and a time on the next, or a block of tenders two by two. <see cref="Columns"/>
    /// only ever has a left and a right, and building this out of it would put every cell in the
    /// wrong place the moment the text was not English.
    /// </remarks>
    public ReceiptBuilder Cells(params ReceiptCell[] cells)
    {
        ArgumentNullException.ThrowIfNull(cells);

        if (cells.Length == 0)
            return Blank();

        var composed = new StringBuilder(PaperWidthChars);
        var segments = new List<Segment>(cells.Length);
        var offset = 0;

        foreach (var cell in cells)
        {
            var width = Math.Max(0, Math.Min(cell.Width, PaperWidthChars - offset));

            if (width == 0)
                break;

            var text = Truncate(cell.Text ?? string.Empty, width);

            composed.Append(cell.Alignment switch
            {
                TextAlignment.Right => text.PadLeft(width),
                TextAlignment.Center => text.PadLeft(text.Length + ((width - text.Length) / 2)).PadRight(width),
                _ => text.PadRight(width),
            });

            segments.Add(new Segment(text, offset, width, cell.Alignment));
            offset += width;
        }

        _directives.Add(new Directive.Line(composed.ToString(), segments, TextAlignment.Left, false, 1, 1));
        return this;
    }

    /// <summary>
    /// Narrowest a description column may be before the figures are moved onto their own line.
    /// Below this an item name shreds into fragments a few characters wide, which is not a receipt
    /// anybody can read.
    /// </summary>
    private const int MinDescriptionWidth = 14;

    /// <summary>
    /// A description with fixed-width figures beside it, as an invoice line needs. The description
    /// takes whatever is left and wraps onto continuation lines; the figures print against the
    /// first line only.
    /// </summary>
    /// <remarks>
    /// On narrow paper the figures will not fit beside a readable name, so the row stacks instead:
    /// the name across the full width, and the figures right-aligned on the line beneath. That is
    /// how 58mm receipts are laid out in practice, and it is the difference between "Toor Dal 1kg"
    /// and four lines reading "Toor", "Dal", "1kg".
    /// </remarks>
    public ReceiptBuilder Row(string description, params ColumnValue[] columns)
    {
        ArgumentNullException.ThrowIfNull(columns);

        var text = description ?? string.Empty;
        var fixedWidth = columns.Sum(c => c.Width + 1);
        var descriptionWidth = PaperWidthChars - fixedWidth;

        // Stack only when the description both lacks room and actually needs it. A short label
        // like "5%" fits in the gap beside the figures however narrow the paper is, and moving it
        // onto its own line would be worse, not better.
        if (descriptionWidth < MinDescriptionWidth && text.Length > descriptionWidth)
            return StackedRow(text, columns);

        descriptionWidth = Math.Max(1, descriptionWidth);

        var wrapped = Wrap(text, descriptionWidth).ToList();

        if (wrapped.Count == 0)
            wrapped.Add(string.Empty);

        var figures = new StringBuilder();
        var segments = new List<Segment>(columns.Length + 1)
        {
            new(wrapped[0], 0, descriptionWidth, TextAlignment.Left),
        };

        var offset = descriptionWidth;

        foreach (var column in columns)
        {
            var value = Truncate(column.Text, column.Width);
            figures.Append(' ').Append(value.PadLeft(column.Width));

            // The leading space belongs to the gutter, not to the figure, so the drawn column is
            // right-aligned against the same edge the character-padded one lands on.
            segments.Add(new Segment(value, offset + 1, column.Width, TextAlignment.Right));
            offset += column.Width + 1;
        }

        _directives.Add(new Directive.Line(
            PadRight(wrapped[0], descriptionWidth) + figures,
            segments,
            TextAlignment.Left,
            false,
            1,
            1));

        for (var i = 1; i < wrapped.Count; i++)
        {
            _directives.Add(new Directive.Line(
                wrapped[i],
                [new Segment(wrapped[i], 0, descriptionWidth, TextAlignment.Left)],
                TextAlignment.Left,
                false,
                1,
                1));
        }

        return this;
    }

    /// <summary>
    /// The narrow-paper form of <see cref="Row"/>: description across the full width, figures
    /// right-aligned beneath it.
    /// </summary>
    private ReceiptBuilder StackedRow(string description, ColumnValue[] columns)
    {
        foreach (var line in Wrap(description, PaperWidthChars))
        {
            _directives.Add(new Directive.Line(
                line,
                [new Segment(line, 0, PaperWidthChars, TextAlignment.Left)],
                TextAlignment.Left,
                false,
                1,
                1));
        }

        var figures = string.Join(' ', columns.Select(column => PadLeft(column.Text, column.Width)));

        if (figures.Trim().Length > 0)
        {
            _directives.Add(new Directive.Line(
                PadLeft(figures, PaperWidthChars),
                [new Segment(figures, 0, PaperWidthChars, TextAlignment.Right)],
                TextAlignment.Left,
                false,
                1,
                1));
        }

        return this;
    }

    /// <summary>A full-width run of the given character, as a separator.</summary>
    public ReceiptBuilder Rule(char character = '-')
    {
        _directives.Add(new Directive.Rule(character));
        return this;
    }

    public ReceiptBuilder Blank(int lines = 1)
    {
        for (var i = 0; i < lines; i++)
            _directives.Add(new Directive.Line(string.Empty, [], TextAlignment.Left, false, 1, 1));

        return this;
    }

    /// <summary>Advances the paper without printing, for the tear-off margin.</summary>
    public ReceiptBuilder Feed(int lines)
    {
        _directives.Add(new Directive.Feed(lines));
        return this;
    }

    public ReceiptBuilder Cut(CutMode mode = CutMode.Partial, int feedBeforeCut = 4)
    {
        _directives.Add(new Directive.Cut(mode, feedBeforeCut));
        return this;
    }

    /// <summary>Pulses a drawer wired to the printer's RJ11 port, in the same job as the receipt.</summary>
    public ReceiptBuilder KickDrawer(int pin = 0, int onMilliseconds = 60, int offMilliseconds = 120)
    {
        _directives.Add(new Directive.Kick(pin, onMilliseconds, offMilliseconds));
        return this;
    }

    /// <summary>Renders to the byte stream a printer consumes.</summary>
    /// <param name="encoding">
    /// The code page for the character path. Null reduces text to ASCII, which prints identically
    /// whatever the printer is set to.
    /// </param>
    /// <param name="raster">
    /// How to draw the lines the printer has no glyphs for. Null means never draw anything, which
    /// is the behaviour of a lane that prints only English.
    /// </param>
    public byte[] ToEscPos(Encoding? encoding = null, RasterOptions? raster = null)
    {
        var bytes = new List<byte>(512);
        bytes.AddRange(EscPos.Initialize());

        var alignment = TextAlignment.Left;
        var bold = false;
        var width = 1;
        var height = 1;

        // A drawn line is a full-width image, so the printer's own justification has to be off or
        // it would centre the image as well as the text inside it.
        void ResetForRaster()
        {
            if (alignment != TextAlignment.Left)
            {
                bytes.AddRange(EscPos.Align(TextAlignment.Left));
                alignment = TextAlignment.Left;
            }
        }

        foreach (var directive in _directives)
        {
            switch (directive)
            {
                case Directive.Line line when ShouldRaster(line, raster):
                    ResetForRaster();
                    bytes.AddRange(EscPos.RasterImage(DrawLine(line, raster!)));
                    break;

                case Directive.Line line:
                    // Only emit a mode change when the mode actually changes; a receipt that resets
                    // bold on every line wastes bytes and makes the output hard to read in a dump.
                    if (line.Alignment != alignment)
                    {
                        bytes.AddRange(EscPos.Align(line.Alignment));
                        alignment = line.Alignment;
                    }

                    if (line.Bold != bold)
                    {
                        bytes.AddRange(EscPos.Bold(line.Bold));
                        bold = line.Bold;
                    }

                    if (line.WidthMultiplier != width || line.HeightMultiplier != height)
                    {
                        bytes.AddRange(EscPos.TextSize(line.WidthMultiplier, line.HeightMultiplier));
                        width = line.WidthMultiplier;
                        height = line.HeightMultiplier;
                    }

                    bytes.AddRange(EscPos.Line(line.Text.TrimEnd(), encoding));
                    break;

                case Directive.Rule rule when raster is { Mode: RasterMode.Always }:
                    ResetForRaster();
                    bytes.AddRange(EscPos.RasterImage(DrawRule(rule.Character, raster)));
                    break;

                case Directive.Rule rule:
                    if (alignment != TextAlignment.Left)
                    {
                        bytes.AddRange(EscPos.Align(TextAlignment.Left));
                        alignment = TextAlignment.Left;
                    }

                    if (bold)
                    {
                        bytes.AddRange(EscPos.Bold(false));
                        bold = false;
                    }

                    if (width != 1 || height != 1)
                    {
                        bytes.AddRange(EscPos.NormalTextSize());
                        width = height = 1;
                    }

                    bytes.AddRange(EscPos.Line(new string(rule.Character, PaperWidthChars), encoding));
                    break;

                case Directive.Feed feed:
                    bytes.AddRange(EscPos.Feed(feed.Lines));
                    break;

                case Directive.Cut cut:
                    // Formatting is reset before the cut so the next receipt starts clean even if
                    // this one ended mid-emphasis.
                    if (bold)
                    {
                        bytes.AddRange(EscPos.Bold(false));
                        bold = false;
                    }

                    if (width != 1 || height != 1)
                    {
                        bytes.AddRange(EscPos.NormalTextSize());
                        width = height = 1;
                    }

                    bytes.AddRange(EscPos.Cut(cut.Mode, cut.FeedBeforeCut));
                    break;

                case Directive.Kick kick:
                    bytes.AddRange(EscPos.KickDrawer(kick.Pin, kick.OnMilliseconds, kick.OffMilliseconds));
                    break;
            }
        }

        return [.. bytes];
    }

    private static bool ShouldRaster(Directive.Line line, RasterOptions? raster) => raster?.Mode switch
    {
        RasterMode.Always => line.Segments.Count > 0,
        RasterMode.Auto => line.Segments.Any(s => RasterOptions.NeedsRaster(s.Text)),
        _ => false,
    };

    /// <summary>
    /// Draws one line's segments into a strip of dots, each in the cell range it was laid out in.
    /// </summary>
    internal MonochromeBitmap DrawLine(Directive.Line line, RasterOptions raster)
    {
        var style = new RasterTextStyle(line.Bold, line.WidthMultiplier, line.HeightMultiplier);
        var strip = new MonochromeBitmap(raster.PaperWidthDots, Math.Max(1, raster.Rasterizer.LineHeight(style)));

        foreach (var segment in line.Segments)
        {
            if (string.IsNullOrEmpty(segment.Text))
                continue;

            var left = CellToDots(segment.CellStart, raster.PaperWidthDots);
            var right = CellToDots(segment.CellStart + segment.CellWidth, raster.PaperWidthDots);
            var room = Math.Max(0, right - left);
            var measured = raster.Rasterizer.Measure(segment.Text, style);

            // An over-long run is left where it starts rather than pushed off the paper by an
            // alignment calculation that assumed it fitted.
            var x = measured >= room
                ? left
                : segment.Alignment switch
                {
                    TextAlignment.Right => right - measured,
                    TextAlignment.Center => left + ((room - measured) / 2),
                    _ => left,
                };

            raster.Rasterizer.Draw(strip, segment.Text, x, 0, style);
        }

        return strip;
    }

    /// <summary>A separator as dots: solid for '=', a dashed run for anything else.</summary>
    private static MonochromeBitmap DrawRule(char character, RasterOptions raster)
    {
        var strip = new MonochromeBitmap(raster.PaperWidthDots, character == '=' ? 3 : 1);

        for (var x = 0; x < strip.Width; x++)
        {
            // A dashed rule reads as a rule rather than as a smudge, and matches what the
            // character path prints on the lines around it.
            if (character != '=' && (x % 4) >= 2)
                continue;

            for (var y = 0; y < strip.Height; y++)
                strip[x, y] = true;
        }

        return strip;
    }

    private int CellToDots(int cell, int paperWidthDots) =>
        (int)Math.Round((double)Math.Clamp(cell, 0, PaperWidthChars) * paperWidthDots / PaperWidthChars);

    /// <summary>
    /// Renders the whole receipt as the dots a printer would burn.
    /// </summary>
    /// <remarks>
    /// Not a printing path — it is how the output gets looked at. A character preview counts
    /// characters, which says nothing about where a Tamil label actually lands or whether it
    /// collides with the figure beside it, and those are the only two questions worth asking about
    /// a bilingual receipt. Every line goes through the same layout the printer gets, including the
    /// ones that would have been sent as characters, so what comes out is a fair picture rather
    /// than a second implementation.
    /// </remarks>
    public MonochromeBitmap ToBitmap(RasterOptions raster)
    {
        ArgumentNullException.ThrowIfNull(raster);

        var baseHeight = Math.Max(1, raster.Rasterizer.LineHeight(new RasterTextStyle()));
        var strips = new List<MonochromeBitmap?>();
        var total = 0;

        void Add(MonochromeBitmap? strip, int height)
        {
            strips.Add(strip);
            total += height;
        }

        foreach (var directive in _directives)
        {
            switch (directive)
            {
                case Directive.Line line:
                    var drawn = DrawLine(line, raster);
                    Add(drawn, drawn.Height);
                    break;

                case Directive.Rule rule:
                    var ruled = DrawRule(rule.Character, raster);

                    // A rule is a few dots tall but occupies a whole line on paper, so the gap
                    // around it is what stops the preview reading tighter than the receipt.
                    Add(ruled, baseHeight);
                    break;

                case Directive.Feed feed:
                    Add(null, feed.Lines * baseHeight);
                    break;

                case Directive.Cut:
                    Add(null, baseHeight);
                    break;

                case Directive.Kick:
                    break;
            }
        }

        var page = new MonochromeBitmap(raster.PaperWidthDots, Math.Max(1, total));
        var y = 0;
        var index = 0;

        foreach (var directive in _directives)
        {
            switch (directive)
            {
                case Directive.Line:
                    y += Blit(page, strips[index++], y, 0);
                    break;

                case Directive.Rule:
                    var strip = strips[index++];
                    Blit(page, strip, y, (baseHeight - (strip?.Height ?? 0)) / 2);
                    y += baseHeight;
                    break;

                case Directive.Feed feed:
                    index++;
                    y += feed.Lines * baseHeight;
                    break;

                case Directive.Cut:
                    index++;
                    y += baseHeight;
                    break;
            }
        }

        return page;
    }

    /// <summary>Copies a strip onto the page and reports how tall it was.</summary>
    private static int Blit(MonochromeBitmap page, MonochromeBitmap? strip, int top, int inset)
    {
        if (strip is null)
            return 0;

        for (var row = 0; row < strip.Height; row++)
        {
            for (var column = 0; column < strip.Width; column++)
            {
                if (strip[column, row])
                    page[column, top + inset + row] = true;
            }
        }

        return strip.Height;
    }

    /// <summary>Renders what the paper will look like, for tests and for the diagnostics preview.</summary>
    public string ToPlainText()
    {
        var text = new StringBuilder();

        foreach (var directive in _directives)
        {
            switch (directive)
            {
                case Directive.Line line:
                    // Scaled text was laid out against a line half as wide, so its indent has to be
                    // scaled back up or a centred heading looks left-shifted in the preview when it
                    // is correctly centred on paper. The characters themselves still show at single
                    // width — a preview cannot render double-width type — so a scaled line reads
                    // narrower here than it prints.
                    text.AppendLine(Rescale(line.Text, line.WidthMultiplier).TrimEnd());
                    break;

                case Directive.Rule rule:
                    text.AppendLine(new string(rule.Character, PaperWidthChars));
                    break;

                case Directive.Feed feed:
                    for (var i = 0; i < feed.Lines; i++)
                        text.AppendLine();
                    break;

                case Directive.Cut:
                    text.AppendLine(new string('=', PaperWidthChars));
                    break;

                case Directive.Kick:
                    break;
            }
        }

        return text.ToString();
    }

    /// <summary>The laid-out lines, for tests that need to know where a segment landed.</summary>
    internal IReadOnlyList<Directive> Directives => _directives;

    /// <summary>Widens a scaled line's indent so the preview shows where it actually starts.</summary>
    private static string Rescale(string text, int widthMultiplier)
    {
        if (widthMultiplier <= 1)
            return text;

        var indent = text.Length - text.TrimStart(' ').Length;

        return indent == 0 ? text : new string(' ', indent * widthMultiplier) + text[indent..];
    }

    // ---- Layout helpers ----------------------------------------------------------------------

    private static string Justify(string text, int width, TextAlignment alignment) => alignment switch
    {
        TextAlignment.Center => text.PadLeft(text.Length + Math.Max(0, (width - text.Length) / 2)),
        TextAlignment.Right => PadLeft(text, width),
        _ => text,
    };

    /// <summary>
    /// Breaks text on word boundaries, falling back to a hard break for a single word longer than
    /// the paper — a barcode or a run-on product code, which must not silently vanish.
    /// </summary>
    internal static IEnumerable<string> Wrap(string text, int width)
    {
        if (width <= 0)
            yield break;

        if (text.Length == 0)
        {
            yield return string.Empty;
            yield break;
        }

        foreach (var paragraph in text.Split('\n'))
        {
            var remaining = paragraph.Trim();

            if (remaining.Length == 0)
            {
                yield return string.Empty;
                continue;
            }

            while (remaining.Length > width)
            {
                var breakAt = remaining.LastIndexOf(' ', Math.Min(width, remaining.Length - 1));

                if (breakAt <= 0)
                    breakAt = width;

                yield return remaining[..breakAt].TrimEnd();
                remaining = remaining[breakAt..].TrimStart();
            }

            yield return remaining;
        }
    }

    internal static string Truncate(string text, int width) =>
        width <= 0 ? string.Empty : text.Length <= width ? text : text[..width];

    private static string PadLeft(string text, int width) => Truncate(text, width).PadLeft(width);

    private static string PadRight(string text, int width) => Truncate(text, width).PadRight(width);

    /// <summary>One run of text and the cell range it was laid out in.</summary>
    internal readonly record struct Segment(string Text, int CellStart, int CellWidth, TextAlignment Alignment);

    internal abstract record Directive
    {
        public sealed record Line(
            string Text,
            IReadOnlyList<Segment> Segments,
            TextAlignment Alignment,
            bool Bold,
            int WidthMultiplier,
            int HeightMultiplier) : Directive;

        public sealed record Rule(char Character) : Directive;

        public sealed record Feed(int Lines) : Directive;

        public sealed record Cut(CutMode Mode, int FeedBeforeCut) : Directive;

        public sealed record Kick(int Pin, int OnMilliseconds, int OffMilliseconds) : Directive;
    }
}

/// <summary>A fixed-width figure printed beside a description in <see cref="ReceiptBuilder.Row"/>.</summary>
public readonly record struct ColumnValue(string Text, int Width);

/// <summary>One cell of a <see cref="ReceiptBuilder.Cells"/> line.</summary>
public readonly record struct ReceiptCell(string Text, int Width, TextAlignment Alignment = TextAlignment.Left);
