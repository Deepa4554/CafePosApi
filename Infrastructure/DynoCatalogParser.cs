using System.Globalization;
using System.Text.Json;

namespace CafePOS.Api.Infrastructure;

public record DynoCatalogItem(string Id, string Name, bool IsCategory, decimal? Price);

/// <summary>
/// Reads an aggregator's full menu dump (the reply to a GetAllItems request) into flat catalog
/// rows the mapping screen can list.
///
/// Same caveat as <see cref="DynoOrderParser"/>: Dyno declares the payload untyped, so the real
/// shape comes from Zomato/Swiggy and isn't specified anywhere. This walks the document for
/// objects that carry an id and a name, treating anything reached through a categories/menu
/// container as a category and everything else as an item. A miss returns an empty list, which
/// the caller logs and leaves the existing catalog untouched — never a throw, since this runs on
/// a background poll nobody is watching.
/// </summary>
public static class DynoCatalogParser
{
    private static readonly string[] IdKeys = ["item_id", "itemId", "catalogue_id", "catalogueId", "id"];
    private static readonly string[] NameKeys = ["item_name", "itemName", "title", "name"];
    private static readonly string[] PriceKeys = ["price", "base_price", "basePrice", "cost", "default_price", "defaultPrice"];
    private static readonly string[] CategoryContainerKeys = ["categor", "section", "menu_group", "group"];
    private static readonly string[] ItemContainerKeys = ["item", "dish", "product"];

    public static List<DynoCatalogItem> Parse(JsonElement root)
    {
        var found = new Dictionary<(string, bool), DynoCatalogItem>();
        Walk(root, inCategoryContainer: false, depth: 0, found);
        return [.. found.Values];
    }

    private static void Walk(JsonElement node, bool inCategoryContainer, int depth, Dictionary<(string, bool), DynoCatalogItem> found)
    {
        if (depth > 8) return;

        switch (node.ValueKind)
        {
            case JsonValueKind.Array:
                foreach (var el in node.EnumerateArray())
                    Walk(el, inCategoryContainer, depth + 1, found);
                return;

            case JsonValueKind.Object:
                // An object carrying both an id and a name is a catalog entry in its own right.
                // Categories usually also carry a nested item array, so record it and keep
                // descending rather than stopping here.
                if (TryGet(node, IdKeys, out var idEl) && TryGet(node, NameKeys, out var nameEl))
                {
                    var id = AsString(idEl);
                    var name = AsString(nameEl);
                    if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(name))
                    {
                        var price = TryGet(node, PriceKeys, out var priceEl) ? AsDecimal(priceEl) : null;
                        // A node that itself contains a list of items is the section, not a dish.
                        var isCategory = inCategoryContainer || HasChildItemArray(node);
                        found[(id, isCategory)] = new DynoCatalogItem(id, name, isCategory, price);
                    }
                }

                foreach (var prop in node.EnumerateObject())
                {
                    var key = prop.Name.ToLowerInvariant();
                    var childInCategory =
                        CategoryContainerKeys.Any(key.Contains) ? true :
                        ItemContainerKeys.Any(key.Contains) ? false :
                        inCategoryContainer;
                    Walk(prop.Value, childInCategory, depth + 1, found);
                }
                return;
        }
    }

    private static bool HasChildItemArray(JsonElement obj)
    {
        foreach (var prop in obj.EnumerateObject())
        {
            if (prop.Value.ValueKind != JsonValueKind.Array) continue;
            var key = prop.Name.ToLowerInvariant();
            if (ItemContainerKeys.Any(key.Contains)) return true;
        }
        return false;
    }

    private static bool TryGet(JsonElement obj, string[] keys, out JsonElement value)
    {
        foreach (var key in keys)
        {
            foreach (var prop in obj.EnumerateObject())
            {
                if (!string.Equals(prop.Name, key, StringComparison.OrdinalIgnoreCase)) continue;
                if (prop.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) continue;
                if (prop.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array) continue;
                value = prop.Value;
                return true;
            }
        }
        value = default;
        return false;
    }

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
