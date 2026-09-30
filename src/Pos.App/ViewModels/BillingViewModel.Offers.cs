using System.Globalization;
using Pos.Core.Domain;

namespace Pos.App.ViewModels;

public sealed partial class BillingViewModel
{
    private IReadOnlyList<Offer> _offers = [];

    /// <summary>What the last pass of the offers newly gave, for the line saying what was added.</summary>
    private string _offerNote = string.Empty;

    /// <summary>
    /// The shop's offers and schemes. Set by the composition root, and again when the owner loads a
    /// new offers sheet, so the next change to the bill is priced by the new list.
    /// </summary>
    public IReadOnlyList<Offer> Offers
    {
        get => _offers;
        set
        {
            _offers = value ?? [];

            if (Mode == BillingMode.Billing && !_bill.IsEmpty)
                RefreshTotals();
        }
    }

    /// <summary>
    /// Works the offers out again on the bill as it stands, and puts each line's discount right.
    /// </summary>
    /// <remarks>
    /// Run before every total is shown, so the third soap brings the free one and taking it off
    /// takes it back. A line discounted by hand is never touched. Not while payment is being taken:
    /// the bill is fixed then, and what is being paid is what was on screen.
    /// </remarks>
    private void ApplyOffers()
    {
        if (Mode == BillingMode.Tender || _bill.IsEmpty)
            return;

        // With no offers, only lines still carrying one need putting right - a recalled bill, or an
        // offer the owner has just taken off.
        if (_offers.Count == 0 && _bill.Lines.All(l => l.OfferName is null))
            return;

        var today = DateOnly.FromDateTime(_now().DateTime);
        var notes = new List<string>();

        foreach (var given in OfferEngine.Work(_bill.Lines, _offers, today))
        {
            var line = _bill.Lines[given.Index];

            if (line.Discount == given.Discount && line.OfferName == given.OfferName)
                continue;

            var before = line.Discount;
            _bill.ApplyOffer(given.Index, given.Discount, given.OfferName);

            if (given.Index < Lines.Count)
                Lines[given.Index].Refresh();

            if (given.OfferName is { } name && given.Discount > before)
                notes.Add($"{name} - {given.Discount.ToString("N2", CultureInfo.InvariantCulture)} off {line.NameSnapshot}");
        }

        _offerNote = notes.Count == 0 ? string.Empty : $" Offer: {string.Join("; ", notes)}.";
    }

    /// <summary>What the offers just gave, said once and then forgotten.</summary>
    private string TakeOfferNote()
    {
        var note = _offerNote;
        _offerNote = string.Empty;
        return note;
    }
}
