using System.Globalization;
using System.Text;
using System.Xml;
using Pos.Core.Domain;

namespace Pos.Core.Analytics;

/// <summary>
/// Writes a day book as the files an accountant loads: a CSV any spreadsheet or accounting package
/// reads, and the same vouchers in Tally's XML import format, with the ledgers they use.
/// </summary>
/// <remarks>
/// <para>
/// Two Tally files rather than one. The ledgers file creates the ledgers the vouchers name - the
/// shop's customers on the khata, its suppliers, a sales ledger for each rate - and is loaded first;
/// loading it again over ledgers that already exist does no harm. The vouchers file is the month.
/// </para>
/// <para>
/// In Tally's format a debit is written as a negative amount with <c>ISDEEMEDPOSITIVE</c> set, and a
/// credit as a positive one without it. The vouchers are plain accounting vouchers, with no stock in
/// them: the till keeps the stock, and the books need only the money.
/// </para>
/// </remarks>
public static class DayBookFiles
{
    /// <summary>A file name for a month's day book: "daybook-L1-2026-09".</summary>
    public static string Stem(DayBookData data) =>
        $"daybook-{data.LaneId}-{data.From.ToString("yyyy-MM", CultureInfo.InvariantCulture)}";

    /// <summary>Writes the CSV to <paramref name="csvPath"/> and the two Tally files beside it.</summary>
    /// <returns>Every file written, the CSV first.</returns>
    public static IReadOnlyList<string> Write(DayBookData data, string csvPath)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentException.ThrowIfNullOrWhiteSpace(csvPath);

        var folder = Path.GetDirectoryName(Path.GetFullPath(csvPath))!;
        var stem = Path.GetFileNameWithoutExtension(csvPath);
        Directory.CreateDirectory(folder);

        var ledgersPath = Path.Combine(folder, $"{stem}-tally-ledgers.xml");
        var vouchersPath = Path.Combine(folder, $"{stem}-tally-vouchers.xml");

        // With a byte-order mark, as the GST files: Excel reads a CSV without one in the machine's
        // own code page, and a customer's name in Tamil comes out as nonsense.
        File.WriteAllText(csvPath, Csv(data), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        File.WriteAllText(ledgersPath, TallyLedgers(data), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        File.WriteAllText(vouchersPath, TallyVouchers(data), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        return [csvPath, ledgersPath, vouchersPath];
    }

    // ---- The CSV ------------------------------------------------------------------------------------

    /// <summary>One row per entry: the voucher's date, type, number and party on every row, so it sorts and filters.</summary>
    public static string Csv(DayBookData data)
    {
        ArgumentNullException.ThrowIfNull(data);

        var csv = new StringBuilder();
        csv.AppendLine("Date,Voucher type,Voucher no,Party,Ledger,Group,Debit,Credit,Narration");

        foreach (var voucher in data.Vouchers)
        {
            foreach (var entry in voucher.Entries)
            {
                csv.AppendLine(Row(
                    voucher.Date.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture),
                    TypeName(voucher.Kind),
                    voucher.Number,
                    voucher.Party ?? string.Empty,
                    entry.Ledger,
                    GroupName(entry.Group),
                    entry.Debit == 0m ? string.Empty : Amount(entry.Debit),
                    entry.Credit == 0m ? string.Empty : Amount(entry.Credit),
                    voucher.Narration));
            }
        }

        return csv.ToString();
    }

    // ---- Tally ----------------------------------------------------------------------------------

    /// <summary>Every ledger the vouchers name, under the group it belongs in.</summary>
    public static string TallyLedgers(DayBookData data)
    {
        ArgumentNullException.ThrowIfNull(data);

        var ledgers = data.Vouchers
            .SelectMany(v => v.Entries)
            .GroupBy(e => e.Ledger, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Name: g.First().Ledger, g.First().Group))
            .OrderBy(l => l.Group)
            .ThenBy(l => l.Name, StringComparer.OrdinalIgnoreCase);

        return Envelope("All Masters", xml =>
        {
            foreach (var (name, group) in ledgers)
            {
                xml.WriteStartElement("TALLYMESSAGE");
                xml.WriteAttributeString("xmlns", "UDF", null, "TallyUDF");

                xml.WriteStartElement("LEDGER");
                xml.WriteAttributeString("NAME", name);
                xml.WriteAttributeString("ACTION", "Create");

                xml.WriteStartElement("NAME.LIST");
                xml.WriteElementString("NAME", name);
                xml.WriteEndElement();

                xml.WriteElementString("PARENT", GroupName(group));

                xml.WriteEndElement();
                xml.WriteEndElement();
            }
        });
    }

    /// <summary>The vouchers, in order, each with its ledger entries.</summary>
    public static string TallyVouchers(DayBookData data)
    {
        ArgumentNullException.ThrowIfNull(data);

        return Envelope("Vouchers", xml =>
        {
            foreach (var voucher in data.Vouchers)
            {
                var type = TypeName(voucher.Kind);
                var date = voucher.Date.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

                xml.WriteStartElement("TALLYMESSAGE");
                xml.WriteAttributeString("xmlns", "UDF", null, "TallyUDF");

                xml.WriteStartElement("VOUCHER");
                xml.WriteAttributeString("VCHTYPE", type);
                xml.WriteAttributeString("ACTION", "Create");

                xml.WriteElementString("DATE", date);
                xml.WriteElementString("EFFECTIVEDATE", date);
                xml.WriteElementString("VOUCHERTYPENAME", type);
                xml.WriteElementString("VOUCHERNUMBER", voucher.Number);

                if (voucher.Party is { } party)
                    xml.WriteElementString("PARTYLEDGERNAME", party);

                xml.WriteElementString("NARRATION", voucher.Narration);

                foreach (var entry in voucher.Entries)
                {
                    var debit = entry.Debit != 0m;

                    xml.WriteStartElement("ALLLEDGERENTRIES.LIST");
                    xml.WriteElementString("LEDGERNAME", entry.Ledger);
                    xml.WriteElementString("ISDEEMEDPOSITIVE", debit ? "Yes" : "No");
                    xml.WriteElementString("AMOUNT", debit ? "-" + Amount(entry.Debit) : Amount(entry.Credit));
                    xml.WriteEndElement();
                }

                xml.WriteEndElement();
                xml.WriteEndElement();
            }
        });
    }

    private static string Envelope(string report, Action<XmlWriter> body)
    {
        var text = new StringBuilder();
        var settings = new XmlWriterSettings { Indent = true, OmitXmlDeclaration = true, NewLineChars = "\n" };

        using (var xml = XmlWriter.Create(text, settings))
        {
            xml.WriteStartElement("ENVELOPE");

            xml.WriteStartElement("HEADER");
            xml.WriteElementString("TALLYREQUEST", "Import Data");
            xml.WriteEndElement();

            xml.WriteStartElement("BODY");
            xml.WriteStartElement("IMPORTDATA");

            xml.WriteStartElement("REQUESTDESC");
            xml.WriteElementString("REPORTNAME", report);
            xml.WriteEndElement();

            xml.WriteStartElement("REQUESTDATA");
            body(xml);
            xml.WriteEndElement();

            xml.WriteEndElement();
            xml.WriteEndElement();
            xml.WriteEndElement();
        }

        return text.Append('\n').ToString();
    }

    // ---- Names ----------------------------------------------------------------------------------

    /// <summary>The voucher type, as an accounting package names its own.</summary>
    public static string TypeName(VoucherKind kind) => kind switch
    {
        VoucherKind.Sales => "Sales",
        VoucherKind.CreditNote => "Credit Note",
        VoucherKind.Receipt => "Receipt",
        VoucherKind.Purchase => "Purchase",
        VoucherKind.Payment => "Payment",
        VoucherKind.DebitNote => "Debit Note",
        VoucherKind.Contra => "Contra",
        _ => kind.ToString(),
    };

    /// <summary>The group, as Tally names the groups it starts every company with.</summary>
    public static string GroupName(LedgerGroup group) => group switch
    {
        LedgerGroup.CashInHand => "Cash-in-Hand",
        LedgerGroup.BankAccounts => "Bank Accounts",
        LedgerGroup.SundryDebtors => "Sundry Debtors",
        LedgerGroup.SundryCreditors => "Sundry Creditors",
        LedgerGroup.SalesAccounts => "Sales Accounts",
        LedgerGroup.PurchaseAccounts => "Purchase Accounts",
        LedgerGroup.DutiesAndTaxes => "Duties & Taxes",
        LedgerGroup.IndirectExpenses => "Indirect Expenses",
        LedgerGroup.Suspense => "Suspense A/c",
        _ => group.ToString(),
    };

    /// <summary>Plainly, for software: no grouping, no sign, a full stop, two places.</summary>
    private static string Amount(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);

    private static string Row(params string[] fields) =>
        string.Join(',', fields.Select(f => f.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? $"\"{f.Replace("\"", "\"\"", StringComparison.Ordinal)}\"" : f));
}
