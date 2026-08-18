using CafePOS.Api.Domain;

namespace CafePOS.Api.Infrastructure;

/// <summary>
/// The one place a bill number is turned into the string a human sees. Every receipt, PDF,
/// UPI note, khata row, customer history line and search result goes through here, so the
/// format can be changed (padding, a per-cafe prefix) in a single edit instead of hunting
/// down the half-dozen call sites that used to each interpolate their own copy.
///
/// It reads Order.BillNumber — the cafe's own counter — never Order.Id. Id is one identity
/// sequence shared by every tenant in the database, so showing it leaked the platform's total
/// order count and printed a brand-new cafe's first bill as "#1455".
/// </summary>
public static class OrderNumberFormat
{
    /// <summary>Bills created before BillNumber existed were backfilled by the
    /// AddPerTenantBillNumber migration, so 0 only shows up if an order was somehow written
    /// without going through OrderBuildingService. Falling back to the id keeps such a row
    /// referenceable (support can still find the order) instead of printing a bare "#0" on
    /// every one of them.</summary>
    public static string Bill(int billNumber, int orderId) =>
        billNumber > 0 ? $"#{billNumber}" : $"#{1000 + orderId}";

    public static string Bill(Order order) => Bill(order.BillNumber, order.Id);
}
