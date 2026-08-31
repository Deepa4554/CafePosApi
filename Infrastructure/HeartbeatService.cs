using Microsoft.AspNetCore.SignalR;

namespace CafePOS.Api.Infrastructure;

/// <summary>
/// Broadcasts a no-payload "heartbeat" event to every connected OrdersHub client on a fixed
/// tick, whether or not anything actually changed. Exists purely so the client can tell "the
/// socket is delivering messages" apart from "the socket's HubConnectionState says Connected"
/// — the two are NOT the same thing. useOrders.ts's own history has an example: on Vercel's
/// web deployment the connection reported Connected while messages were silently not arriving,
/// and every client fell back to being carried entirely by the safety-net refetchInterval
/// without anyone — client or server — ever finding out from the connection state itself.
///
/// The client (see ordersRealtime.ts) only trusts the socket enough to slow its polling down
/// when a message — this heartbeat, or any real push — has actually landed recently. No
/// heartbeat ever received (old app build predating this, or a socket that LOOKS connected but
/// isn't) means the client's liveness check reports "not proven alive", which keeps it on the
/// original fast interval. That's the safe direction to fail in.
///
/// Broadcast to Clients.All rather than per-tenant: a heartbeat carries no tenant-specific
/// data, so there's nothing gained by looking up group membership for it, only cost.
/// </summary>
public class HeartbeatService(IHubContext<OrdersHub> hub, ILogger<HeartbeatService> logger) : BackgroundService
{
    // Well under the client's 25s liveness window (see socketLiveness.ts) so a single missed
    // tick — GC pause, a slow broadcast, one dropped frame — doesn't by itself flip a client
    // back to fast polling; it takes roughly two misses in a row before the window lapses.
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                await hub.Clients.All.SendAsync("heartbeat", cancellationToken: stoppingToken);
            }
            catch (Exception ex)
            {
                // Same fire-and-forget stance as RealtimeNotifier: a broadcast failing here
                // must never crash the service loop. A missed heartbeat just means clients
                // fall back to fast polling a little sooner than strictly necessary — the
                // safe direction — not that anything breaks.
                logger.LogWarning(ex, "Heartbeat broadcast failed.");
            }
        } while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken));
    }
}
