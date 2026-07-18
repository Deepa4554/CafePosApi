using CafePOS.Api.Data;
using CafePOS.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace CafePOS.Api.Infrastructure;

/// <summary>
/// Doc Section 5.7's "expiry sirf read-time pe check hoti hai to ghost ACTIVE rows
/// dashboard ko jhootha dikhati hain" problem — ValidateGuestSessionAttribute already
/// expires a session lazily the next time IT'S read, but a session nobody ever calls back
/// into (customer just walks off) would stay ACTIVE forever without this. Bulk-expires
/// every overdue session on a timer instead. First BackgroundService in this codebase —
/// registered via AddHostedService in Program.cs.
/// </summary>
public class GuestSessionSweepService(IServiceScopeFactory scopeFactory, ILogger<GuestSessionSweepService> logger) : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan InactivityLimit = TimeSpan.FromMinutes(45);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(SweepInterval);
        do
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<CafePosDbContext>();
                var now = DateTime.UtcNow;
                var inactivityCutoff = now - InactivityLimit;
                var overdue = db.GuestSessions.IgnoreQueryFilters()
                    .Where(s => (s.Status == GuestSessionStatus.Active || s.Status == GuestSessionStatus.Locked)
                        && (s.ExpiresAt < now || s.LastActivity < inactivityCutoff));

                int expiredCount;
                if (db.Database.IsRelational())
                {
                    // Single bulk UPDATE, no round trip per row — ExecuteUpdateAsync isn't
                    // supported by the InMemory provider (the dev-only fallback when no
                    // Postgres connection string is configured, see Program.cs), so that
                    // path falls back to a plain load-and-save loop below.
                    expiredCount = await overdue.ExecuteUpdateAsync(setters => setters
                        .SetProperty(s => s.Status, GuestSessionStatus.Expired)
                        .SetProperty(s => s.ClosedReason, SessionCloseReason.Timeout)
                        .SetProperty(s => s.ClosedAt, now), stoppingToken);
                }
                else
                {
                    var sessions = await overdue.ToListAsync(stoppingToken);
                    foreach (var s in sessions)
                    {
                        s.Status = GuestSessionStatus.Expired;
                        s.ClosedReason = SessionCloseReason.Timeout;
                        s.ClosedAt = now;
                    }
                    expiredCount = sessions.Count;
                    if (expiredCount > 0) await db.SaveChangesAsync(stoppingToken);
                }

                if (expiredCount > 0)
                    logger.LogInformation("Guest-session sweep expired {Count} session(s).", expiredCount);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Guest-session sweep failed.");
            }
        } while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken));
    }
}
