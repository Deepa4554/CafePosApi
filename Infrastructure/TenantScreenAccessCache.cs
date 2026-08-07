using CafePOS.Api.Domain;
using Microsoft.Extensions.Caching.Memory;

namespace CafePOS.Api.Infrastructure;

/// <summary>What every screen-gate needs to know about a tenant's platform-admin-curated
/// screen access, and nothing else — same "snapshot, not the entity" reasoning as
/// SubscriptionSnapshot. PlanDefault (the struct's default) means "no tenant-level
/// restriction beyond the plan itself", which is also what a missing/deleted tenant
/// resolves to — the safe direction to fail in.</summary>
public readonly record struct TenantScreenSnapshot(TenantScreenMode Mode, List<string>? EnabledScreens);

/// <summary>
/// ScreenAccessFilter runs on every [RequireScreen] endpoint and AuthController's
/// /auth/me is polled every few seconds by every open session (see useLiveAccessSync) —
/// both would otherwise be a per-request round trip to Postgres for a row that changes
/// only when a platform admin edits one cafe's Screen Access tab. Same shape and TTL
/// reasoning as ISubscriptionCache.
/// </summary>
public interface ITenantScreenAccessCache
{
    Task<TenantScreenSnapshot> GetAsync(int tenantId, Func<Task<TenantScreenSnapshot>> loadFromDb);
    void Invalidate(int tenantId);
}

public class TenantScreenAccessCache(IMemoryCache cache) : ITenantScreenAccessCache
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(60);
    private static string Key(int tenantId) => $"tenant-screen-access:{tenantId}";

    public async Task<TenantScreenSnapshot> GetAsync(int tenantId, Func<Task<TenantScreenSnapshot>> loadFromDb)
    {
        if (cache.TryGetValue<TenantScreenSnapshot>(Key(tenantId), out var cached)) return cached;
        var value = await loadFromDb();
        cache.Set(Key(tenantId), value, Ttl);
        return value;
    }

    public void Invalidate(int tenantId) => cache.Remove(Key(tenantId));
}
