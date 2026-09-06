using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace CafePOS.Api.Infrastructure;

/// <summary>How many round trips to Postgres the CURRENT request has made, and how long they
/// took in total. Scoped, so each request gets its own.
///
/// Exists because "this endpoint feels slow" could not be answered without guessing. On this
/// deployment a sequential query costs ~60ms (app to database and back), so the query COUNT is
/// what an endpoint's latency is actually made of — and counting them by reading the code
/// misses everything SaveChanges, the audit trail and the interceptors do on their own. Reading
/// it off the response beats attaching a profiler to production.</summary>
public sealed class DbQueryStats
{
    private int _count;
    private long _elapsedTicks;

    public int Count => _count;
    public TimeSpan Elapsed => TimeSpan.FromTicks(Interlocked.Read(ref _elapsedTicks));

    public void Record(TimeSpan duration)
    {
        Interlocked.Increment(ref _count);
        Interlocked.Add(ref _elapsedTicks, duration.Ticks);
    }
}

/// <summary>Feeds <see cref="DbQueryStats"/> from EF itself, so it counts every command the
/// provider actually issues — including the ones no application code spells out.</summary>
public sealed class DbQueryCountingInterceptor(IServiceProvider services) : DbCommandInterceptor
{
    // Resolved per callback rather than injected: the interceptor is registered once for the
    // whole app, while the stats object it writes to belongs to one request.
    private DbQueryStats? Current => services.GetService<IHttpContextAccessor>()?.HttpContext
        ?.RequestServices.GetService<DbQueryStats>();

    public override DbDataReader ReaderExecuted(DbCommand command, CommandExecutedEventData eventData, DbDataReader result)
    {
        Current?.Record(eventData.Duration);
        return base.ReaderExecuted(command, eventData, result);
    }

    public override ValueTask<DbDataReader> ReaderExecutedAsync(
        DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
    {
        Current?.Record(eventData.Duration);
        return base.ReaderExecutedAsync(command, eventData, result, cancellationToken);
    }

    public override object? ScalarExecuted(DbCommand command, CommandExecutedEventData eventData, object? result)
    {
        Current?.Record(eventData.Duration);
        return base.ScalarExecuted(command, eventData, result);
    }

    public override ValueTask<object?> ScalarExecutedAsync(
        DbCommand command, CommandExecutedEventData eventData, object? result, CancellationToken cancellationToken = default)
    {
        Current?.Record(eventData.Duration);
        return base.ScalarExecutedAsync(command, eventData, result, cancellationToken);
    }

    public override int NonQueryExecuted(DbCommand command, CommandExecutedEventData eventData, int result)
    {
        Current?.Record(eventData.Duration);
        return base.NonQueryExecuted(command, eventData, result);
    }

    public override ValueTask<int> NonQueryExecutedAsync(
        DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        Current?.Record(eventData.Duration);
        return base.NonQueryExecutedAsync(command, eventData, result, cancellationToken);
    }
}
