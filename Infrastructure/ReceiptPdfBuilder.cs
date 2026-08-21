using CafePOS.Api.Contracts;
using CafePOS.Api.Domain;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace CafePOS.Api.Infrastructure;

/// <summary>
/// Renders a receipt PDF straight from an Order + CafeSettings — no separate "receipt"
/// data model, no persisted file. It's generated fresh on every request (see
/// PublicController's receipt endpoint), so it always reflects the order's current state
/// (e.g. after a refund) instead of going stale like a stored copy would.
/// </summary>
public static class ReceiptPdfBuilder
{
    /// <param name="logo">The cafe's logo image bytes, or null. Optional so a bill still
    /// renders when the logo is missing or its host is unreachable — see CafeLogoLoader.</param>
    public static byte[] Build(CafeSettings settings, Order order, byte[]? logo = null)
    {
        var businessName = string.IsNullOrWhiteSpace(settings.BusinessName) ? "CafePOS" : settings.BusinessName;

        return Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A6);
                page.Margin(20);
                page.DefaultTextStyle(x => x.FontSize(10));

                page.Content().Column(col =>
                {
                    col.Spacing(4);

                    // Height-capped rather than width-fitted: logos are all shapes, and a wide
                    // banner scaled to the page width would push the whole bill down a page
                    // on A6. FitArea keeps the aspect ratio inside that box either way.
                    if (logo is not null)
                        col.Item().AlignCenter().Height(45).Image(logo).FitArea();

                    col.Item().AlignCenter().Text(businessName).FontSize(16).Bold();
                    if (!string.IsNullOrWhiteSpace(settings.Address))
                        col.Item().AlignCenter().Text(settings.Address).FontSize(8);
                    if (!string.IsNullOrWhiteSpace(settings.Phone))
                        col.Item().AlignCenter().Text(settings.Phone).FontSize(8);
                    // Last of the header block: a licence number is something an inspector or
                    // a customer looks up, not something anyone reads first.
                    if (!string.IsNullOrWhiteSpace(settings.LicenceNumber))
                        col.Item().AlignCenter().Text($"Licence No: {settings.LicenceNumber}").FontSize(8);

                    col.Item().PaddingTop(8).LineHorizontal(0.5f);

                    col.Item().Text($"Order {OrderNumberFormat.Bill(order)}").Bold();
                    col.Item().Text(order.Title).FontSize(9);
                    // CreatedAt is stored UTC; the guest holding this bill reads the cafe's own
                    // clock, so it has to be shifted — printed raw it showed every bill 5:30
                    // early (a 4:53 PM order billed as 11:23 AM).
                    col.Item().Text($"{IstClock.ToIst(order.CreatedAt):dd MMM yyyy, hh:mm tt}").FontSize(8);
                    if (!string.IsNullOrWhiteSpace(order.GuestName))
                        col.Item().Text($"Guest: {order.GuestName}").FontSize(9);

                    col.Item().PaddingTop(6).LineHorizontal(0.5f);

                    // Live lines only. A voided line stays on the order forever (never deleted,
                    // so the KOT and void history survive) but is not billed — every money
                    // figure below comes from RecomputeTotals, which sums non-voided lines
                    // alone. Itemising the voided ones here printed food the guest was not
                    // charged for, on the very document handed to them as the bill.
                    foreach (var item in order.Items.Where(i => !i.Voided))
                    {
                        var variantSuffix = item.VariantName is null ? "" : $" ({item.VariantName})";
                        col.Item().Row(row =>
                        {
                            row.RelativeItem(3).Text($"{item.Qty}x {item.Name}{variantSuffix}{(string.IsNullOrWhiteSpace(item.Modifier) ? "" : $" — {item.Modifier}")}");
                            row.RelativeItem(1).AlignRight().Text($"{item.Price * item.Qty:0.00}");
                        });
                        foreach (var mod in item.SelectedModifiers)
                            col.Item().PaddingLeft(12).Text($"+ {mod.Name}").FontSize(8).FontColor(Colors.Grey.Darken1);
                    }

                    col.Item().PaddingTop(6).LineHorizontal(0.5f);

                    col.Item().Row(row =>
                    {
                        row.RelativeItem().Text("Subtotal");
                        row.RelativeItem().AlignRight().Text($"{order.Subtotal:0.00}");
                    });
                    if (order.DiscountAmount > 0)
                    {
                        col.Item().Row(row =>
                        {
                            row.RelativeItem().Text("Discount");
                            row.RelativeItem().AlignRight().Text($"-{order.DiscountAmount:0.00}");
                        });
                    }
                    if (order.OfferDiscountAmount > 0)
                    {
                        col.Item().Row(row =>
                        {
                            // Name the offer that fired ("Buy 2 Get 1 — Coffee") so the customer
                            // sees why the bill dropped, not an unexplained line.
                            row.RelativeItem().Text(string.IsNullOrWhiteSpace(order.AppliedOfferTitle) ? "Offer" : order.AppliedOfferTitle);
                            row.RelativeItem().AlignRight().Text($"-{order.OfferDiscountAmount:0.00}");
                        });
                    }
                    // One row per tax slab on the bill — a mixed 5%/12% order has to show the
                    // taxable value and tax for each rate separately, not one combined figure —
                    // and each slab is split into its CGST and SGST halves, which is what makes
                    // this a tax invoice rather than just a receipt (see GstSplit).
                    var taxLines = OrderTaxLineDto.From(order, settings.TaxRatePct);
                    if (order.Tax <= 0)
                    {
                        // No GST charged (unregistered or composition scheme) — one plain row
                        // beats printing a CGST and an SGST line that both read 0.00.
                        col.Item().Row(row =>
                        {
                            row.RelativeItem().Text("Tax");
                            row.RelativeItem().AlignRight().Text($"{order.Tax:0.00}");
                        });
                    }
                    else if (taxLines.Count <= 1)
                    {
                        // Halve order.Tax rather than the slab's own figure so the two printed
                        // rows always reconcile against the total the customer is paying.
                        var singleRate = taxLines.Count == 1 ? taxLines[0].RatePct : settings.TaxRatePct;
                        var half = GstSplit.HalfRate(singleRate);
                        var (cgst, sgst) = GstSplit.Split(order.Tax);

                        col.Item().Row(row =>
                        {
                            row.RelativeItem().Text($"CGST ({half:0.##}%)");
                            row.RelativeItem().AlignRight().Text($"{cgst:0.00}");
                        });
                        col.Item().Row(row =>
                        {
                            row.RelativeItem().Text($"SGST ({half:0.##}%)");
                            row.RelativeItem().AlignRight().Text($"{sgst:0.00}");
                        });
                    }
                    else
                    {
                        foreach (var taxLine in taxLines)
                        {
                            var half = GstSplit.HalfRate(taxLine.RatePct);
                            var (cgst, sgst) = GstSplit.Split(taxLine.TaxAmount);

                            col.Item().Row(row =>
                            {
                                row.RelativeItem().Text($"CGST {half:0.##}% (on {taxLine.TaxableAmount:0.00})");
                                row.RelativeItem().AlignRight().Text($"{cgst:0.00}");
                            });
                            col.Item().Row(row =>
                            {
                                row.RelativeItem().Text($"SGST {half:0.##}% (on {taxLine.TaxableAmount:0.00})");
                                row.RelativeItem().AlignRight().Text($"{sgst:0.00}");
                            });
                        }
                    }

                    col.Item().PaddingTop(4).LineHorizontal(0.5f);

                    col.Item().Row(row =>
                    {
                        row.RelativeItem().Text("Total").Bold().FontSize(13);
                        row.RelativeItem().AlignRight().Text($"{order.Total:0.00}").Bold().FontSize(13);
                    });

                    if (order.Refunded)
                    {
                        col.Item().PaddingTop(6).AlignCenter().Text($"REFUNDED{(order.RefundedAmount is decimal r ? $" — {r:0.00}" : "")}").FontSize(9).Bold();
                    }

                    // Scan-to-pay block. This PDF is the bill a waiter carries to the table
                    // (it's generated before settlement and sent over WhatsApp), so it's the
                    // copy a guest is most likely to pay from — the thermal slip and the bill
                    // screen carry the same QR, built from the same link (see UpiPaymentLink).
                    //
                    // Charged on the outstanding balance, not Total, so a part-paid bill asks
                    // only for what's left; a settled or refunded one drops the block entirely
                    // rather than inviting a second payment.
                    var alreadyPaid = order.Payments.Sum(p => p.Amount);
                    var outstanding = order.Total - alreadyPaid;
                    var upiUri = order.Paid || order.Refunded
                        ? null
                        : UpiPaymentLink.Build(settings.UpiVpa, businessName, outstanding, $"Bill {OrderNumberFormat.Bill(order)}");

                    if (upiUri is not null)
                    {
                        col.Item().PaddingTop(10).LineHorizontal(0.5f);
                        col.Item().PaddingTop(6).AlignCenter().Text("SCAN TO PAY").FontSize(9).Bold();
                        col.Item().AlignCenter().Text($"{outstanding:0.00}").FontSize(12).Bold();
                        // PngByteQRCode rather than QRCoder's System.Drawing-backed renderers:
                        // this runs on Linux containers where System.Drawing.Common isn't
                        // supported at all. 10 px per module keeps it sharp when a phone
                        // camera reads it off a screen rather than paper.
                        col.Item().PaddingTop(4).AlignCenter().Width(120).Image(BuildQrPng(upiUri));
                        col.Item().PaddingTop(2).AlignCenter().Text(settings.UpiVpa).FontSize(8);
                    }

                    col.Item().PaddingTop(10).AlignCenter().Text(
                        string.IsNullOrWhiteSpace(settings.ReceiptFooter) ? "Thank you for visiting!" : settings.ReceiptFooter
                    ).FontSize(9).Italic();
                });
            });
        }).GeneratePdf();
    }

    /// <summary>Encodes a payment link as a PNG the PDF can embed. ECC level M is the usual
    /// choice for a payment QR — enough redundancy to survive a phone photographing a screen
    /// or a creased slip, without inflating the code so much it stops scanning at this
    /// size.</summary>
    private static byte[] BuildQrPng(string payload)
    {
        using var generator = new QRCoder.QRCodeGenerator();
        using var data = generator.CreateQrCode(payload, QRCoder.QRCodeGenerator.ECCLevel.M);
        return new QRCoder.PngByteQRCode(data).GetGraphic(10);
    }
}
