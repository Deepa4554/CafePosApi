using Microsoft.Extensions.Caching.Memory;

namespace CafePOS.Api.Infrastructure;

/// <summary>
/// Order creation is on the hottest path in the app (every "Fire to Kitchen") and, until
/// this cache existed, paid a full round trip to the remote Postgres instance just to read
/// one number that almost never changes: the tenant's tax rate. A short TTL (not
/// "forever") keeps a change from SettingsController.Update visible within seconds even if
/// the explicit Invalidate call below is ever missed on some future edit path.
/// </summary>
public interface ITaxRateCache
{
    Task<decimal> GetTaxRatePctAsync(int tenantId, Func<Task<decimal>> loadFromDb);
    void Invalidate(int tenantId);
}

public class TaxRateCache(IMemoryCache cache) : ITaxRateCache
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);
    private static string Key(int tenantId) => $"tax-rate-pct:{tenantId}";

    public async Task<decimal> GetTaxRatePctAsync(int tenantId, Func<Task<decimal>> loadFromDb)
    {
        if (cache.TryGetValue<decimal>(Key(tenantId), out var cached)) return cached;
        var value = await loadFromDb();
        cache.Set(Key(tenantId), value, Ttl);
        return value;
    }

    public void Invalidate(int tenantId) => cache.Remove(Key(tenantId));
}
