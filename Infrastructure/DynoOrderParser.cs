using System.Globalization;
using System.Text.Json;

namespace CafePOS.Api.Infrastructure;

/// <summary>One line read out of an aggregator's order document.</summary>
/// <param name="PlatformItemId">The aggregator's own item id, when it gave one — this is what a
/// PlatformMenuMapping row is keyed on, and without it a line can only be matched by name.</param>
public record DynoParsedLine(string? PlatformItemId, string? Name, int Qty, decimal? UnitPrice);

/// <param name="Lines">Empty when nothing recognisable was found — the caller must still ingest
/// the order (see DynoWebhookController), never discard it.</param>
public record DynoParsedOrder(
    List<DynoParsedLine> Lines,
    string? CustomerName,
    string? CustomerPhone,
    decimal? GrossAmount);

/// <summary>
/// Reads an aggregator's raw order document into something the order builder can use.
///
/// This is written defensively on purpose. Dyno's OpenAPI spec types every response as an untyped
/// `{}` (FastAPI with no response_model), so the document under `data` is whatever Zomato or
/// Swiggy happened to return, and the two do not agree with each other. Rather than hard-code one
/// guessed schema and fail loudly on the other, this walks the document looking for the first
/// array that reads like order lines and pulls fields by trying the names these APIs are known to
/// use, in order of specificity.
///
/// The contract with callers is deliberately weak: a miss returns empty <see cref="DynoParsedOrder.Lines"/>
/// rather than throwing, because an order that arrives during a dinner rush must reach the kitchen
/// as *something* even if we cannot read its lines. The raw document is archived alongside the
/// created order (PlatformOrderPayload) so that once a real payload has been seen, this parser can
/// be corrected and historic orders re-read — which is the whole reason it keeps the original.
/// </summary>
public static class DynoOrderParser
{
    // Ordered most- to least- specific: "item_name" is unambiguous, "name" could be the
    // restaurant's. First hit on a given object wins.
    private static readonly string[] ItemIdKeys =
        ["item_id", "itemId", "catalogue_id", "catalogueId", "menu_item_id", "menuItemId", "id"];
    private static readonly string[] NameKeys =
        ["item_name", "itemName", "dish_name", "dishName", "title", "name"];
    private static readonly string[] QtyKeys =
        ["quantity", "qty", "item_quantity", "itemQuantity", "count"];
    private static readonly string[] UnitPriceKeys =
        ["unit_cost", "unitCost", "unit_price", "unitPrice", "base_price", "basePrice", "item_price", "itemPrice", "price"];
    private static readonly string[] LineTotalKeys =
        ["total_cost", "totalCost", "total_price", "totalPrice", "final_price", "finalPrice", "subtotal", "total"];

    private static readonly string[] CustomerNameKeys =
        ["customer_name", "customerName", "user_name", "userName", "name"];
    private static readonly string[] CustomerPhoneKeys =
        ["customer_phone", "customerPhone", "phone_number", "phoneNumber", "mobile", "phone", "contact_number"];
    private static readonly string[] GrossKeys =
        ["total_cost", "totalCost", "order_total", "orderTotal", "net_amount", "netAmount", "bill_amount", "billAmount", "total"];

    /// <summary>Objects whose key contains one of these are customer/address blocks worth
    /// descending into for name and phone.</summary>
    private static readonly string[] CustomerContainerKeys = ["customer", "user", "delivery", "address", "contact"];

    public static DynoParsedOrder Parse(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object && data.ValueKind != JsonValueKind.Array)
            return new DynoParsedOrder([], null, null, null);

        var lines = new List<DynoParsedLine>();
        var itemsArray = FindItemsArray(data, depth: 0);
        if (itemsArray is { } arr)
        {
            foreach (var el in arr.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object) continue;
                var line = ReadLine(el);
                if (line is not null) lines.Add(line);
            }
        }

        return new DynoParsedOrder(
            lines,
            FindString(data, CustomerNameKeys, depth: 0),
            FindString(data, CustomerPhoneKeys, depth: 0),
            FindDecimal(data, GrossKeys, depth: 0));
    }

    /// <summary>Depth-first hunt for the array that holds the order's lines. Prefers a property
    /// whose NAME says items (order_items, items, dishes); falls back to any array whose first
    /// object element looks line-shaped, so an unexpected key name doesn't defeat it.</summary>
    private static JsonElement? FindItemsArray(JsonElement node, int depth)
    {
        if (depth > 6) return null;

        if (node.ValueKind == JsonValueKind.Array)
            return LooksLikeLines(node) ? node : null;

        if (node.ValueKind != JsonValueKind.Object) return null;

        // Named candidates first, at this level, before descending.
        foreach (var prop in node.EnumerateObject())
        {
            if (prop.Value.ValueKind != JsonValueKind.Array) continue;
            var key = prop.Name.ToLowerInvariant();
            if ((key.Contains("item") || key.Contains("dish") || key.Contains("product") || key.Contains("line"))
                && LooksLikeLines(prop.Value))
                return prop.Value;
        }

        foreach (var prop in node.EnumerateObject())
        {
            var found = FindItemsArray(prop.Value, depth + 1);
            if (found is not null) return found;
        }

        return null;
    }

    /// <summary>An array is line-shaped if its first object element carries something namelike and
    /// something quantity- or price-like. Two signals rather than one, so an array of category
    /// names or image URLs isn't mistaken for the cart.</summary>
    private static bool LooksLikeLines(JsonElement array)
    {
        foreach (var el in array.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.Object) return false;
            var hasName = TryGet(el, NameKeys, out _);
            var hasQtyOrPrice = TryGet(el, QtyKeys, out _) || TryGet(el, UnitPriceKeys, out _) || TryGet(el, LineTotalKeys, out _);
            return hasName && hasQtyOrPrice;
        }
        return false;
    }

    private static DynoParsedLine? ReadLine(JsonElement el)
    {
        var name = TryGet(el, NameKeys, out var nameEl) ? AsString(nameEl) : null;
        var itemId = TryGet(el, ItemIdKeys, out var idEl) ? AsString(idEl) : null;
        if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(itemId)) return null;

        var qty = TryGet(el, QtyKeys, out var qtyEl) ? (int?)AsDecimal(qtyEl) : null;
        // A missing quantity means one, not zero — aggregators omit it for single units, and
        // defaulting to zero would silently drop the line at pricing time.
        if (qty is null or < 1) qty = 1;

        decimal? unit = TryGet(el, UnitPriceKeys, out var priceEl) ? AsDecimal(priceEl) : null;
        if (unit is null && TryGet(el, LineTotalKeys, out var totalEl))
        {
            // Only a line total was given, so divide back out — the order builder prices per unit.
            var total = AsDecimal(totalEl);
            if (total is not null) unit = decimal.Round(total.Value / qty.Value, 2);
        }

        return new DynoParsedLine(itemId, name, qty.Value, unit);
    }

    private static bool TryGet(JsonElement obj, string[] keys, out JsonElement value)
    {
        foreach (var key in keys)
        {
            foreach (var prop in obj.EnumerateObject())
            {
                if (!string.Equals(prop.Name, key, StringComparison.OrdinalIgnoreCase)) continue;
                if (prop.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) continue;
                value = prop.Value;
                return true;
            }
        }
        value = default;
        return false;
    }

    private static string? FindString(JsonElement node, string[] keys, int depth)
    {
        if (depth > 4 || node.ValueKind != JsonValueKind.Object) return null;

        if (TryGet(node, keys, out var direct))
        {
            var s = AsString(direct);
            if (!string.IsNullOrWhiteSpace(s)) return s;
        }

        foreach (var prop in node.EnumerateObject())
        {
            if (prop.Value.ValueKind != JsonValueKind.Object) continue;
            var key = prop.Name.ToLowerInvariant();
            if (depth > 0 && !CustomerContainerKeys.Any(key.Contains)) continue;
            var nested = FindString(prop.Value, keys, depth + 1);
            if (!string.IsNullOrWhiteSpace(nested)) return nested;
        }
        return null;
    }

    private static decimal? FindDecimal(JsonElement node, string[] keys, int depth)
    {
        if (depth > 3 || node.ValueKind != JsonValueKind.Object) return null;
        if (TryGet(node, keys, out var direct))
        {
            var d = AsDecimal(direct);
            if (d is not null) return d;
        }
        foreach (var prop in node.EnumerateObject())
        {
            if (prop.Value.ValueKind != JsonValueKind.Object) continue;
            var nested = FindDecimal(prop.Value, keys, depth + 1);
            if (nested is not null) return nested;
        }
        return null;
    }

    /// <summary>Aggregators are inconsistent about quoting numbers ("2" vs 2), so both are read.</summary>
    private static string? AsString(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.String => el.GetString(),
        JsonValueKind.Number => el.ToString(),
        _ => null,
    };

    private static decimal? AsDecimal(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.Number when el.TryGetDecimal(out var d) => d,
        JsonValueKind.String when decimal.TryParse(el.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var s) => s,
        _ => null,
    };
}
