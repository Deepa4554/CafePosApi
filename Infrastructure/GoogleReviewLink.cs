using System.Text.RegularExpressions;

namespace CafePOS.Api.Infrastructure;

/// <summary>Turns whatever an Owner pastes into the "Google review link" box into a finished URL
/// a QR can be built from — see CafeSettings.GoogleReviewUrl.
///
/// There is no one link Google hands out. Its business dashboard's "Ask for reviews" button gives
/// a short https://g.page/r/.../review; a Business Profile's share sheet gives a maps.app.goo.gl
/// short link; the API and most guides talk in terms of a Place ID (ChIJ...), which is an opaque
/// id and not a URL at all. An Owner pasting any of those means the same thing, so all of them are
/// accepted and a bare Place ID is expanded into Google's own write-a-review endpoint.
///
/// Deliberately NOT restricted to a list of Google hostnames. A cafe collecting reviews somewhere
/// else — a Zomato page, a TripAdvisor listing, its own feedback form — has the same need and the
/// same QR, and refusing their link would only push them to work around it. What IS enforced is
/// that the result is an http(s) URL: a QR is scanned long after the bill left the counter, so a
/// typo has to fail here, in the settings screen, rather than on a guest's phone.</summary>
public static class GoogleReviewLink
{
    /// <summary>Google's opaque place identifier. Always starts ChIJ/GhIJ/EiQ-style base64-ish
    /// text; matched loosely (no dots, no slashes, no scheme) purely to tell "this is an id, not
    /// a URL" apart, since the exact alphabet isn't documented as a stable contract.</summary>
    private static readonly Regex PlaceIdPattern =
        new(@"^[A-Za-z0-9_-]{15,255}$", RegexOptions.Compiled);

    /// <summary>Normalizes a pasted value, or throws if it's neither a usable URL nor a Place ID.
    /// Returns null for blank input, which is how the setting is cleared.</summary>
    public static string? Normalize(string? raw)
    {
        var value = raw?.Trim();
        if (string.IsNullOrEmpty(value)) return null;

        if (PlaceIdPattern.IsMatch(value))
            return $"https://search.google.com/local/writereview?placeid={Uri.EscapeDataString(value)}";

        // A link copied out of a browser's address bar sometimes loses its scheme on the way
        // through a chat app. Assume https rather than rejecting it — http would be a downgrade
        // nobody asked for, and every review site serves https.
        if (!value.Contains("://")) value = "https://" + value;

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new ApiValidationException(
                "Paste the review link from your Google Business Profile (it looks like https://g.page/r/.../review), or just your Place ID.");

        return uri.ToString();
    }
}
