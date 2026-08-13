using System.Globalization;
using CafePOS.Api.Contracts;
using CafePOS.Api.Data;
using CafePOS.Api.Domain;
using CafePOS.Api.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CafePOS.Api.Controllers;

/// <summary>
/// The cafe's tiffin/thali round — standing meal plans for regulars, and the daily "who gets a
/// plate today" list that falls out of them. Three tabs' worth of endpoints, one idea:
///
///  • <b>Subscribers</b> — who's on the plan, Daily or Occasional (see <see cref="TiffinType"/>).
///  • <b>Roster</b> — a chosen day's list. A Daily subscriber is on it by default and comes off
///    only when the customer says "aaj band"; an Occasional one is off by default and goes on
///    only when they ask. Both are the same toggle, opposite starting side — see
///    <see cref="TiffinMark"/>, the override row behind it.
///  • <b>Billing</b> — a month's bill per subscriber (days served × plates × rate), raised as a
///    frozen <see cref="TiffinInvoice"/> and paid back in instalments like a khata.
///
/// The whole controller is <see cref="RequireScreenAttribute">RequireScreen("Tiffin")</see> and
/// authenticated; reading and toggling the roster is open to any floor login the screen is on
/// for, but everything under Billing (raising a bill, taking money) is Owner/Manager only, for
/// the same reason KhatabookController is: clearing what a customer owes can't be a floor
/// decision, since the invoice is the cafe's only record the debt existed.
/// </summary>
[ApiController]
[Route("api/tiffin")]
[Authorize]
[RequireScreen("Tiffin")]
public class TiffinController(CafePosDbContext db) : ControllerBase
{
    private static readonly HashSet<string> ValidSettleMethods =
        new(StringComparer.OrdinalIgnoreCase) { "Cash", "Card", "UPI" };

    // A tiffin bill is a difference of two sums (billed minus paid); compare against a
    // paisa-sized epsilon rather than 0 so one settled to the last rupee is never left "Unpaid"
    // on a rounding tail. Same convention as KhatabookController.
    private const decimal Epsilon = 0.005m;

    private static DateOnly TodayIst => DateOnly.FromDateTime(IstClock.NowIst);

    // ─────────────────────────────  Subscribers  ─────────────────────────────

    /// <summary>Everyone on the plan. `search` matches name or mobile; inactive subscribers are
    /// hidden unless `includeInactive` is set, since the list's day-to-day job is the live round.</summary>
    [HttpGet("subscribers")]
    public async Task<List<TiffinSubscriberDto>> ListSubscribers(
        [FromQuery] string? search = null, [FromQuery] bool includeInactive = false)
    {
        var subs = await db.TiffinSubscribers
            .Include(s => s.Customer)
            .Where(s => includeInactive || s.IsActive)
            .ToListAsync();

        var balances = await WalletBalancesAsync(subs.Where(s => s.PaymentMode == TiffinPaymentMode.Prepaid).Select(s => s.Id));

        var term = search?.Trim();
        return subs
            .Where(s => s.Customer is not null)
            .Where(s => string.IsNullOrEmpty(term)
                || s.Customer!.Name.Contains(term, StringComparison.OrdinalIgnoreCase)
                || (s.Customer.Phone is not null && s.Customer.Phone.Contains(term)))
            .OrderBy(s => !s.IsActive)                 // active first
            .ThenBy(s => s.Customer!.Name, StringComparer.OrdinalIgnoreCase)
            .Select(s => TiffinSubscriberDto.From(s, s.Customer!, balances.GetValueOrDefault(s.Id)))
            .ToList();
    }

    /// <summary>Puts a customer on the plan. A phone already on file is reused rather than
    /// duplicated (same find-or-create rule as the POS's guest flow), so a regular who later
    /// takes a tiffin keeps one CRM record. One active subscription per customer — a second is
    /// rejected rather than quietly creating a duplicate round.</summary>
    [HttpPost("subscribers")]
    public async Task<ActionResult<TiffinSubscriberDto>> CreateSubscriber(CreateTiffinSubscriberRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Name))
            throw new ApiValidationException("Enter the customer's name.");
        // Required (unlike a walk-in POS guest) — a tiffin round runs on being able to reach the
        // customer, and re-validated here even though the client already checks it, same as
        // every other client-side rule in this controller.
        if (string.IsNullOrWhiteSpace(req.Phone) || !System.Text.RegularExpressions.Regex.IsMatch(req.Phone.Trim(), @"^\d{10}$"))
            throw new ApiValidationException("Enter a 10-digit mobile number.");
        if (req.Rate < 0)
            throw new ApiValidationException("Rate can't be negative.");
        var type = ParseType(req.Type);
        var mealType = ParseMealType(req.MealType);
        var paymentMode = ParsePaymentMode(req.PaymentMode);

        var customer = await ResolveCustomerAsync(req.Name, req.Phone);

        // Block a second subscription on the same (already-persisted) customer. A customer just
        // created above has Id 0 and can't collide, so this only bites the reuse path.
        if (customer.Id != 0)
        {
            var already = await db.TiffinSubscribers.FirstOrDefaultAsync(s => s.CustomerId == customer.Id && s.IsActive);
            if (already is not null)
                throw new ApiConflictException($"{customer.Name} is already on the tiffin plan.");
        }

        var sub = new TiffinSubscriber
        {
            Customer = customer,
            Type = type,
            PaymentMode = paymentMode,
            PlanName = string.IsNullOrWhiteSpace(req.PlanName) ? "Tiffin" : req.PlanName.Trim(),
            MealType = mealType,
            Rate = req.Rate,
            // Daily delivers this qty every day by default, so it can't be 0. Occasional
            // delivers nothing by default regardless (see BuildRosterEntry) — 0 there just means
            // no standing qty until someone marks a day, so it's left as entered.
            DefaultQty = type == TiffinType.Occasional ? Math.Max(req.DefaultQty, 0) : (req.DefaultQty > 0 ? req.DefaultQty : 1),
            DeliveryAddress = string.IsNullOrWhiteSpace(req.DeliveryAddress) ? null : req.DeliveryAddress.Trim(),
            StartDate = req.StartDate ?? TodayIst,
            Notes = string.IsNullOrWhiteSpace(req.Notes) ? null : req.Notes.Trim(),
        };
        db.TiffinSubscribers.Add(sub);
        await db.SaveChangesAsync();

        return TiffinSubscriberDto.From(sub, customer);
    }

    /// <summary>Edits the plan side of a subscription — plate, rate, quantity, address, type,
    /// active/paused. The customer's name and phone belong to the CRM record and are changed
    /// there. Changing Type or IsActive changes what the roster defaults to from now on; it
    /// never rewrites days already served.</summary>
    [HttpPut("subscribers/{id:int}")]
    public async Task<ActionResult<TiffinSubscriberDto>> UpdateSubscriber(int id, UpdateTiffinSubscriberRequest req)
    {
        var sub = await db.TiffinSubscribers.Include(s => s.Customer).FirstOrDefaultAsync(s => s.Id == id);
        if (sub is null || sub.Customer is null) return NotFound();
        if (req.Rate < 0)
            throw new ApiValidationException("Rate can't be negative.");

        sub.Type = ParseType(req.Type);
        sub.MealType = ParseMealType(req.MealType);
        sub.PaymentMode = ParsePaymentMode(req.PaymentMode);
        sub.PlanName = string.IsNullOrWhiteSpace(req.PlanName) ? "Tiffin" : req.PlanName.Trim();
        sub.Rate = req.Rate;
        sub.DefaultQty = sub.Type == TiffinType.Occasional ? Math.Max(req.DefaultQty, 0) : (req.DefaultQty > 0 ? req.DefaultQty : 1);
        sub.DeliveryAddress = string.IsNullOrWhiteSpace(req.DeliveryAddress) ? null : req.DeliveryAddress.Trim();
        sub.IsActive = req.IsActive;
        sub.Notes = string.IsNullOrWhiteSpace(req.Notes) ? null : req.Notes.Trim();
        await db.SaveChangesAsync();

        decimal? balance = sub.PaymentMode == TiffinPaymentMode.Prepaid ? await WalletBalanceAsync(sub.Id) : null;
        return TiffinSubscriberDto.From(sub, sub.Customer, balance);
    }

    // ─────────────────────────────  Prepaid wallet  ─────────────────────────────

    /// <summary>A Prepaid subscriber's balance and full transaction history, newest first —
    /// what backs the recharge sheet's "why is the balance what it is". Owner/Manager only, same
    /// as everything else that shows money.</summary>
    [HttpGet("subscribers/{id:int}/wallet")]
    [Authorize(Policy = Policies.OwnerOrManager)]
    public async Task<ActionResult<TiffinWalletDto>> GetWallet(int id)
    {
        var sub = await db.TiffinSubscribers.FirstOrDefaultAsync(s => s.Id == id);
        if (sub is null) return NotFound();

        var txns = await db.TiffinWalletTransactions
            .Where(t => t.SubscriberId == id)
            .OrderByDescending(t => t.CreatedAt).ThenByDescending(t => t.Id)
            .ToListAsync();

        return new TiffinWalletDto(txns.Sum(t => t.Amount), txns.Select(TiffinWalletTransactionDto.From).ToList());
    }

    /// <summary>Tops up a Prepaid subscriber's balance. Rejected for a Postpaid subscriber — that
    /// plan settles by invoice, not a wallet; switch PaymentMode first. Owner/Manager only, same
    /// reasoning as Settle: taking money can't be a floor decision.</summary>
    [HttpPost("subscribers/{id:int}/recharge")]
    [Authorize(Policy = Policies.OwnerOrManager)]
    public async Task<ActionResult<TiffinSubscriberDto>> Recharge(int id, RechargeTiffinWalletRequest req)
    {
        var sub = await db.TiffinSubscribers.Include(s => s.Customer).FirstOrDefaultAsync(s => s.Id == id);
        if (sub is null || sub.Customer is null) return NotFound();
        if (sub.PaymentMode != TiffinPaymentMode.Prepaid)
            throw new ApiValidationException($"{sub.Customer.Name} is on a Postpaid plan — switch them to Prepaid first.");
        if (req.Amount <= 0)
            throw new ApiValidationException("Enter an amount greater than zero.");
        if (string.IsNullOrWhiteSpace(req.Method) || !ValidSettleMethods.Contains(req.Method.Trim()))
            throw new ApiValidationException($"'{req.Method}' isn't a valid payment method — a top-up is Cash, Card or UPI.");

        db.TiffinWalletTransactions.Add(new TiffinWalletTransaction
        {
            SubscriberId = sub.Id,
            Type = TiffinWalletTxnType.Recharge,
            Amount = req.Amount,
            Method = ValidSettleMethods.TryGetValue(req.Method.Trim(), out var canonical) ? canonical : req.Method.Trim(),
            Note = string.IsNullOrWhiteSpace(req.Note) ? null : req.Note.Trim(),
            RecordedByUserId = CurrentUserId(),
            RecordedByName = CurrentUserName(),
        });
        await db.SaveChangesAsync();

        return TiffinSubscriberDto.From(sub, sub.Customer, await WalletBalanceAsync(sub.Id));
    }

    // ─────────────────────────────  Roster  ─────────────────────────────

    /// <summary>One day's round — every live subscriber whose plan had started by that date,
    /// with the effective "delivering / skipped" already resolved from the type's default and
    /// the day's mark (if any), plus the kitchen's veg/non-veg headcount on top.</summary>
    [HttpGet("roster")]
    public async Task<TiffinRosterDto> Roster([FromQuery] DateOnly? date = null)
    {
        var day = date ?? TodayIst;

        var subs = await db.TiffinSubscribers
            .Include(s => s.Customer)
            .Where(s => s.IsActive && s.StartDate <= day)
            .ToListAsync();

        var subIds = subs.Select(s => s.Id).ToList();
        var marks = await db.TiffinMarks
            .Where(m => m.Date == day && subIds.Contains(m.SubscriberId))
            .ToListAsync();
        var markBySub = marks.ToDictionary(m => m.SubscriberId);

        var entries = subs
            .Where(s => s.Customer is not null)
            .Select(s => BuildRosterEntry(s, markBySub.GetValueOrDefault(s.Id)))
            // Plain alphabetical — a STABLE order that never shuffles when a row is toggled. A
            // "delivering first" sort would jump a just-skipped row down to the bottom mid-mark,
            // costing whoever's working down the list their place; the kitchen's headcount lives
            // in the summary card instead, and a skipped row is dimmed in place, not moved.
            .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Prepaid subscribers settle this day's charge (or clear it, if the day turned out to be
        // a skip) as soon as it's known — including a Daily subscriber's untouched default day,
        // which has no TiffinMark row and so would otherwise never trigger a deduction. Only for
        // today-or-earlier: a day being planned ahead hasn't happened yet, nothing should leave
        // the wallet for it (see SyncWalletDeductionAsync).
        if (day <= TodayIst)
        {
            var byId = subs.ToDictionary(s => s.Id);
            foreach (var e in entries)
            {
                if (byId[e.SubscriberId].PaymentMode == TiffinPaymentMode.Prepaid)
                    await SyncWalletDeductionAsync(byId[e.SubscriberId], day, e.Delivering, e.Qty);
            }
            await db.SaveChangesAsync();
        }

        var prepaidIds = subs.Where(s => s.PaymentMode == TiffinPaymentMode.Prepaid).Select(s => s.Id).ToList();
        if (prepaidIds.Count > 0)
        {
            var balances = await WalletBalancesAsync(prepaidIds);
            entries = entries
                .Select(e => balances.TryGetValue(e.SubscriberId, out var bal) ? e with { WalletBalance = bal } : e)
                .ToList();
        }

        var delivering = entries.Where(e => e.Delivering).ToList();
        var summary = new TiffinRosterSummaryDto(
            delivering.Count,
            delivering.Sum(e => e.Qty),
            delivering.Where(e => e.MealType == nameof(TiffinMealType.Veg)).Sum(e => e.Qty),
            delivering.Where(e => e.MealType == nameof(TiffinMealType.NonVeg)).Sum(e => e.Qty),
            delivering.Where(e => e.MealType == nameof(TiffinMealType.Custom)).Sum(e => e.Qty),
            entries.Count - delivering.Count);

        return new TiffinRosterDto(day, summary, entries);
    }

    /// <summary>Toggles one subscriber's day. Writes, updates, or deletes the day's override row
    /// so the stored state reaches the requested one given the subscriber's type — a Daily "yes"
    /// is the absence of a skip (so any skip row is cleared), a Daily "no" a Skipped row, an
    /// Occasional "yes" a Delivered row, an Occasional "no" the absence of one. A non-default
    /// quantity is kept as a Delivered row even for a Daily subscriber.</summary>
    [HttpPost("roster/mark")]
    public async Task<ActionResult<TiffinRosterEntryDto>> Mark(MarkTiffinRequest req)
    {
        var sub = await db.TiffinSubscribers.Include(s => s.Customer).FirstOrDefaultAsync(s => s.Id == req.SubscriberId);
        if (sub is null || sub.Customer is null) return NotFound();

        var existing = await db.TiffinMarks
            .FirstOrDefaultAsync(m => m.SubscriberId == req.SubscriberId && m.Date == req.Date);
        var qty = req.Qty is int q && q > 0 ? q : sub.DefaultQty;

        void Upsert(TiffinMarkStatus status, int markQty)
        {
            if (existing is null)
            {
                existing = new TiffinMark
                {
                    SubscriberId = sub.Id,
                    Date = req.Date,
                    Status = status,
                    Qty = markQty,
                    RecordedByUserId = CurrentUserId(),
                    RecordedByName = CurrentUserName(),
                };
                db.TiffinMarks.Add(existing);
            }
            else
            {
                existing.Status = status;
                existing.Qty = markQty;
                existing.RecordedByUserId = CurrentUserId();
                existing.RecordedByName = CurrentUserName();
                existing.UpdatedAt = DateTime.UtcNow;
            }
        }

        if (sub.Type == TiffinType.Daily)
        {
            if (req.Deliver)
            {
                // Default already delivers — only a non-default quantity needs a row.
                if (qty != sub.DefaultQty) Upsert(TiffinMarkStatus.Delivered, qty);
                else if (existing is not null) db.TiffinMarks.Remove(existing);
            }
            else
            {
                Upsert(TiffinMarkStatus.Skipped, sub.DefaultQty);
            }
        }
        else // Occasional
        {
            if (req.Deliver) Upsert(TiffinMarkStatus.Delivered, qty);
            else if (existing is not null) db.TiffinMarks.Remove(existing);
        }

        await db.SaveChangesAsync();

        // Re-read the mark's committed state (it may have just been deleted) so the response is
        // the effective row the client should now show.
        var fresh = await db.TiffinMarks.AsNoTracking()
            .FirstOrDefaultAsync(m => m.SubscriberId == req.SubscriberId && m.Date == req.Date);
        var entry = BuildRosterEntry(sub, fresh);

        if (sub.PaymentMode == TiffinPaymentMode.Prepaid && req.Date <= TodayIst)
        {
            await SyncWalletDeductionAsync(sub, req.Date, entry.Delivering, entry.Qty);
            await db.SaveChangesAsync();
            entry = entry with { WalletBalance = await WalletBalanceAsync(sub.Id) };
        }

        return entry;
    }

    // ─────────────────────────────  Billing  ─────────────────────────────

    /// <summary>A month's computed bill for every subscriber — days served × plates × rate —
    /// alongside any invoice already raised for the exact period, so the screen shows "already
    /// billed" rather than offering to bill twice. Owner/Manager only.</summary>
    [HttpGet("billing")]
    [Authorize(Policy = Policies.OwnerOrManager)]
    public async Task<ActionResult<TiffinBillingDto>> Billing([FromQuery] string? month = null)
    {
        var (start, end) = ResolveMonth(month);
        return await ComputeBillingAsync(start, end);
    }

    /// <summary>Raises invoices for a month. With no <c>SubscriberId</c> it bills everyone with a
    /// non-zero, not-yet-invoiced figure; with one, exactly that subscriber. Each invoice freezes
    /// the days/plates/rate that produced it. Owner/Manager only.</summary>
    [HttpPost("billing/generate")]
    [Authorize(Policy = Policies.OwnerOrManager)]
    public Task<ActionResult<TiffinBillingDto>> Generate(GenerateTiffinInvoicesRequest req) =>
        DbConcurrency.InTransactionAsync<ActionResult<TiffinBillingDto>>(db, async () =>
    {
        var (start, end) = ResolveMonth(req.Month);
        if (end < start)
            throw new ApiValidationException("That month hasn't started yet — there's nothing to bill.");

        var subs = await db.TiffinSubscribers.Include(s => s.Customer)
            .Where(s => req.SubscriberId == null || s.Id == req.SubscriberId)
            .ToListAsync();
        var subIds = subs.Select(s => s.Id).ToList();

        var marks = await LoadMarksAsync(subIds, start, end);
        var existing = await db.TiffinInvoices
            .Where(i => subIds.Contains(i.SubscriberId) && i.PeriodStart == start && i.PeriodEnd == end)
            .Select(i => i.SubscriberId)
            .ToListAsync();
        var alreadyBilled = existing.ToHashSet();

        var userId = CurrentUserId();
        var userName = CurrentUserName();
        var created = 0;

        foreach (var sub in subs)
        {
            // Prepaid subscribers never get an invoice — their delivered days are already
            // settled day-by-day out of their wallet (see SyncWalletDeductionAsync).
            if (sub.Customer is null || alreadyBilled.Contains(sub.Id) || sub.PaymentMode == TiffinPaymentMode.Prepaid) continue;
            var (days, plates) = ComputeServed(sub, start, end, marks.GetValueOrDefault(sub.Id));
            if (days == 0) continue;

            db.TiffinInvoices.Add(new TiffinInvoice
            {
                SubscriberId = sub.Id,
                CustomerId = sub.CustomerId,
                PeriodStart = start,
                PeriodEnd = end,
                DeliveredDays = days,
                TotalQty = plates,
                Rate = sub.Rate,
                TotalAmount = plates * sub.Rate,
                AmountPaid = 0,
                Status = plates * sub.Rate <= Epsilon ? TiffinInvoiceStatus.Paid : TiffinInvoiceStatus.Unpaid,
                GeneratedByUserId = userId,
                GeneratedByName = userName,
            });
            created++;
        }

        if (created > 0) await db.SaveChangesAsync();
        return await ComputeBillingAsync(start, end);
    });

    /// <summary>Every raised invoice, most-outstanding first, with the tenant's whole position on
    /// top. `status` filters to Unpaid / PartiallyPaid / Paid. Owner/Manager only.</summary>
    [HttpGet("invoices")]
    [Authorize(Policy = Policies.OwnerOrManager)]
    public async Task<TiffinInvoiceListDto> ListInvoices([FromQuery] string? status = null)
    {
        var invoices = await db.TiffinInvoices
            .Include(i => i.Subscriber)
            .Include(i => i.Subscriber!.Customer)
            .ToListAsync();

        var filtered = invoices
            .Where(i => i.Subscriber?.Customer is not null)
            .Where(i => string.IsNullOrEmpty(status) || i.Status.ToString().Equals(status, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(i => i.TotalAmount - i.AmountPaid)
            .ThenByDescending(i => i.CreatedAt)
            .Select(i => TiffinInvoiceDto.From(i, i.Subscriber!.Customer!))
            .ToList();

        var outstanding = invoices.Sum(i => i.TotalAmount - i.AmountPaid);
        var withDue = invoices.Count(i => i.TotalAmount - i.AmountPaid > Epsilon);

        var istNow = IstClock.NowIst;
        var monthStartUtc = IstClock.IstDateStartUtc(new DateOnly(istNow.Year, istNow.Month, 1));
        var payments = await db.TiffinPayments.Where(p => p.CreatedAt >= monthStartUtc).Select(p => p.Amount).ToListAsync();

        return new TiffinInvoiceListDto(
            new TiffinInvoiceListSummaryDto(outstanding, withDue, payments.Sum()),
            filtered);
    }

    /// <summary>One invoice with its repayments, newest first. Owner/Manager only.</summary>
    [HttpGet("invoices/{id:int}")]
    [Authorize(Policy = Policies.OwnerOrManager)]
    public async Task<ActionResult<TiffinInvoiceDetailDto>> GetInvoice(int id)
    {
        var invoice = await db.TiffinInvoices
            .Include(i => i.Subscriber).Include(i => i.Subscriber!.Customer)
            .FirstOrDefaultAsync(i => i.Id == id);
        if (invoice is null || invoice.Subscriber?.Customer is null) return NotFound();

        var payments = await db.TiffinPayments
            .Where(p => p.InvoiceId == id)
            .OrderByDescending(p => p.CreatedAt).ThenByDescending(p => p.Id)
            .Select(p => TiffinPaymentDto.From(p))
            .ToListAsync();

        return new TiffinInvoiceDetailDto(TiffinInvoiceDto.From(invoice, invoice.Subscriber.Customer), payments);
    }

    /// <summary>Records money collected against an invoice — the whole amount or any part, as
    /// many times as the customer pays. Overpayment is rejected rather than left as a credit
    /// balance. Serialised under a row lock so two devices can't both clear the same bill for
    /// half the money — same guard KhatabookController.Settle uses. Owner/Manager only.</summary>
    [HttpPost("invoices/{id:int}/settle")]
    [Authorize(Policy = Policies.OwnerOrManager)]
    public Task<ActionResult<TiffinInvoiceDetailDto>> Settle(int id, SettleTiffinInvoiceRequest req) =>
        DbConcurrency.InTransactionAsync<ActionResult<TiffinInvoiceDetailDto>>(db, async () =>
    {
        if (req.Amount <= 0)
            throw new ApiValidationException("Enter an amount greater than zero.");
        if (string.IsNullOrWhiteSpace(req.Method) || !ValidSettleMethods.Contains(req.Method.Trim()))
            throw new ApiValidationException($"'{req.Method}' isn't a valid payment method — a tiffin bill is settled with Cash, Card or UPI.");

        await DbConcurrency.LockRowsAsync<TiffinInvoice>(db, id);
        var invoice = await db.TiffinInvoices.FirstOrDefaultAsync(i => i.Id == id);
        if (invoice is null) return NotFound();

        var outstanding = invoice.TotalAmount - invoice.AmountPaid;
        if (outstanding <= Epsilon)
            throw new ApiConflictException("This bill is already cleared — nothing left to settle.");
        if (req.Amount - outstanding > Epsilon)
            throw new ApiValidationException($"₹{req.Amount:0.00} is more than the ₹{outstanding:0.00} outstanding on this bill.");

        db.TiffinPayments.Add(new TiffinPayment
        {
            InvoiceId = id,
            Amount = req.Amount,
            Method = ValidSettleMethods.TryGetValue(req.Method.Trim(), out var canonical) ? canonical : req.Method.Trim(),
            Note = string.IsNullOrWhiteSpace(req.Note) ? null : req.Note.Trim(),
            RecordedByUserId = CurrentUserId(),
            RecordedByName = CurrentUserName(),
        });
        invoice.AmountPaid += req.Amount;
        invoice.Status = invoice.TotalAmount - invoice.AmountPaid <= Epsilon
            ? TiffinInvoiceStatus.Paid
            : TiffinInvoiceStatus.PartiallyPaid;
        await db.SaveChangesAsync();

        return await GetInvoice(id);
    });

    // ─────────────────────────────  Internals  ─────────────────────────────

    /// <summary>The effective day-view for one subscriber, given that day's override row (or its
    /// absence). This one method is the single source of truth the roster and the billing scan
    /// both read the "delivering / plates" rule from.</summary>
    private static TiffinRosterEntryDto BuildRosterEntry(TiffinSubscriber s, TiffinMark? mark)
    {
        bool delivering;
        int qty;
        if (s.Type == TiffinType.Daily)
        {
            delivering = mark?.Status != TiffinMarkStatus.Skipped;
            qty = mark?.Status == TiffinMarkStatus.Delivered ? mark.Qty : s.DefaultQty;
        }
        else
        {
            delivering = mark?.Status == TiffinMarkStatus.Delivered;
            qty = delivering ? mark!.Qty : s.DefaultQty;
        }

        return new TiffinRosterEntryDto(
            s.Id, s.CustomerId, s.Customer!.Name, s.Customer.Phone, s.Type.ToString(),
            s.PlanName, s.MealType.ToString(), s.DeliveryAddress, s.DefaultQty,
            delivering, qty, mark?.Status.ToString(),
            s.PaymentMode.ToString(), WalletBalance: null); // caller fills this in for Prepaid — see Roster/Mark
    }

    /// <summary>Days served and total plates for one subscriber over a period, applying the exact
    /// same rule <see cref="BuildRosterEntry"/> shows on the roster, day by day. A Daily
    /// subscriber accrues every day from its start date that isn't skipped; an Occasional one
    /// only the days explicitly marked Delivered.</summary>
    private static (int days, int plates) ComputeServed(
        TiffinSubscriber sub, DateOnly start, DateOnly end, Dictionary<DateOnly, TiffinMark>? marks)
    {
        var from = sub.StartDate > start ? sub.StartDate : start;
        var days = 0;
        var plates = 0;
        for (var d = from; d <= end; d = d.AddDays(1))
        {
            var mark = marks?.GetValueOrDefault(d);
            bool delivering;
            int qty;
            if (sub.Type == TiffinType.Daily)
            {
                delivering = mark?.Status != TiffinMarkStatus.Skipped;
                qty = mark?.Status == TiffinMarkStatus.Delivered ? mark.Qty : sub.DefaultQty;
            }
            else
            {
                delivering = mark?.Status == TiffinMarkStatus.Delivered;
                qty = delivering ? mark!.Qty : sub.DefaultQty;
            }
            if (!delivering) continue;
            days++;
            plates += qty;
        }
        return (days, plates);
    }

    private async Task<Dictionary<int, Dictionary<DateOnly, TiffinMark>>> LoadMarksAsync(
        List<int> subIds, DateOnly start, DateOnly end)
    {
        var marks = await db.TiffinMarks
            .Where(m => subIds.Contains(m.SubscriberId) && m.Date >= start && m.Date <= end)
            .ToListAsync();
        return marks
            .GroupBy(m => m.SubscriberId)
            .ToDictionary(g => g.Key, g => g.ToDictionary(m => m.Date));
    }

    private async Task<TiffinBillingDto> ComputeBillingAsync(DateOnly start, DateOnly end)
    {
        var subs = await db.TiffinSubscribers.Include(s => s.Customer).ToListAsync();
        var subIds = subs.Select(s => s.Id).ToList();
        var marks = await LoadMarksAsync(subIds, start, end);

        var invoices = await db.TiffinInvoices
            .Where(i => i.PeriodStart == start && i.PeriodEnd == end)
            .ToListAsync();
        var invoiceBySub = invoices.ToDictionary(i => i.SubscriberId);

        var lines = new List<TiffinBillingLineDto>();
        foreach (var sub in subs)
        {
            // Prepaid subscribers don't appear in the billing conversation — nothing to collect,
            // their wallet already carries what they owe/have credited.
            if (sub.Customer is null || sub.PaymentMode == TiffinPaymentMode.Prepaid) continue;
            var (days, plates) = ComputeServed(sub, start, end, marks.GetValueOrDefault(sub.Id));
            var invoice = invoiceBySub.GetValueOrDefault(sub.Id);
            // Skip the wall of zeroes — a subscriber with nothing served and no bill for the
            // period isn't part of this month's billing conversation.
            if (days == 0 && invoice is null) continue;

            lines.Add(new TiffinBillingLineDto(
                sub.Id, sub.CustomerId, sub.Customer.Name, sub.Customer.Phone, sub.Type.ToString(),
                sub.PlanName, sub.Rate, days, plates, plates * sub.Rate,
                invoice?.Id, invoice?.Status.ToString(), invoice?.AmountPaid ?? 0));
        }

        lines = lines
            .OrderByDescending(l => l.InvoiceId == null)   // not-yet-billed first (the work to do)
            .ThenByDescending(l => l.Amount)
            .ThenBy(l => l.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var summary = new TiffinBillingSummaryDto(
            lines.Count,
            lines.Sum(l => l.TotalQty),
            lines.Sum(l => l.Amount),
            invoices.Sum(i => i.TotalAmount),
            invoices.Sum(i => i.AmountPaid),
            invoices.Sum(i => i.TotalAmount - i.AmountPaid));

        return new TiffinBillingDto(start, end, summary, lines);
    }

    /// <summary>Turns a "yyyy-MM" (or null = current month) into the IST period to bill: the
    /// first of the month through today, never past today — you can't bill days that haven't
    /// happened. A fully-past month runs first-to-last as normal.</summary>
    private static (DateOnly start, DateOnly end) ResolveMonth(string? month)
    {
        var today = TodayIst;
        DateOnly start;
        if (string.IsNullOrWhiteSpace(month) ||
            !DateTime.TryParseExact(month.Trim(), "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            start = new DateOnly(today.Year, today.Month, 1);
        }
        else
        {
            start = new DateOnly(parsed.Year, parsed.Month, 1);
        }

        var lastOfMonth = start.AddMonths(1).AddDays(-1);
        var end = lastOfMonth < today ? lastOfMonth : today;
        return (start, end);
    }

    private async Task<Customer> ResolveCustomerAsync(string name, string? phone)
    {
        var trimmedName = name.Trim();
        var trimmedPhone = string.IsNullOrWhiteSpace(phone) ? null : phone.Trim();

        if (trimmedPhone is not null)
        {
            var byPhone = await db.Customers.FirstOrDefaultAsync(c => c.Phone == trimmedPhone);
            if (byPhone is not null) return byPhone;
        }

        var slug = new string(trimmedName.Where(char.IsLetter).ToArray()).ToUpperInvariant();
        var customer = new Customer
        {
            Name = trimmedName,
            Phone = trimmedPhone,
            ReferralCode = $"{(slug.Length >= 4 ? slug[..4] : slug.PadRight(4, 'X'))}{Random.Shared.Next(100, 999)}",
        };
        db.Customers.Add(customer);
        return customer;
    }

    private static TiffinType ParseType(string type) =>
        Enum.TryParse<TiffinType>(type, ignoreCase: true, out var t)
            ? t
            : throw new ApiValidationException($"'{type}' isn't a valid subscription type — use Daily or Occasional.");

    private static TiffinMealType ParseMealType(string mealType) =>
        Enum.TryParse<TiffinMealType>(mealType, ignoreCase: true, out var m)
            ? m
            : throw new ApiValidationException($"'{mealType}' isn't a valid meal type — use Veg, NonVeg or Custom.");

    private static TiffinPaymentMode ParsePaymentMode(string paymentMode) =>
        Enum.TryParse<TiffinPaymentMode>(paymentMode, ignoreCase: true, out var p)
            ? p
            : throw new ApiValidationException($"'{paymentMode}' isn't a valid payment mode — use Postpaid or Prepaid.");

    private async Task<decimal> WalletBalanceAsync(int subscriberId) =>
        await db.TiffinWalletTransactions.Where(t => t.SubscriberId == subscriberId).SumAsync(t => (decimal?)t.Amount) ?? 0m;

    private async Task<Dictionary<int, decimal>> WalletBalancesAsync(IEnumerable<int> subscriberIds)
    {
        var ids = subscriberIds.ToList();
        if (ids.Count == 0) return [];
        return await db.TiffinWalletTransactions
            .Where(t => ids.Contains(t.SubscriberId))
            .GroupBy(t => t.SubscriberId)
            .Select(g => new { g.Key, Sum = g.Sum(t => t.Amount) })
            .ToDictionaryAsync(x => x.Key, x => x.Sum);
    }

    /// <summary>Keeps a Prepaid subscriber's Deduction row for one date in sync with that date's
    /// effective delivering/qty — the single place that actually moves money out of a wallet.
    /// Called from both Roster (so a Daily subscriber's untouched default day still charges, once
    /// that day is viewed) and Mark (so an explicit toggle corrects the charge immediately,
    /// without waiting for the roster to be reloaded). Idempotent: re-syncing the same
    /// (subscriber, date) to the same state is a no-op. Frozen at the rate in force right now —
    /// a later rate change never rewrites a day already charged, same guarantee an invoice gives
    /// a Postpaid bill. Caller is responsible for SaveChangesAsync.</summary>
    private async Task SyncWalletDeductionAsync(TiffinSubscriber sub, DateOnly date, bool delivering, int qty)
    {
        var existing = await db.TiffinWalletTransactions.FirstOrDefaultAsync(
            t => t.SubscriberId == sub.Id && t.Type == TiffinWalletTxnType.Deduction && t.ForDate == date);
        var amount = delivering ? -(qty * sub.Rate) : 0m;

        if (amount == 0m)
        {
            if (existing is not null) db.TiffinWalletTransactions.Remove(existing);
            return;
        }
        if (existing is null)
        {
            db.TiffinWalletTransactions.Add(new TiffinWalletTransaction
            {
                SubscriberId = sub.Id,
                Type = TiffinWalletTxnType.Deduction,
                ForDate = date,
                Amount = amount,
                RecordedByName = "System (roster)",
            });
        }
        else if (existing.Amount != amount)
        {
            existing.Amount = amount;
        }
    }

    private int? CurrentUserId()
    {
        var claim = User.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value;
        return int.TryParse(claim, out var id) ? id : null;
    }

    private string CurrentUserName() => User.Identity?.Name ?? "Cafe Staff";
}
