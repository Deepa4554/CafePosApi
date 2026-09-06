using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace CafePOS.Api.Infrastructure;

/// <summary>How many round trips to Postgres the CURRENT request has made, and how long they
/// took in total.
///
/// Exists because "this endpoint feels slow" could not be answered without guessing. On this
/// deployment a sequential query costs ~60ms (app to database and back), so the query COUNT is
/// what an endpoint's latency is actually made of — and counting them by reading the code misses
/// everything SaveChanges, the audit trail and the interceptors do on their own.
///
/// Carried in an AsyncLocal rather than as a scoped DI service. The DI version of this counted
/// nothing: the interceptor reached for the stats through IHttpContextAccessor while the
/// middleware read them straight off HttpContext.RequestServices, and the two did not resolve to
/// the same object, so every response reported zero. An AsyncLocal set at the top of the request
/// flows into every await the request makes — EF's command execution included — so the object the
/// interceptor writes to is by construction the one the middleware reads.</summary>
public sealed class DbQueryStats
{
    private static readonly AsyncLocal<DbQueryStats?> Ambient = new();

    /// <summary>The tally for the request running on this async flow, or null outside one
    /// (background jobs, startup migrations — deliberately not counted).</summary>
    public static DbQueryStats? Current => Ambient.Value;

    /// <summary>Starts a fresh tally for this request. Called once, by the middleware.</summary>
    public static DbQueryStats BeginRequest()
    {
        var stats = new DbQueryStats();
        Ambient.Value = stats;
        return stats;
    }

    private int _count;
    private long _elapsedTicks;

    public int Count => Volatile.Read(ref _count);
    public TimeSpan Elapsed => TimeSpan.FromTicks(Interlocked.Read(ref _elapsedTicks));

    public void Record(TimeSpan duration)
    {
        Interlocked.Increment(ref _count);
        Interlocked.Add(ref _elapsedTicks, duration.Ticks);
    }
}

/// <summary>Feeds <see cref="DbQueryStats"/> from EF itself, so it counts every command the
/// provider actually issues — including the ones no application code spells out. Stateless, so
/// it is registered once and shared.</summary>
public sealed class DbQueryCountingInterceptor : DbCommandInterceptor
{
    public override DbDataReader ReaderExecuted(DbCommand command, CommandExecutedEventData eventData, DbDataReader result)
    {
        DbQueryStats.Current?.Record(eventData.Duration);
        return base.ReaderExecuted(command, eventData, result);
    }

    public override ValueTask<DbDataReader> ReaderExecutedAsync(
        DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
    {
        DbQueryStats.Current?.Record(eventData.Duration);
        return base.ReaderExecutedAsync(command, eventData, result, cancellationToken);
    }

    public override object? ScalarExecuted(DbCommand command, CommandExecutedEventData eventData, object? result)
    {
        DbQueryStats.Current?.Record(eventData.Duration);
        return base.ScalarExecuted(command, eventData, result);
    }

    public override ValueTask<object?> ScalarExecutedAsync(
        DbCommand command, CommandExecutedEventData eventData, object? result, CancellationToken cancellationToken = default)
    {
        DbQueryStats.Current?.Record(eventData.Duration);
        return base.ScalarExecutedAsync(command, eventData, result, cancellationToken);
    }

    public override int NonQueryExecuted(DbCommand command, CommandExecutedEventData eventData, int result)
    {
        DbQueryStats.Current?.Record(eventData.Duration);
        return base.NonQueryExecuted(command, eventData, result);
    }

    public override ValueTask<int> NonQueryExecutedAsync(
        DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        DbQueryStats.Current?.Record(eventData.Duration);
        return base.NonQueryExecutedAsync(command, eventData, result, cancellationToken);
    }
}
