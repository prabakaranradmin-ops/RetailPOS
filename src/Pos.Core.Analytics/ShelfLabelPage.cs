using System.Globalization;
using System.Text;
using Pos.Core.Domain;

namespace Pos.Core.Analytics;

/// <summary>
/// Shelf labels as a page to print on A4 from any printer: three across, cut along the dashed lines.
/// </summary>
/// <remarks>
/// An EAN-13 barcode is drawn as real bars a scanner reads off the paper; any other code - a SKU on
/// something sold loose - is printed as text. The page is white and plain: it is for paper, not for
/// a screen.
/// </remarks>
public static class ShelfLabelPage
{
    private static readonly CultureInfo India = CultureInfo.GetCultureInfo("en-IN");

    public static string Render(IReadOnlyList<ShelfLabel> labels, string shopName)
    {
        ArgumentNullException.ThrowIfNull(labels);

        var p = new StringBuilder(16 * 1024);

        p.Append("<!doctype html>\n<html lang=\"en\">\n<head>\n<meta charset=\"utf-8\">\n");
        p.Append($"<title>{Escape(shopName)} — shelf labels</title>\n");
        p.Append("""
            <style>
              @page { size: A4; margin: 8mm; }
              * { box-sizing: border-box; }
              body { margin: 0; font-family: "Segoe UI", "Nirmala UI", Arial, sans-serif; color: #000; background: #fff; }
              .sheet { display: grid; grid-template-columns: repeat(3, 1fr); }
              .label { border: 1px dashed #999; padding: 3mm 4mm; height: 38mm; overflow: hidden; break-inside: avoid;
                       display: flex; flex-direction: column; justify-content: space-between; }
              .name { font-weight: 600; font-size: 11pt; line-height: 1.2; max-height: 2.4em; overflow: hidden; }
              .row { display: flex; justify-content: space-between; font-size: 8.5pt; }
              .price { font-size: 20pt; font-weight: 700; text-align: center; line-height: 1; }
              .price small { font-size: 9pt; font-weight: 400; }
              .code { text-align: center; font-family: Consolas, monospace; font-size: 8pt; }
              .code svg { display: block; margin: 0 auto; height: 9mm; }
              .note { font-size: 9pt; color: #555; margin: 4mm 0; }
              @media print { .note { display: none; } }
            </style>
            </head>
            <body>
            """);

        p.Append($"<p class=\"note\">{Plural.Of(labels.Count, "label")} for {Escape(shopName)}. Print on A4 at actual size, then cut along the dashed lines.</p>\n");
        p.Append("<div class=\"sheet\">\n");

        foreach (var label in labels)
        {
            p.Append("<div class=\"label\">");
            p.Append($"<div class=\"name\">{Escape(label.Name)}</div>");
            p.Append($"<div class=\"row\"><span>MRP Rs {Money(label.Mrp)}</span><span>{(label.Saving > 0m ? $"You save Rs {Money(label.Saving)}" : "")}</span></div>");
            p.Append($"<div class=\"price\">Rs {Money(label.Price)} <small>/ {Escape(Units.Of(label.Unit).Code)}</small></div>");
            p.Append("<div class=\"code\">");

            if (Ean13.Bars(label.Code) is { } bars)
                p.Append(Svg(bars));

            p.Append(Escape(label.Code));
            p.Append("</div></div>\n");
        }

        p.Append("</div>\n</body>\n</html>");
        return p.ToString();
    }

    /// <summary>The bars as an SVG, one unit per module, with the guard bars run long as printed codes have them.</summary>
    private static string Svg(bool[] bars)
    {
        var svg = new StringBuilder();
        svg.Append($"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 {bars.Length + 18} 50\" preserveAspectRatio=\"none\" width=\"38mm\">");

        for (var i = 0; i < bars.Length; i++)
        {
            if (bars[i])
                svg.Append(CultureInfo.InvariantCulture, $"<rect x=\"{i + 9}\" y=\"0\" width=\"1\" height=\"50\"/>");
        }

        svg.Append("</svg>");
        return svg.ToString();
    }

    private static string Money(decimal value) => value.ToString("N2", India);

    private static string Escape(string? text) => string.IsNullOrEmpty(text)
        ? string.Empty
        : text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
}

/// <summary>EAN-13, as the 95 modules of bars and spaces a scanner reads.</summary>
/// <remarks>
/// The published symbology: a start guard, six digits in odd or even parity chosen by the first
/// digit, a centre guard, six digits in the right-hand set, an end guard.
/// </remarks>
public static class Ean13
{
    private static readonly string[] Odd = ["0001101", "0011001", "0010011", "0111101", "0100011", "0110001", "0101111", "0111011", "0110111", "0001011"];
    private static readonly string[] Even = ["0100111", "0110011", "0011011", "0100001", "0011101", "0111001", "0000101", "0010001", "0001001", "0010111"];
    private static readonly string[] Right = ["1110010", "1100110", "1101100", "1000010", "1011100", "1001110", "1010000", "1000100", "1001000", "1110100"];
    private static readonly string[] Parity = ["OOOOOO", "OOEOEE", "OOEEOE", "OOEEEO", "OEOOEE", "OEEOOE", "OEEEOO", "OEOEOE", "OEOEEO", "OEEOEO"];

    /// <summary>The modules, true for a bar; null for anything that is not a valid EAN-13.</summary>
    public static bool[]? Bars(string? code)
    {
        if (code is null || code.Length != 13 || !code.All(char.IsAsciiDigit)
            || Hardware.Scanning.Barcode.Identify(code) != Hardware.Scanning.Symbology.Ean13
            || !Hardware.Scanning.Barcode.IsValid(code))
        {
            return null;
        }

        var modules = new StringBuilder(95);
        var parity = Parity[code[0] - '0'];

        modules.Append("101");

        for (var i = 1; i <= 6; i++)
            modules.Append(parity[i - 1] == 'O' ? Odd[code[i] - '0'] : Even[code[i] - '0']);

        modules.Append("01010");

        for (var i = 7; i <= 12; i++)
            modules.Append(Right[code[i] - '0']);

        modules.Append("101");

        return [.. modules.ToString().Select(c => c == '1')];
    }
}
