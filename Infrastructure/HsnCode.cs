namespace CafePOS.Api.Infrastructure;

/// <summary>Validates the HSN/SAC code an invoice line is billed under — the one place the rule
/// lives, so the cafe-wide default (SettingsController) and a per-item override (MenuController)
/// can't accept different things and leave an invoice quoting a code the return won't take.
///
/// A restaurant serving food for consumption on the premises bills the whole menu under one SAC
/// (996331), which is why the usual case is a single default and a mostly-empty per-item column;
/// per-item codes matter to the cafes that also sell packaged goods across the counter.</summary>
public static class HsnCode
{
    /// <summary>Trims and validates, mapping blank to null — "no code" is one value rather than
    /// two, so callers can drop the invoice's HSN column on a plain null check.
    ///
    /// Digits only, 4 to 8 of them: HSN and SAC are both numeric and are quoted at 4, 6 or 8
    /// digits depending on turnover. Anything shorter is a typo rather than a valid code, and
    /// rejecting it here beats printing it on an invoice that then has to be revised.</summary>
    public static string? Normalize(string? raw, string fieldLabel)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var code = raw.Trim();
        if (!code.All(char.IsAsciiDigit))
            throw new ApiValidationException($"{fieldLabel} must be digits only — HSN and SAC codes are numeric.");
        if (code.Length is < 4 or > 8)
            throw new ApiValidationException($"{fieldLabel} must be between 4 and 8 digits.");
        return code;
    }
}
