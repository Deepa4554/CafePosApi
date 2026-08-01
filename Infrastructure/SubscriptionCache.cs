using CafePOS.Api.Domain;
using Microsoft.Extensions.Caching.Memory;

namespace CafePOS.Api.Infrastructure;

/// <summary>What the two plan gates need to know about a tenant's subscription, and nothing
/// else — deliberately a snapshot rather than the entity, so nothing can accidentally write
/// through a cached instance.</summary>
public readonly record struct SubscriptionSnapshot(SubscriptionTier Plan, DateTime? PlanExpiresAt);

/// <summary>
/// SubscriptionExpiryMiddleware and RequirePlanHandler both run on nearly every
/// authenticated request, and each was doing its own uncached round trip to a remote
/// Postgres for a row that changes at most once a billing cycle — two extra queries on the
/// path of every order fire, every KDS poll, every dashboard refresh, for every tenant.
///
/// Same shape and reasoning as ITaxRateCache. The TTL is short rather than "until
/// invalidated" so a plan change made anywhere (SuperAdmin, a webhook, straight SQL) takes
/// effect on its own within a minute even if the explicit Invalidate below is ever missed —
/// which also bounds how long an expired plan can keep working to that same minute.
/// </summary>
public interface ISubscriptionCache
{
    Task<SubscriptionSnapshot> GetAsync(int tenantId, Func<Task<SubscriptionSnapshot>> loadFromDb);
    void Invalidate(int tenantId);
}

public class SubscriptionCache(IMemoryCache cache) : ISubscriptionCache
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(60);
    private static string Key(int tenantId) => $"subscription:{tenantId}";

    public async Task<SubscriptionSnapshot> GetAsync(int tenantId, Func<Task<SubscriptionSnapshot>> loadFromDb)
    {
        if (cache.TryGetValue<SubscriptionSnapshot>(Key(tenantId), out var cached)) return cached;
        var value = await loadFromDb();
        cache.Set(Key(tenantId), value, Ttl);
        return value;
    }

    public void Invalidate(int tenantId) => cache.Remove(Key(tenantId));
}
