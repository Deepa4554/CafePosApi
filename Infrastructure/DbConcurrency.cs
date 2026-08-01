using CafePOS.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace CafePOS.Api.Infrastructure;

/// <summary>
/// The read-modify-write sequences that money and stock depend on — settle a bill, redeem a
/// gift card, deduct an ingredient, claim a table — are all "read a value in SQL, decide in
/// C#, write it back". Two devices doing that to the same row at the same time (two waiters
/// on one table during a rush, a double-tapped Pay button on slow wifi) both read the same
/// starting value and the later save silently overwrites the earlier one: a bill paid twice,
/// a gift card spent twice, stock that never actually came off the shelf.
///
/// This is the one place that's fixed. <see cref="InTransactionAsync"/> opens a transaction
/// and <see cref="LockRowsAsync{TEntity}"/> takes a Postgres row-level lock (SELECT … FOR
/// UPDATE) on the specific rows about to be mutated, so the second writer blocks until the
/// first has committed and then re-reads the value the first one actually wrote. Same
/// approach WhatsAppInternalController already uses for queue claiming, just applied to the
/// staff-side billing/inventory paths too.
///
/// Both no-op against the in-memory provider (the zero-config local-dev fallback in
/// Program.cs) — it is single-process and has no SQL to run, so the race can't happen there,
/// exactly like NextTokenNumberAsync's UPSERT fallback.
/// </summary>
public static class DbConcurrency
{
    /// <summary>Runs <paramref name="work"/> inside one database transaction, routed through
    /// EF's execution strategy so it composes with EnableRetryOnFailure (see Program.cs —
    /// without this, opening a transaction on a retrying strategy throws outright).
    ///
    /// <paramref name="work"/> MUST load everything it touches itself: a retried attempt
    /// clears the change tracker first, so anything the caller loaded before this call is
    /// detached and stale by then. The first attempt never clears, so a straight-through
    /// run behaves exactly as it did before.</summary>
    public static async Task<T> InTransactionAsync<T>(CafePosDbContext db, Func<Task<T>> work)
    {
        if (!db.Database.IsRelational()) return await work();

        // Re-entrant: a transaction is already open on this context, so join it rather than
        // trying to open a second one (which EF rejects outright). This is a real path, not
        // just defensiveness — GuestSessionController locks the session and then calls
        // BuildOrderAsync, which wants a transaction of its own for the table claim. The
        // outermost caller owns the commit, which is what "all of this or none of it" needs
        // to mean anyway.
        if (db.Database.CurrentTransaction is not null) return await work();

        var attempt = 0;
        return await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            if (attempt++ > 0) db.ChangeTracker.Clear();
            await using var txn = await db.Database.BeginTransactionAsync();
            var result = await work();
            await txn.CommitAsync();
            return result;
        });
    }

    /// <inheritdoc cref="InTransactionAsync{T}"/>
    public static Task InTransactionAsync(CafePosDbContext db, Func<Task> work) =>
        InTransactionAsync(db, async () => { await work(); return 0; });

    /// <summary>Takes an exclusive row lock on the given rows of <typeparamref name="TEntity"/>'s
    /// table, held until the surrounding transaction commits or rolls back. Call this BEFORE
    /// loading the entity, so the load reflects whatever a competing writer already committed.
    ///
    /// Rows are locked in ascending Id order, which gives every caller in the process one
    /// global lock ordering — two requests whose id sets overlap queue up behind each other
    /// instead of deadlocking half-way through.
    ///
    /// Must run inside a transaction (see <see cref="InTransactionAsync"/>); outside one
    /// Postgres commits the implicit single-statement transaction immediately and the lock
    /// is gone before the caller can use it.</summary>
    public static async Task LockRowsAsync<TEntity>(CafePosDbContext db, params int[] ids) where TEntity : class
    {
        if (!db.Database.IsRelational() || ids.Length == 0) return;

        var table = db.Model.FindEntityType(typeof(TEntity))?.GetTableName()
            ?? throw new InvalidOperationException($"{typeof(TEntity).Name} is not a mapped entity type.");
        var ordered = ids.Where(id => id != 0).Distinct().OrderBy(id => id).ToArray();
        if (ordered.Length == 0) return;

        // Table name comes from the EF model, never from user input; every id goes through
        // its own real parameter. Nothing here is string-concatenated from a request.
        // One placeholder per id rather than a single array parameter — plainest possible
        // SQL, on a statement that most of the billing and stock paths now depend on.
        var placeholders = string.Join(", ", ordered.Select((_, i) => $"{{{i}}}"));
        // EF1002 warns that ExecuteSqlRawAsync doesn't protect an interpolated string from
        // injection. Suppressed deliberately: the only two interpolated values are the table
        // name, read out of the EF model above, and a run of "{0}, {1}, …" placeholders
        // generated from a count. Every id itself is passed as a parameter, never inlined —
        // which is exactly why this can't be the ExecuteSqlAsync the analyzer suggests, as
        // that has no way to expand a variable-length IN list.
#pragma warning disable EF1002
        await db.Database.ExecuteSqlRawAsync(
            $"SELECT 1 FROM \"{table}\" WHERE \"Id\" IN ({placeholders}) ORDER BY \"Id\" FOR UPDATE",
            ordered.Select(id => (object)id).ToArray());
#pragma warning restore EF1002
    }
}
