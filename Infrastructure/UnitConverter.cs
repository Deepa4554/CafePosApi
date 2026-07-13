namespace CafePOS.Api.Infrastructure;

/// <summary>
/// Converts a recipe/purchase quantity expressed in one unit into the equivalent amount
/// in an ingredient's stored unit. Only three families exist — weight, volume, count —
/// and units never convert across families (e.g. "gm" can never become "ml").
/// </summary>
public static class UnitConverter
{
    private enum Family { Weight, Volume, Count }

    private static Family? FamilyOf(string unit) => unit.Trim().ToLowerInvariant() switch
    {
        "gm" or "g" or "kg" => Family.Weight,
        "ml" or "litre" or "liter" or "l" => Family.Volume,
        "pcs" or "pc" or "unit" or "units" => Family.Count,
        _ => null,
    };

    /// <summary>Grams-per-unit (weight) or ml-per-unit (volume) — 1 for count units.</summary>
    private static double BaseFactor(string unit) => unit.Trim().ToLowerInvariant() switch
    {
        "gm" or "g" or "ml" => 1,
        "kg" or "litre" or "liter" or "l" => 1000,
        _ => 1,
    };

    public static bool AreCompatible(string unitA, string unitB)
    {
        var famA = FamilyOf(unitA);
        var famB = FamilyOf(unitB);
        return famA is not null && famA == famB;
    }

    /// <summary>Converts <paramref name="quantity"/> expressed in <paramref name="fromUnit"/>
    /// into the equivalent amount in <paramref name="toUnit"/>. Throws if the two units
    /// belong to different families (e.g. weight vs volume) — that's a data-entry error,
    /// not something to silently coerce.</summary>
    public static double Convert(double quantity, string fromUnit, string toUnit)
    {
        if (!AreCompatible(fromUnit, toUnit))
            throw new ApiValidationException($"Cannot convert '{fromUnit}' to '{toUnit}' — incompatible unit types.");

        return quantity * BaseFactor(fromUnit) / BaseFactor(toUnit);
    }
}
