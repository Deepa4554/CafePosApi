using CafePOS.Api.Domain;

namespace CafePOS.Api.Infrastructure;

/// <summary>What the bill calls itself. A GST invoice has to say which kind of document it is:
/// an officer reading an untitled slip cannot tell a tax invoice from a receipt, and a cafe
/// charging GST on a slip that never claims to be a tax invoice is issuing something its own
/// customer cannot claim credit against.
///
/// Mirrored in TypeScript by billDocumentTitle in receiptFormat.ts — the thermal slip and the
/// PDF of the same bill must not disagree about what they are.</summary>
public static class BillDocument
{
    /// <summary>Composition dealers come first because they are the case a GSTIN alone gets
    /// wrong: they HAVE a registration and print it, but may not issue a tax invoice against it
    /// — they supply at their own rate and their customer claims no credit.
    ///
    /// A cafe with no GSTIN at all is not registered, so it can issue neither a tax invoice nor
    /// a bill of supply (both are GST documents). Its slip is titled plainly, which is also what
    /// every existing cafe that has never entered a GSTIN keeps getting.</summary>
    public static string Title(CafeSettings settings) =>
        settings.IsCompositionScheme ? "BILL OF SUPPLY"
        : !string.IsNullOrWhiteSpace(settings.GstNumber) ? "TAX INVOICE"
        : "BILL";
}
