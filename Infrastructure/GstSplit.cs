namespace CafePOS.Api.Infrastructure;

/// <summary>Splits a GST figure into the Central and State halves a tax invoice has to show
/// separately. A bill that prints one combined "GST 5%" line is not a valid tax invoice — the
/// rules require the central and state components to be stated individually.
///
/// The halves are always equal, which is what makes this a presentation concern rather than a
/// change to how tax is computed: every Indian GST slab a restaurant bills at (5/12/18/28)
/// divides evenly, so nothing upstream of the receipt needs to know about the split. IGST never
/// enters into it — a meal is consumed where it is sold, so restaurant supply is intra-state.
///
/// Composition-scheme cafes issue a bill of supply and charge no GST at all; they reach this
/// code with a zero tax figure, and callers drop the rows rather than printing two zeros.</summary>
public static class GstSplit
{
    /// <summary>The per-component rate to print beside each half — a 5% slab is billed as
    /// CGST 2.5% + SGST 2.5%.</summary>
    public static decimal HalfRate(decimal ratePct) => ratePct / 2m;

    /// <summary>Halves a tax amount into (CGST, SGST) that always add back to exactly the
    /// figure passed in. An odd paise cannot be halved — ₹16.65 splits 8.32/8.33 — so the
    /// remainder is handed to SGST instead of rounding both halves up and printing a bill whose
    /// tax rows out-total its own tax.
    ///
    /// Done in whole paise rather than by halving the decimal: rounding the half is what makes
    /// the answer depend on the rounding mode, and the TypeScript twin (splitGst in
    /// receiptFormat.ts) would then disagree with this on the odd amount — the printed slip and
    /// the PDF of the same bill have to state identical tax.</summary>
    public static (decimal Cgst, decimal Sgst) Split(decimal taxAmount)
    {
        var totalPaise = (long)Math.Round(taxAmount * 100m, MidpointRounding.AwayFromZero);
        var cgstPaise = totalPaise / 2;
        return (cgstPaise / 100m, (totalPaise - cgstPaise) / 100m);
    }
}
