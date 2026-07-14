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
    public static byte[] Build(CafeSettings settings, Order order)
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

                    col.Item().AlignCenter().Text(businessName).FontSize(16).Bold();
                    if (!string.IsNullOrWhiteSpace(settings.Address))
                        col.Item().AlignCenter().Text(settings.Address).FontSize(8);
                    if (!string.IsNullOrWhiteSpace(settings.Phone))
                        col.Item().AlignCenter().Text(settings.Phone).FontSize(8);

                    col.Item().PaddingTop(8).LineHorizontal(0.5f);

                    col.Item().Text($"Order #{1000 + order.Id}").Bold();
                    col.Item().Text(order.Title).FontSize(9);
                    col.Item().Text($"{order.CreatedAt:dd MMM yyyy, hh:mm tt}").FontSize(8);
                    if (!string.IsNullOrWhiteSpace(order.GuestName))
                        col.Item().Text($"Guest: {order.GuestName}").FontSize(9);

                    col.Item().PaddingTop(6).LineHorizontal(0.5f);

                    foreach (var item in order.Items)
                    {
                        col.Item().Row(row =>
                        {
                            row.RelativeItem(3).Text($"{item.Qty}x {item.Name}{(string.IsNullOrWhiteSpace(item.Modifier) ? "" : $" ({item.Modifier})")}");
                            row.RelativeItem(1).AlignRight().Text($"{item.Price * item.Qty:0.00}");
                        });
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
                    col.Item().Row(row =>
                    {
                        row.RelativeItem().Text("Tax");
                        row.RelativeItem().AlignRight().Text($"{order.Tax:0.00}");
                    });

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

                    col.Item().PaddingTop(10).AlignCenter().Text(
                        string.IsNullOrWhiteSpace(settings.ReceiptFooter) ? "Thank you for visiting!" : settings.ReceiptFooter
                    ).FontSize(9).Italic();
                });
            });
        }).GeneratePdf();
    }
}
