using CafePOS.Api.Contracts;
using CafePOS.Api.Data;
using CafePOS.Api.Domain;
using CafePOS.Api.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CafePOS.Api.Controllers;

/// <summary>
/// The cafe's udhaar book. A bill settled with the "Due" tender (OrdersController.Pay) closes
/// on the spot and drops the uncollected amount here as a KhataEntry against the customer;
/// everything in this controller is the other half of that — reading who owes what, and taking
/// the money back in whenever they actually pay, in as many instalments as it takes.
///
/// Owner/Manager only. Extending credit is a floor decision any cashier settling a bill can
/// make, but deciding a customer's debt has been paid off is not: a waiter who could record
/// repayments could clear a khata with no money ever having changed hands, and the ledger is
/// the cafe's only record that the debt existed.
///
/// Normal plan on purpose, unlike the CRM screens it shares a Customer record with. Running a
/// khata for regulars is what the smallest, cheapest-plan shops do most, not least — see
/// ScreenCatalog.
/// </summary>
[ApiController]
[Route("api/khatabook")]
[Authorize(Policy = Policies.OwnerOrManager)]
[RequireScreen("Khatabook")]
public class KhatabookController(CafePosDbContext db) : ControllerBase
{
    /// <summary>The tenders a repayment can actually arrive as — the payment methods minus
    /// "Due" itself. Kept separate from OrdersController's set rather than filtered out of it,
    /// so that adding a tender there is a deliberate decision here too.</summary>
    private static readonly HashSet<string> ValidSettleMethods =
        new(StringComparer.OrdinalIgnoreCase) { "Cash", "Card", "UPI" };

    // Money is stored as decimal, but the balance is a difference of two sums — compare against
    // a paisa-sized epsilon rather than 0 so a khata that's been settled to the last rupee can
    // never linger in the list on a rounding tail.
    private const decimal Epsilon = 0.005m;

    /// <summary>
    /// Who owes money, most owed first. `search` matches name or mobile. `includeSettled`
    /// brings back customers whose khata is square — off by default, because the list's job is
    /// "who do I need to chase", and a cafe that's run credit for two years would otherwise
    /// open this screen to a wall of zeroes.
    ///
    /// Grouped in memory rather than as a SQL GROUP BY. Every khata line a cafe will ever write
    /// is a handful of rows per credit customer, so this is two flat reads either way — and the
    /// SQL version has to project into a shape you then filter and sort by a *computed* balance
    /// (due minus paid), which is exactly the sort of expression EF declines to translate. Same
    /// load-then-aggregate shape ExpensesController.List uses, for the same reason.
    /// </summary>
    [HttpGet]
    public async Task<KhatabookDto> List([FromQuery] string? search = null, [FromQuery] bool includeSettled = false)
    {
        var rows = await db.KhataEntries
            .Select(e => new { e.CustomerId, e.Type, e.Amount, e.CreatedAt })
            .ToListAsync();

        var byCustomer = rows.GroupBy(r => r.CustomerId).ToDictionary(g => g.Key, g =>
        {
            var due = g.Where(r => r.Type == KhataEntryType.Due).Sum(r => r.Amount);
            var paid = g.Where(r => r.Type == KhataEntryType.Payment).Sum(r => r.Amount);
            return new
            {
                Due = due,
                Paid = paid,
                Outstanding = due - paid,
                Count = g.Count(),
                LastActivityAt = g.Max(r => r.CreatedAt),
                LastDueAt = g.Where(r => r.Type == KhataEntryType.Due).Select(r => (DateTime?)r.CreatedAt).Max(),
            };
        });

        // Only the customers who actually have a khata — a cafe's CRM can hold thousands of
        // records, and all but the credit ones are irrelevant here.
        var customerIds = byCustomer.Keys.ToList();
        var customers = await db.Customers
            .Where(c => customerIds.Contains(c.Id))
            .Select(c => new { c.Id, c.Name, c.Phone, c.ProfilePhotoUrl })
            .ToListAsync();

        var term = search?.Trim();
        var list = customers
            .Select(c =>
            {
                var t = byCustomer[c.Id];
                return new KhataCustomerDto(c.Id, c.Name, c.Phone, c.ProfilePhotoUrl,
                    t.Outstanding, t.Due, t.Paid, t.Count, t.LastActivityAt, t.LastDueAt);
            })
            .Where(k => includeSettled || k.Outstanding > Epsilon)
            .Where(k => string.IsNullOrEmpty(term)
                || k.Name.Contains(term, StringComparison.OrdinalIgnoreCase)
                || (k.Phone is not null && k.Phone.Contains(term)))
            // Most owed first; ties broken by whoever's khata has sat untouched longest, which
            // is the one worth chasing between two people owing the same.
            .OrderByDescending(k => k.Outstanding)
            .ThenBy(k => k.LastActivityAt)
            .ToList();

        // Summary totals ignore `search` and `includeSettled` deliberately — they're the cafe's
        // whole position, which shouldn't move because someone typed a name into the filter box.
        // "This month" is the cafe's month, not UTC's: on a UTC boundary the month would turn
        // over at 5:30 AM IST and the 1st's early collections would land in the previous one.
        var istNow = IstClock.NowIst;
        var monthStartUtc = IstClock.IstDateStartUtc(new DateOnly(istNow.Year, istNow.Month, 1));
        var collectedThisMonth = rows
            .Where(r => r.Type == KhataEntryType.Payment && r.CreatedAt >= monthStartUtc)
            .Sum(r => r.Amount);
        var owing = byCustomer.Values.Select(t => t.Outstanding).Where(o => o > Epsilon).ToList();

        return new KhatabookDto(new KhataSummaryDto(owing.Sum(), owing.Count, collectedThisMonth), list);
    }

    /// <summary>One customer's full ledger, newest first, each row carrying the balance as it
    /// stood right after it — see KhataEntryDto.RunningOutstanding.</summary>
    [HttpGet("{customerId:int}")]
    public async Task<ActionResult<KhataDetailDto>> Get(int customerId)
    {
        var customer = await db.Customers.FirstOrDefaultAsync(c => c.Id == customerId);
        if (customer is null) return NotFound();

        // Oldest first here so the running balance can be accumulated in one forward pass;
        // reversed at the end, since the screen reads newest at the top.
        var entries = await db.KhataEntries
            .Where(e => e.CustomerId == customerId)
            .OrderBy(e => e.CreatedAt).ThenBy(e => e.Id)
            .ToListAsync();

        var running = 0m;
        var rows = entries.Select(e =>
        {
            running += e.Type == KhataEntryType.Due ? e.Amount : -e.Amount;
            return KhataEntryDto.From(e, running);
        }).ToList();
        rows.Reverse();

        var due = entries.Where(e => e.Type == KhataEntryType.Due).Sum(e => e.Amount);
        var paid = entries.Where(e => e.Type == KhataEntryType.Payment).Sum(e => e.Amount);
        var summary = new KhataCustomerDto(
            customer.Id, customer.Name, customer.Phone, customer.ProfilePhotoUrl,
            due - paid, due, paid, entries.Count,
            entries.Count > 0 ? entries[^1].CreatedAt : customer.JoinedAt,
            entries.LastOrDefault(e => e.Type == KhataEntryType.Due)?.CreatedAt);

        return new KhataDetailDto(summary, rows);
    }

    /// <summary>
    /// Records money collected against a khata — the whole outstanding balance or any part of
    /// it, as many times as the customer comes back. Overpayment is rejected rather than left
    /// as a credit balance: a khata that can go negative is a store-account, which is a
    /// different (and much larger) feature than "who owes us".
    /// </summary>
    [HttpPost("{customerId:int}/settle")]
    public Task<ActionResult<KhataDetailDto>> Settle(int customerId, SettleKhataRequest req) =>
        // Two staff both recording "₹500 received" on the same khata in the same moment would
        // otherwise both read the same outstanding balance, both pass the over-payment check,
        // and both write a row — clearing a debt for half the money. Same serialisation
        // OrdersController.Pay uses, for exactly the same reason.
        DbConcurrency.InTransactionAsync<ActionResult<KhataDetailDto>>(db, async () =>
    {
        if (req.Amount <= 0)
            throw new ApiValidationException("Enter an amount greater than zero.");
        if (string.IsNullOrWhiteSpace(req.Method) || !ValidSettleMethods.Contains(req.Method.Trim()))
            throw new ApiValidationException($"'{req.Method}' isn't a valid payment method — a khata is settled with Cash, Card or UPI.");

        // Lock before the read, not after — the point is for a second device's Settle to wait
        // here and then re-read the balance this one actually committed. The customer row
        // stands in for the khata as a whole (there's no single ledger row to lock).
        await DbConcurrency.LockRowsAsync<Customer>(db, customerId);
        var customer = await db.Customers.FirstOrDefaultAsync(c => c.Id == customerId);
        if (customer is null) return NotFound();

        var entries = await db.KhataEntries.Where(e => e.CustomerId == customerId).ToListAsync();
        var outstanding = entries.Sum(e => e.Type == KhataEntryType.Due ? e.Amount : -e.Amount);

        if (outstanding <= Epsilon)
            throw new ApiConflictException($"{customer.Name}'s khata is already clear — nothing left to settle.");
        if (req.Amount - outstanding > Epsilon)
            throw new ApiValidationException($"₹{req.Amount:0.00} is more than the ₹{outstanding:0.00} outstanding on this khata.");

        db.KhataEntries.Add(new KhataEntry
        {
            CustomerId = customerId,
            Type = KhataEntryType.Payment,
            Amount = req.Amount,
            Method = ValidSettleMethods.TryGetValue(req.Method.Trim(), out var canonical) ? canonical : req.Method.Trim(),
            Note = string.IsNullOrWhiteSpace(req.Note) ? null : req.Note.Trim(),
            RecordedByUserId = CurrentUserId(),
            RecordedByName = User.Identity?.Name ?? "Cafe Staff",
        });
        await db.SaveChangesAsync();

        return await Get(customerId);
    });

    private int? CurrentUserId()
    {
        var claim = User.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value;
        return int.TryParse(claim, out var id) ? id : null;
    }
}
