using System.Globalization;

namespace CafePOS.Api.Infrastructure;

/// <summary>
/// Server-side twin of the app's buildUpiPaymentUri (CafePOS/src/core/payments/upi.ts).
/// The two exist separately because the two documents are built in different places — the
/// thermal bill is composed on the device, the WhatsApp bill PDF here — but they must
/// produce the byte-identical link for the same order, or a guest who scans the paper slip
/// and a guest who scans the PDF would be charged differently. Change one, change the other.
///
/// Same caveat as the TypeScript side: this is an intent link, not a payment gateway.
/// Nothing reports back when the guest pays; staff still settle the order by hand.
/// </summary>
public static class UpiPaymentLink
{
    /// <summary>Matches MAX_NOTE_LENGTH in upi.ts — several UPI apps truncate or reject
    /// longer notes.</summary>
    private const int MaxNoteLength = 50;

    /// <summary>
    /// Null when there's nothing sensible to charge — no VPA configured, or a
    /// zero/negative amount (a comped or already-settled bill). Callers treat null as
    /// "don't show a QR here".
    /// </summary>
    public static string? Build(string? vpa, string payeeName, decimal amount, string? note = null)
    {
        var trimmedVpa = vpa?.Trim();
        if (string.IsNullOrEmpty(trimmedVpa) || amount <= 0) return null;

        var name = string.IsNullOrWhiteSpace(payeeName) ? "Cafe" : payeeName.Trim();
        // InvariantCulture: a server running under a comma-decimal locale would otherwise
        // emit "am=448,47", which every UPI app rejects.
        var parts = new List<string>
        {
            $"pa={Uri.EscapeDataString(trimmedVpa)}",
            $"pn={Uri.EscapeDataString(name)}",
            $"am={amount.ToString("0.00", CultureInfo.InvariantCulture)}",
            "cu=INR",
        };

        var trimmedNote = note?.Trim();
        if (!string.IsNullOrEmpty(trimmedNote))
        {
            if (trimmedNote.Length > MaxNoteLength) trimmedNote = trimmedNote[..MaxNoteLength];
            parts.Add($"tn={Uri.EscapeDataString(trimmedNote)}");
        }

        return $"upi://pay?{string.Join("&", parts)}";
    }
}
