using Microsoft.Extensions.Caching.Memory;

namespace CafePOS.Api.Infrastructure;

/// <summary>
/// GET /settings/logo/thermal sits in the request path of every ESC/POS bill (WiFi, Web
/// Bluetooth) and, until this cache existed, paid a full fetch of the source image PLUS a
/// Floyd–Steinberg dither (see ThermalLogoRasterizer) on every single receipt — for a logo
/// that changes maybe once a year. Same short-TTL shape as TaxRateCache: long enough to
/// erase the cost from the hot path, short enough that a stuck cache is never the story if
/// something looks wrong.
/// </summary>
public interface IThermalLogoCache
{
    Task<byte[]?> GetAsync(int tenantId, int columns, Func<Task<byte[]?>> loadFromSource);
    void Invalidate(int tenantId);
}

public class ThermalLogoCache(IMemoryCache cache) : IThermalLogoCache
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(10);
    // Both supported paper widths (32/48 columns, see GetThermalLogo) share one cache keyed
    // by tenant — Invalidate below has no columns to scope to, so caching by tenant+columns
    // means a logo change clears both entries's *next* miss individually rather than needing
    // to know which widths exist.
    private static string Key(int tenantId, int columns) => $"thermal-logo:{tenantId}:{columns}";

    public async Task<byte[]?> GetAsync(int tenantId, int columns, Func<Task<byte[]?>> loadFromSource)
    {
        if (cache.TryGetValue<byte[]?>(Key(tenantId, columns), out var cached)) return cached;
        var value = await loadFromSource();
        cache.Set(Key(tenantId, columns), value, Ttl);
        return value;
    }

    // Only the two widths the app actually asks for (see GetThermalLogo) — a wider cache-wipe
    // API would need the same "which columns exist" knowledge this is trying to avoid.
    public void Invalidate(int tenantId)
    {
        cache.Remove(Key(tenantId, 32));
        cache.Remove(Key(tenantId, 48));
    }
}
