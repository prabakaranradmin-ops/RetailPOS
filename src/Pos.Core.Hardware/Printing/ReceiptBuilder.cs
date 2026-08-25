using System.Text;

namespace Pos.Core.Hardware.Printing;

/// <summary>
/// Lays out a receipt for a fixed-width thermal printer and renders it either as ESC/POS bytes or
/// as plain text.
/// </summary>
/// <remarks>
/// The plain-text rendering is not a debugging afterthought. Receipt bugs are layout bugs — a
/// column that does not line up, a long item name that swallows the price — and those are far
/// easier to see and to assert as text than as a byte array. The tests check layout against
/// <see cref="ToPlainText"/> and check the command bytes against <see cref="EscPos"/> directly, so
/// each is tested by whichever form makes the failure obvious. The diagnostics CLI prints the same
/// text as a preview.
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
            _directives.Add(new Directive.Line(Justify(line, usable, alignment), alignment, bold, widthMultiplier, heightMultiplier));

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

        _directives.Add(new Directive.Line(leftText + new string(' ', gap) + rightText, TextAlignment.Left, bold, 1, 1));
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

        foreach (var column in columns)
            figures.Append(' ').Append(PadLeft(column.Text, column.Width));

        _directives.Add(new Directive.Line(PadRight(wrapped[0], descriptionWidth) + figures, TextAlignment.Left, false, 1, 1));

        for (var i = 1; i < wrapped.Count; i++)
            _directives.Add(new Directive.Line(wrapped[i], TextAlignment.Left, false, 1, 1));

        return this;
    }

    /// <summary>
    /// The narrow-paper form of <see cref="Row"/>: description across the full width, figures
    /// right-aligned beneath it.
    /// </summary>
    private ReceiptBuilder StackedRow(string description, ColumnValue[] columns)
    {
        foreach (var line in Wrap(description, PaperWidthChars))
            _directives.Add(new Directive.Line(line, TextAlignment.Left, false, 1, 1));

        var figures = string.Join(' ', columns.Select(column => PadLeft(column.Text, column.Width)));

        if (figures.Trim().Length > 0)
            _directives.Add(new Directive.Line(PadLeft(figures, PaperWidthChars), TextAlignment.Left, false, 1, 1));

        return this;
    }

    /// <summary>A full-width run of the given character, as a separator.</summary>
    public ReceiptBuilder Rule(char character = '-')
    {
        _directives.Add(new Directive.Line(new string(character, PaperWidthChars), TextAlignment.Left, false, 1, 1));
        return this;
    }

    public ReceiptBuilder Blank(int lines = 1)
    {
        for (var i = 0; i < lines; i++)
            _directives.Add(new Directive.Line(string.Empty, TextAlignment.Left, false, 1, 1));

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
    public byte[] ToEscPos(Encoding? encoding = null)
    {
        var bytes = new List<byte>(512);
        bytes.AddRange(EscPos.Initialize());

        var alignment = TextAlignment.Left;
        var bold = false;
        var width = 1;
        var height = 1;

        foreach (var directive in _directives)
        {
            switch (directive)
            {
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

    private abstract record Directive
    {
        public sealed record Line(string Text, TextAlignment Alignment, bool Bold, int WidthMultiplier, int HeightMultiplier) : Directive;

        public sealed record Feed(int Lines) : Directive;

        public sealed record Cut(CutMode Mode, int FeedBeforeCut) : Directive;

        public sealed record Kick(int Pin, int OnMilliseconds, int OffMilliseconds) : Directive;
    }
}

/// <summary>A fixed-width figure printed beside a description in <see cref="ReceiptBuilder.Row"/>.</summary>
public readonly record struct ColumnValue(string Text, int Width);
