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
    /// <summary>One billing-time reduction or charge line, in the order they print. Split out
    /// of Build so the "what goes on the bill" decision — a row only when it actually fired —
    /// is a plain, PDF-independent function that can be unit tested without rendering a PDF
    /// (see ReceiptPdfBuilderTests). Mirrors receiptFormat.ts's pushReduction/pushCharge on the
    /// printed slip of the same order, so both documents state the identical set of rows.</summary>
    /// <param name="IsTaxedCharge">This charge was itself taxed (see CafeSettings.TaxChargesEnabled),
    /// so it has to print ABOVE the GST rows — the taxable value on those rows already includes
    /// it, and printing it below would state on the invoice that it had been added after tax.
    /// False for every untaxed charge and every reduction, which is the pre-existing layout.</param>
    public readonly record struct BillAdjustmentLine(string Label, decimal Amount, bool IsReduction, bool IsTaxedCharge = false);

    /// <summary>Every reduction/charge row RecomputeTotals folded into Order.Total, gated on
    /// having actually fired — an order with nothing applied returns an empty list, so the PDF
    /// renders exactly the plain Subtotal/Tax/Total it always did. Round Off is handled
    /// separately by the caller: unlike these, its sign can go either way.</summary>
    public static List<BillAdjustmentLine> BuildAdjustmentLines(Order order)
    {
        var lines = new List<BillAdjustmentLine>();
        void Reduction(string label, decimal amount) { if (amount > 0) lines.Add(new BillAdjustmentLine(label, amount, true)); }
        void Charge(string label, decimal amount, bool taxed = false) { if (amount > 0) lines.Add(new BillAdjustmentLine(label, amount, false, taxed)); }
        // Whether Service/Packing/Delivery carried tax on THIS bill — snapshotted per order, so
        // a cafe that switched the setting on last week still prints its older bills the old way.
        var chargesTaxed = order.ChargesTaxAmount != 0;

        Reduction("Discount", order.DiscountAmount);
        Reduction("Bill Discount", order.BillDiscountAmount);
        Reduction(string.IsNullOrWhiteSpace(order.CouponCode) ? "Coupon" : $"Coupon ({order.CouponCode})", order.CouponDiscountAmount);
        // Name the offer that fired ("Buy 2 Get 1 — Coffee") so the customer sees why the bill
        // dropped, not an unexplained line.
        Reduction(string.IsNullOrWhiteSpace(order.AppliedOfferTitle) ? "Offer" : order.AppliedOfferTitle, order.OfferDiscountAmount);
        Reduction(string.IsNullOrWhiteSpace(order.GiftCardCode) ? "Gift Card" : $"Gift Card ({order.GiftCardCode})", order.GiftCardAmountApplied);
        Reduction(order.LoyaltyPointsRedeemed > 0 ? $"Loyalty Points ({order.LoyaltyPointsRedeemed})" : "Loyalty Points", order.LoyaltyDiscountAmount);
        // Where these print depends on whether they were taxed: above the GST rows when they
        // were (they are inside the taxable value shown there), below when they weren't (added
        // on top of an already-computed tax). See Build, which reads IsTaxedCharge to place them.
        Charge("Service Charge", order.ServiceChargeAmount, chargesTaxed);
        Charge("Packing Charge", order.PackingChargeAmount, chargesTaxed);
        Charge("Delivery Charge", order.DeliveryChargeAmount, chargesTaxed);
        // A tip is never taxed — it isn't consideration for the supply — so it always prints
        // below the GST rows, even at a cafe that taxes the three charges above.
        Charge("Tip", order.TipAmount);
        return lines;
    }

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
                    // Receipt Builder toggle (see Cafe Settings → Receipt Builder) — the printed
                    // slip already honours this (receiptFormat.ts); the PDF ignored it entirely,
                    // so a cafe that turned the address off still had it on every WhatsApp bill.
                    if (!string.IsNullOrWhiteSpace(settings.Address) && settings.ReceiptShowAddress)
                        col.Item().AlignCenter().Text(settings.Address).FontSize(8);
                    if (!string.IsNullOrWhiteSpace(settings.Phone))
                        col.Item().AlignCenter().Text(settings.Phone).FontSize(8);
                    if (!string.IsNullOrWhiteSpace(settings.GstNumber))
                        col.Item().AlignCenter().Text($"GSTIN: {settings.GstNumber}").FontSize(8);
                    // Last of the header block: a licence number is something an inspector or
                    // a customer looks up, not something anyone reads first.
                    if (!string.IsNullOrWhiteSpace(settings.LicenceNumber))
                        col.Item().AlignCenter().Text($"Licence No: {settings.LicenceNumber}").FontSize(8);

                    col.Item().PaddingTop(8).LineHorizontal(0.5f);

                    // What kind of document this is — see BillDocument. Sits between the cafe's
                    // identity block and the order's own details because that is where a reader
                    // looks for it, and where the same line sits on the thermal slip.
                    col.Item().PaddingTop(4).AlignCenter().Text(BillDocument.Title(settings)).FontSize(11).Bold();
                    col.Item().PaddingBottom(4).LineHorizontal(0.5f);

                    col.Item().Text($"Order {OrderNumberFormat.Bill(order)}").Bold();
                    col.Item().Text(order.Title).FontSize(9);
                    // CreatedAt is stored UTC; the guest holding this bill reads the cafe's own
                    // clock, so it has to be shifted — printed raw it showed every bill 5:30
                    // early (a 4:53 PM order billed as 11:23 AM).
                    col.Item().Text($"{IstClock.ToIst(order.CreatedAt):dd MMM yyyy, hh:mm tt}").FontSize(8);
                    if (!string.IsNullOrWhiteSpace(order.GuestName))
                        col.Item().Text($"Guest: {order.GuestName}").FontSize(9);
                    var waiterName = order.ServedByName ?? order.CreatedByName;
                    if (!string.IsNullOrWhiteSpace(waiterName) && settings.ReceiptShowWaiterName)
                        col.Item().Text($"Waiter: {waiterName}").FontSize(9);
                    if (!string.IsNullOrWhiteSpace(order.GuestPhone) && settings.ReceiptShowGuestPhone)
                        col.Item().Text($"Mobile: {order.GuestPhone}").FontSize(9);

                    col.Item().PaddingTop(6).LineHorizontal(0.5f);

                    // Live lines only. A voided line stays on the order forever (never deleted,
                    // so the KOT and void history survive) but is not billed — every money
                    // figure below comes from RecomputeTotals, which sums non-voided lines
                    // alone. Itemising the voided ones here printed food the guest was not
                    // charged for, on the very document handed to them as the bill.
                    foreach (var item in order.Items.Where(i => !i.Voided))
                    {
                        var variantSuffix = item.VariantName is null ? "" : $" ({item.VariantName})";
                        // Free-text kitchen note, gated on Receipt Builder's own toggle — the
                        // printed slip already respects this (receiptFormat.ts's showItemNotes).
                        var modifierSuffix = !settings.ReceiptShowItemNotes || string.IsNullOrWhiteSpace(item.Modifier)
                            ? "" : $" — {item.Modifier}";
                        col.Item().Row(row =>
                        {
                            row.RelativeItem(3).Text($"{item.Qty}x {item.Name}{variantSuffix}{modifierSuffix}");
                            row.RelativeItem(1).AlignRight().Text($"{item.Price * item.Qty:0.00}");
                        });
                        // Under the line rather than in a column of its own: this bill renders
                        // as narrow as A6, where a fourth column would squeeze the item name to
                        // nothing. Printed only where a code exists, so a cafe that has entered
                        // none gets exactly the bill it had before (see MenuItem.HsnCode).
                        if (!string.IsNullOrWhiteSpace(item.HsnCode))
                            col.Item().PaddingLeft(12).Text($"HSN/SAC: {item.HsnCode}").FontSize(7).FontColor(Colors.Grey.Darken1);
                        foreach (var mod in item.SelectedModifiers)
                            col.Item().PaddingLeft(12).Text($"+ {mod.Name}").FontSize(8).FontColor(Colors.Grey.Darken1);
                    }

                    col.Item().PaddingTop(6).LineHorizontal(0.5f);

                    // Every row below prints only when it actually fired, so a bill with
                    // nothing applied renders exactly the plain Subtotal/Tax/Total it always
                    // did (see BuildAdjustmentLines). Split into reductions and charges because
                    // that's where RecomputeTotals places each: reductions come off the taxable
                    // base, so they print before the GST rows; charges are added on top of tax,
                    // so they print after — printing a charge above the tax rows would state on
                    // the invoice that it had been taxed.
                    var adjustmentLines = BuildAdjustmentLines(order);

                    col.Item().Row(row =>
                    {
                        row.RelativeItem().Text("Subtotal");
                        row.RelativeItem().AlignRight().Text($"{order.Subtotal:0.00}");
                    });
                    foreach (var line in adjustmentLines.Where(l => l.IsReduction))
                    {
                        col.Item().Row(row =>
                        {
                            row.RelativeItem().Text(line.Label);
                            row.RelativeItem().AlignRight().Text($"-{line.Amount:0.00}");
                        });
                    }
                    // Charges that were themselves taxed belong above the GST rows — the taxable
                    // value printed there includes them. Empty at every cafe that hasn't opted in.
                    foreach (var line in adjustmentLines.Where(l => l.IsTaxedCharge))
                    {
                        col.Item().Row(row =>
                        {
                            row.RelativeItem().Text(line.Label);
                            row.RelativeItem().AlignRight().Text($"{line.Amount:0.00}");
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

                    foreach (var line in adjustmentLines.Where(l => !l.IsReduction && !l.IsTaxedCharge))
                    {
                        col.Item().Row(row =>
                        {
                            row.RelativeItem().Text(line.Label);
                            row.RelativeItem().AlignRight().Text($"{line.Amount:0.00}");
                        });
                    }
                    if (order.RoundOffAmount != 0)
                    {
                        // The sign is the whole point of this row — a bare figure printed
                        // against a total that went down reads as an unexplained charge.
                        var sign = order.RoundOffAmount > 0 ? "+" : "-";
                        col.Item().Row(row =>
                        {
                            row.RelativeItem().Text("Round Off");
                            row.RelativeItem().AlignRight().Text($"{sign}{Math.Abs(order.RoundOffAmount):0.00}");
                        });
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

                    // "Rate us on Google" block. Below scan-to-pay and never instead of it: the
                    // one QR that costs the cafe money if it's missed is the one that collects
                    // the bill. Unlike that block this one prints on a settled bill too — a
                    // review is asked for on the way out, which is exactly when the bill is
                    // already paid. Absent entirely until the cafe sets a link (see
                    // CafeSettings.GoogleReviewUrl), so nothing changes for a cafe that doesn't.
                    if (!string.IsNullOrWhiteSpace(settings.GoogleReviewUrl))
                    {
                        col.Item().PaddingTop(10).LineHorizontal(0.5f);
                        col.Item().PaddingTop(6).AlignCenter().Text("ENJOYED YOUR VISIT?").FontSize(9).Bold();
                        col.Item().AlignCenter().Text("Scan to rate us on Google").FontSize(8);
                        // Smaller than the payment QR above (120): this one is scanned off a
                        // phone or a screen at leisure, not squinted at to settle a bill, and a
                        // second full-size code would crowd the foot of the page.
                        col.Item().PaddingTop(4).AlignCenter().Width(96).Image(BuildQrPng(settings.GoogleReviewUrl));
                    }

                    if (settings.ReceiptShowFooter)
                    {
                        col.Item().PaddingTop(10).AlignCenter().Text(
                            string.IsNullOrWhiteSpace(settings.ReceiptFooter) ? "Thank you for visiting!" : settings.ReceiptFooter
                        ).FontSize(9).Italic();
                    }
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
