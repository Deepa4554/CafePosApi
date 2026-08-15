using System.Text.RegularExpressions;
using CafePOS.Api.Contracts;
using CafePOS.Api.Data;
using CafePOS.Api.Domain;
using CafePOS.Api.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CafePOS.Api.Controllers;

// Not gated at class level: coupons/apply (and the checkout flow that calls it) must
// work for every plan tier — only the CRM-directory/issuing actions below are Plus+.
[ApiController]
[Route("api/customers")]
public class CustomersController(CafePosDbContext db) : ControllerBase
{
    private static readonly Regex EmailPattern = new(@"^[^\s@]+@[^\s@]+\.[^\s@]+$", RegexOptions.Compiled);
    private static readonly Regex PhonePattern = new(@"^\d{10}$", RegexOptions.Compiled);

    /// <summary>Email/phone are optional on a Customer, so this only rejects a
    /// value that's actually present and malformed — never rejects "not given".</summary>
    private static void ValidateContactFields(string? name, string? email, string? phone)
    {
        if (name is not null && name.Trim().Length > 200)
            throw new ApiValidationException("Name cannot exceed 200 characters.");
        if (!string.IsNullOrWhiteSpace(email) && !EmailPattern.IsMatch(email))
            throw new ApiValidationException("Enter a valid email address.");
        if (!string.IsNullOrWhiteSpace(phone) && !PhonePattern.IsMatch(phone))
            throw new ApiValidationException("Enter a valid 10-digit phone number.");
    }

    /// <summary>
    /// Real CRM analytics — replaces the old CRMInsightsScreen's fully hardcoded
    /// SEGMENTS/NEW_DATA/RETURNING_DATA/REDEMPTION constants. Every number here, including
    /// the closing "Suggestion" line, is computed straight from real Customer/Order/Coupon
    /// rows — see BuildSuggestion for the rule-matching (no LLM call, ₹0 cost, always
    /// available instead of occasionally missing on a Gemini failure).
    /// </summary>
    [Authorize(Policy = Policies.RequirePlus)]
    [HttpGet("insights")]
    public async Task<CrmInsightsDto> Insights()
    {
        var customers = await db.Customers.ToListAsync();
        var totalCustomers = customers.Count;
        var retentionRate = totalCustomers > 0 ? customers.Count(c => c.VisitCount >= 2) * 100.0 / totalCustomers : 0;
        var avgLtv = totalCustomers > 0 ? customers.Average(c => c.TotalSpent) : 0;

        // The week's days are the cafe's, not UTC's (see IstClock) — on a UTC boundary each bar
        // would run 5:30am-to-5:30am, so a customer who came in after midnight would be counted
        // against the previous day, and today's bar would look emptier than the floor did.
        var weekStartIst = IstClock.NowIst.Date.AddDays(-6);
        var weekStartUtc = weekStartIst - IstClock.Offset;
        var recentOrders = await db.Orders
            .Where(o => o.CreatedAt >= weekStartUtc && o.CustomerId != null)
            .Select(o => new { o.CreatedAt, o.CustomerId })
            .ToListAsync();
        var customerJoinDates = customers.ToDictionary(c => c.Id, c => IstClock.ToIst(c.JoinedAt).Date);

        var growth = Enumerable.Range(0, 7).Select(offset =>
        {
            var day = weekStartIst.AddDays(offset);
            var dayCustomerIds = recentOrders.Where(o => IstClock.ToIst(o.CreatedAt).Date == day).Select(o => o.CustomerId!.Value).Distinct().ToList();
            var newCount = dayCustomerIds.Count(cid => customerJoinDates.TryGetValue(cid, out var joined) && joined == day);
            return new CrmGrowthPointDto(day.ToString("ddd"), newCount, dayCustomerIds.Count - newCount);
        }).ToList();

        var redemption = await db.Coupons
            .GroupBy(c => c.Title)
            .Select(g => new { Title = g.Key, Issued = g.Count(), Redeemed = g.Count(c => c.IsUsed) })
            .OrderByDescending(g => g.Issued)
            .Take(5)
            .ToListAsync();
        var redemptionDtos = redemption
            .Select(g => new CrmRedemptionDto(g.Title, g.Issued, g.Redeemed, g.Issued > 0 ? (int)Math.Round(g.Redeemed * 100.0 / g.Issued) : 0))
            .ToList();

        var now = DateTime.UtcNow;
        var frequent = customers.Where(c => c.VisitCount >= 5).ToList();
        var newCustomers30d = customers.Where(c => c.JoinedAt >= now.AddDays(-30)).ToList();
        var atRisk = customers.Where(c => c.VisitCount >= 2 && c.LastVisitAt < now.AddDays(-30)).ToList();

        var segments = new List<CrmSegmentDto>
        {
            new("Frequent Visitors", "5 or more lifetime visits", frequent.Count, frequent.Count > 0 ? Math.Round(frequent.Average(c => c.TotalSpent), 2) : 0, ["LOYAL"]),
            new("New Customers", "Joined in the last 30 days", newCustomers30d.Count, newCustomers30d.Count > 0 ? Math.Round(newCustomers30d.Average(c => c.TotalSpent), 2) : 0, ["NEW", "30 DAYS"]),
            new("At Risk", "2+ visits before, none in the last 30 days", atRisk.Count, atRisk.Count > 0 ? Math.Round(atRisk.Average(c => c.TotalSpent), 2) : 0, ["LAPSING"]),
        };

        var suggestions = BuildSuggestions(totalCustomers, retentionRate, avgLtv, frequent, newCustomers30d, atRisk, redemptionDtos, growth);

        return new CrmInsightsDto(Math.Round(retentionRate, 1), Math.Round(avgLtv, 2), growth, redemptionDtos, segments, suggestions);
    }

    /// <summary>
    /// Up to 2 actionable tips, picked by matching real numbers against a priority-ordered
    /// list of conditions — no LLM, deterministic, free to compute. Two improvements over a
    /// naive "first match wins": (1) thresholds are scaled to the cafe's own customer count
    /// (percentages, not fixed numbers) so a 10-customer cafe and a 500-customer one each
    /// get thresholds that mean something for their size; (2) every matching condition is
    /// collected rather than stopping at the first, then the single highest-priority one
    /// always leads and a second slot rotates (by day-of-year) among the rest — so a cafe
    /// with several real signals at once doesn't see the exact same pair every single day.
    /// </summary>
    private static List<string> BuildSuggestions(
        int totalCustomers, double retentionRate, decimal avgLtv,
        List<Customer> frequent, List<Customer> newCustomers30d, List<Customer> atRisk,
        List<CrmRedemptionDto> redemption, List<CrmGrowthPointDto> growth)
    {
        if (totalCustomers == 0) return []; // nothing to say yet — matches the screen's other empty states

        decimal AvgSpend(List<Customer> list) => list.Count > 0 ? Math.Round(list.Average(c => c.TotalSpent), 0) : 0;
        var atRiskAvg = AvgSpend(atRisk);
        var frequentAvg = AvgSpend(frequent);
        var atRiskPct = atRisk.Count * 100.0 / totalCustomers;
        var frequentPct = frequent.Count * 100.0 / totalCustomers;
        var newPct = newCustomers30d.Count * 100.0 / totalCustomers;

        // Ordered highest-value action first — position in this list is what "priority"
        // means below (candidates[0] always leads; see the rotation at the bottom).
        var candidates = new List<string>();

        // --- Win-back (At Risk) — usually the highest-ROI action a small cafe can take.
        // Percentage-of-base + a small absolute floor, so 2 at-risk customers out of 4
        // don't trigger the same alarm as 2 out of 400.
        if (atRisk.Count >= 3 && atRiskPct >= 25)
            candidates.Add($"{atRisk.Count} of your customers ({atRiskPct:0}%) haven't visited in 30+ days — a 15% win-back coupon could bring a good chunk of them back.");
        else if (atRisk.Count >= 2 && atRiskPct >= 12)
            candidates.Add($"{atRisk.Count} regulars haven't visited in over a month — a 10% comeback coupon could bring them back.");
        if (atRisk.Count >= 2 && avgLtv > 0 && atRiskAvg > avgLtv * 1.3m)
            candidates.Add($"Your at-risk customers spend above average (₹{atRiskAvg} vs ₹{avgLtv:0} overall) — worth a bigger win-back offer for this group specifically.");
        if (atRisk.Count is >= 1 and <= 3)
            candidates.Add($"A few regulars are slipping away ({atRisk.Count}) — a personal WhatsApp check-in may work better than a blanket coupon at this scale.");

        // --- Retention health ---
        if (totalCustomers >= 8 && retentionRate < 30)
            candidates.Add($"Retention is low ({retentionRate:0}%) — prioritize a comeback campaign before spending on new-customer offers.");
        if (totalCustomers >= 8 && retentionRate >= 70)
            candidates.Add($"Retention is strong ({retentionRate:0}%) — focus on average order value next, e.g. a combo or upsell offer.");

        // --- New customer conversion — also scaled to the cafe's own base ---
        if (totalCustomers >= 5 && newPct >= 30 && retentionRate < 40)
            candidates.Add($"You gained {newCustomers30d.Count} new customers this month but repeat visits are low ({retentionRate:0}%) — a 2nd-visit discount could convert more of them.");
        else if (totalCustomers >= 5 && newPct >= 30)
            candidates.Add($"{newCustomers30d.Count} new customers joined this month — a first-return offer helps turn them into regulars.");
        if (totalCustomers >= 8 && newPct < 5)
            candidates.Add("New customer growth is slow this month — a referral or first-visit offer could help attract more.");

        // --- Coupon performance ---
        var deadCoupon = redemption.FirstOrDefault(r => r.Issued >= 5 && r.Pct == 0);
        if (deadCoupon is not null)
            candidates.Add($"'{deadCoupon.Title}' has been issued {deadCoupon.Issued} times with 0% redemption — the offer or its visibility may need a rethink.");
        var hotCoupon = redemption.FirstOrDefault(r => r.Pct >= 60);
        if (hotCoupon is not null)
            candidates.Add($"'{hotCoupon.Title}' is working well ({hotCoupon.Pct}% redeemed) — consider extending or repeating it.");
        if (redemption.Count == 0 && totalCustomers >= 3)
            candidates.Add("You haven't issued any coupons yet — a simple 10% first-time offer is an easy way to start.");

        // --- Frequent-visitor loyalty ---
        if (frequent.Count >= 3 && frequentPct >= 20)
            candidates.Add($"{frequent.Count} of your customers ({frequentPct:0}%) are frequent visitors — reward them with a loyalty perk, like a free item on their next visit.");
        if (frequent.Count >= 3 && avgLtv > 0 && frequentAvg > avgLtv * 1.5m)
            candidates.Add($"Your frequent visitors average ₹{frequentAvg} — a VIP tier or exclusive perk could deepen that further.");

        // --- Growth trend, last 7 days ---
        if (growth.Count == 7 && growth.All(g => g.NewCustomers == 0 && g.ReturningCustomers == 0))
            candidates.Add("No customer visits recorded this week — worth checking that QR ordering and walk-in tracking are both working.");
        else if (growth.Count == 7)
        {
            var earlyWeekNew = growth.Take(3).Sum(g => g.NewCustomers);
            var lateWeekNew = growth.Skip(4).Sum(g => g.NewCustomers);
            if (lateWeekNew >= 3 && lateWeekNew > earlyWeekNew * 1.5)
                candidates.Add("New customer visits are trending up this week — a good time to launch a referral program.");

            var totalReturning = growth.Sum(g => g.ReturningCustomers);
            var totalNew = growth.Sum(g => g.NewCustomers);
            if (totalReturning >= 4 && totalReturning > totalNew * 3)
                candidates.Add("Your traffic this week is mostly repeat customers — a loyalty points multiplier could reward that further.");
        }

        // --- Lifetime value ---
        if (totalCustomers >= 8 && avgLtv >= 500)
            candidates.Add($"Average customer lifetime value is strong (₹{avgLtv:0}) — a referral incentive could bring in more customers like them.");
        if (totalCustomers >= 8 && avgLtv is > 0 and < 150)
            candidates.Add($"Average spend per customer is on the lower side (₹{avgLtv:0}) — a combo or bundle offer could help raise it.");

        // --- Small/new cafe starter tips — most conditions above need enough customers to
        // be statistically meaningful; below that floor, give advice suited to a cafe still
        // building its base instead of falling straight through to the generic fallback.
        if (totalCustomers < 8)
        {
            candidates.Add(retentionRate == 0
                ? "No repeat customers yet — personally greeting regulars by name is the highest-leverage retention tool at this stage."
                : "Small but promising customer base — a quick WhatsApp thank-you to repeat visitors goes a long way before you're ready for automated campaigns.");
        }

        if (candidates.Count == 0)
            candidates.Add("Keep an eye on your At Risk segment as it grows — a timely win-back offer is usually the highest-return move for a cafe.");

        if (candidates.Count <= 2) return candidates;

        // Always lead with the single most important signal; rotate the second slot by
        // day-of-year among the rest — deterministic (same day = same pair every time it's
        // viewed) but not frozen on the same two forever the way a fixed top-2 would be.
        var secondIndex = 1 + DateTime.UtcNow.DayOfYear % (candidates.Count - 1);
        return [candidates[0], candidates[secondIndex]];
    }

    /// <summary>Exact-phone existing-vs-new check for POS/billing screens while a guest
    /// number is being entered. Not gated Plus+ like the CRM directory below — checkout
    /// must work on every plan tier (see class-level note above).</summary>
    [HttpGet("by-phone/{phone}")]
    public async Task<CustomerLookupDto> LookupByPhone(string phone)
    {
        var digits = new string(phone.Where(char.IsDigit).ToArray());
        var customer = await db.Customers.FirstOrDefaultAsync(c => c.Phone == digits);
        return customer is null ? new CustomerLookupDto(false, null, null) : new CustomerLookupDto(true, customer.Id, customer.Name);
    }

    [Authorize(Policy = Policies.RequirePlus)]
    [HttpGet]
    public async Task<PagedResult<CustomerSummaryDto>> List([FromQuery] string? search, [FromQuery] int page = 1, [FromQuery] int pageSize = 20)
    {
        var query = db.Customers.AsQueryable();
        if (!string.IsNullOrWhiteSpace(search))
            query = query.Where(c => c.Name.Contains(search));

        var paged = await query.OrderByDescending(c => c.LastVisitAt).ToPagedResultAsync(page, pageSize);

        var cutoff = DateTime.UtcNow.AddDays(-30);
        var customerIds = paged.Items.Select(c => c.Id).ToList();
        var visitCounts = await db.Orders
            .Where(o => o.CustomerId != null && customerIds.Contains(o.CustomerId.Value) && o.CreatedAt >= cutoff)
            .GroupBy(o => o.CustomerId!.Value)
            .Select(g => new { CustomerId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.CustomerId, g => g.Count);

        return new PagedResult<CustomerSummaryDto>(
            paged.Items.Select(c => CustomerSummaryDto.From(c, visitCounts.GetValueOrDefault(c.Id))).ToList(),
            paged.Page, paged.PageSize, paged.TotalCount);
    }

    /// <summary>Real count from Order rows, not a stored/reset counter — stays honest
    /// as the 30-day window rolls forward.</summary>
    private async Task<int> VisitsLast30DaysAsync(int customerId)
    {
        var cutoff = DateTime.UtcNow.AddDays(-30);
        return await db.Orders.CountAsync(o => o.CustomerId == customerId && o.CreatedAt >= cutoff);
    }

    [Authorize(Policy = Policies.RequirePlus)]
    [HttpGet("{id:int}")]
    public async Task<ActionResult<CustomerDetailDto>> Get(int id)
    {
        var customer = await db.Customers
            .Include(c => c.Coupons)
            .Include(c => c.GiftCards)
            .Include(c => c.FavoriteItems)
            .FirstOrDefaultAsync(c => c.Id == id);
        if (customer is null) return NotFound();

        var recentOrders = await db.Orders
            .Include(o => o.Items)
            .Where(o => o.CustomerId == id)
            .OrderByDescending(o => o.CreatedAt)
            .Take(20)
            .ToListAsync();

        var visits = recentOrders.Select(o => new VisitDto(
            // Cancelled lines are off the bill this visit's Total came from, so listing them
            // would tell the cafe the guest ate something they were never charged for.
            o.Id, $"#{1000 + o.Id}", o.CreatedAt, o.Total, (int)Math.Floor(o.Total), o.Items.Where(i => !i.Voided).Select(i => i.Name).ToList()));

        var favoriteMenuIds = customer.FavoriteItems.Select(f => f.MenuItemId).ToList();
        var favoriteMenu = await db.MenuItems.Where(m => favoriteMenuIds.Contains(m.Id)).ToDictionaryAsync(m => m.Id);

        return new CustomerDetailDto(
            CustomerSummaryDto.From(customer, await VisitsLast30DaysAsync(id)),
            customer.AddressLine1, customer.AddressCity, customer.AddressPincode, customer.DateOfBirth, customer.Notes,
            customer.ReferralCode, customer.TotalReferrals, customer.SuccessfulReferrals, customer.ReferralEarned,
            visits.ToList(),
            customer.Coupons.Select(CouponDto.From).ToList(),
            customer.GiftCards.Select(GiftCardDto.From).ToList(),
            customer.FavoriteItems
                .Where(f => favoriteMenu.ContainsKey(f.MenuItemId))
                .Select(f => new FavoriteItemDto(f.MenuItemId, favoriteMenu[f.MenuItemId].Name, favoriteMenu[f.MenuItemId].Price, f.OrderCount))
                .OrderByDescending(f => f.OrderCount)
                .ToList());
    }

    [Authorize(Policy = Policies.RequirePlus)]
    [HttpPost]
    public async Task<ActionResult<CustomerSummaryDto>> Create(CreateCustomerRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Name))
            throw new ApiValidationException("Name is required.");
        ValidateContactFields(req.Name, req.Email, req.Phone);

        var customer = new Customer
        {
            Name = req.Name.Trim(),
            Email = req.Email,
            Phone = req.Phone,
            DateOfBirth = req.DateOfBirth is null ? null : DateOnly.Parse(req.DateOfBirth),
            ReferralCode = GenerateReferralCode(req.Name),
        };
        db.Customers.Add(customer);
        await db.SaveChangesAsync();
        return CreatedAtAction(nameof(Get), new { id = customer.Id }, CustomerSummaryDto.From(customer));
    }

    [Authorize(Policy = Policies.RequirePlus)]
    [HttpPatch("{id:int}")]
    public async Task<ActionResult<CustomerSummaryDto>> Update(int id, UpdateCustomerRequest req)
    {
        var customer = await db.Customers.FindAsync(id);
        if (customer is null) return NotFound();

        if (req.Name is not null && string.IsNullOrWhiteSpace(req.Name))
            throw new ApiValidationException("Name cannot be blank.");
        ValidateContactFields(req.Name, req.Email, req.Phone);

        if (req.Name is not null) customer.Name = req.Name.Trim();
        if (req.Email is not null) customer.Email = req.Email;
        if (req.Phone is not null) customer.Phone = req.Phone;
        if (req.AddressLine1 is not null) customer.AddressLine1 = req.AddressLine1;
        if (req.AddressCity is not null) customer.AddressCity = req.AddressCity;
        if (req.AddressPincode is not null) customer.AddressPincode = req.AddressPincode;
        if (req.Notes is not null) customer.Notes = req.Notes;

        await db.SaveChangesAsync();
        return CustomerSummaryDto.From(customer, await VisitsLast30DaysAsync(id));
    }

    [Authorize(Policy = Policies.RequirePlus)]
    [HttpPost("{id:int}/points/redeem")]
    public async Task<ActionResult<CustomerSummaryDto>> RedeemPoints(int id, AdjustPointsRequest req)
    {
        var customer = await db.Customers.FindAsync(id);
        if (customer is null) return NotFound();
        if (req.Points <= 0) throw new ApiValidationException("Points must be positive.");
        if (req.Points > customer.AvailablePoints) throw new ApiValidationException("Not enough available points.");

        customer.RedeemedPoints += req.Points;
        await db.SaveChangesAsync();
        return CustomerSummaryDto.From(customer, await VisitsLast30DaysAsync(id));
    }

    [Authorize(Policy = Policies.RequirePlus)]
    [HttpPost("{id:int}/points/add")]
    public async Task<ActionResult<CustomerSummaryDto>> AddPoints(int id, AdjustPointsRequest req)
    {
        var customer = await db.Customers.FindAsync(id);
        if (customer is null) return NotFound();
        if (req.Points <= 0) throw new ApiValidationException("Points must be positive.");

        customer.TotalPoints += req.Points;
        await db.SaveChangesAsync();
        return CustomerSummaryDto.From(customer, await VisitsLast30DaysAsync(id));
    }

    // ---------- Coupons ----------

    [Authorize(Policy = Policies.RequirePlus)]
    [HttpPost("{id:int}/coupons")]
    public async Task<ActionResult<CouponDto>> IssueCoupon(int id, IssueCouponRequest req)
    {
        if (!await db.Customers.AnyAsync(c => c.Id == id)) return NotFound();
        if (string.IsNullOrWhiteSpace(req.Title)) throw new ApiValidationException("Title is required.");
        if (req.Value <= 0) throw new ApiValidationException("Value must be greater than zero.");
        if (req.MinOrderValue < 0) throw new ApiValidationException("Minimum order value cannot be negative.");
        if (req.ExpiresAt <= DateTime.UtcNow) throw new ApiValidationException("Expiry date must be in the future.");

        var coupon = new Coupon
        {
            CustomerId = id,
            Code = GenerateCode("CPN"),
            Title = req.Title,
            Description = req.Description,
            Type = req.Type,
            Value = req.Value,
            MinOrderValue = req.MinOrderValue,
            ExpiresAt = req.ExpiresAt,
        };
        db.Coupons.Add(coupon);
        await db.SaveChangesAsync();
        return CouponDto.From(coupon);
    }

    /// <summary>Validates a coupon code against an order subtotal — used by POS checkout.</summary>
    [HttpPost("coupons/apply")]
    public async Task<ActionResult<ApplyCouponResult>> ApplyCoupon(ApplyCouponRequest req)
    {
        var coupon = await db.Coupons.FirstOrDefaultAsync(c => c.Code == req.Code.ToUpperInvariant());
        if (coupon is null) return new ApplyCouponResult(false, "Coupon code is invalid or expired.", 0);
        if (coupon.IsUsed) return new ApplyCouponResult(false, "Coupon has already been used.", 0);
        if (coupon.ExpiresAt < DateTime.UtcNow) return new ApplyCouponResult(false, "Coupon has expired.", 0);
        if (req.OrderSubtotal < coupon.MinOrderValue)
            return new ApplyCouponResult(false, $"Minimum order value is {coupon.MinOrderValue:C}.", 0);

        var discount = coupon.Type switch
        {
            CouponType.Percent => Math.Round(req.OrderSubtotal * coupon.Value / 100, 2),
            CouponType.Flat => coupon.Value,
            _ => 0,
        };
        return new ApplyCouponResult(true, null, discount);
    }

    [HttpPost("coupons/{couponId:int}/redeem")]
    public async Task<IActionResult> RedeemCoupon(int couponId)
    {
        var coupon = await db.Coupons.FindAsync(couponId);
        if (coupon is null) return NotFound();
        if (coupon.IsUsed) throw new ApiConflictException("Coupon has already been used.");

        coupon.IsUsed = true;
        await db.SaveChangesAsync();
        return NoContent();
    }

    // ---------- Gift Cards ----------

    [Authorize(Policy = Policies.RequirePlus)]
    [HttpPost("gift-cards")]
    public async Task<ActionResult<GiftCardDto>> IssueGiftCard(IssueGiftCardRequest req)
    {
        if (req.Amount <= 0) throw new ApiValidationException("Amount must be greater than zero.");
        if (req.ValidDays <= 0) throw new ApiValidationException("Valid days must be greater than zero.");

        var card = new GiftCard
        {
            CustomerId = req.CustomerId,
            Code = GenerateCode("GC"),
            Balance = req.Amount,
            OriginalBalance = req.Amount,
            PurchasedBy = req.PurchasedBy,
            ExpiresAt = DateTime.UtcNow.AddDays(req.ValidDays),
        };
        db.GiftCards.Add(card);
        await db.SaveChangesAsync();
        return GiftCardDto.From(card);
    }

    /// <summary>Validates a gift card code without touching its balance — used by POS
    /// checkout the same way ApplyCoupon is. The real debit happens atomically at order
    /// creation in OrdersController.BuildOrderAsync, not here.</summary>
    [HttpPost("gift-cards/check")]
    public async Task<ActionResult<CheckGiftCardResult>> CheckGiftCard(CheckGiftCardRequest req)
    {
        var card = await db.GiftCards.FirstOrDefaultAsync(g => g.Code == req.Code.ToUpperInvariant());
        if (card is null) return new CheckGiftCardResult(false, "Gift card code not found.", 0);
        if (card.Status != GiftCardStatus.Active) return new CheckGiftCardResult(false, "Gift card is not active.", 0);
        if (card.ExpiresAt < DateTime.UtcNow) return new CheckGiftCardResult(false, "Gift card has expired.", 0);
        if (card.Balance <= 0) return new CheckGiftCardResult(false, "Gift card has no balance left.", 0);

        return new CheckGiftCardResult(true, null, card.Balance);
    }

    [HttpPost("gift-cards/redeem")]
    public async Task<ActionResult<GiftCardDto>> RedeemGiftCard(RedeemGiftCardRequest req)
    {
        var card = await db.GiftCards.FirstOrDefaultAsync(g => g.Code == req.Code.ToUpperInvariant());
        if (card is null) throw new ApiValidationException("Gift card code not found.");
        if (card.Status != GiftCardStatus.Active) throw new ApiConflictException("Gift card is not active.");
        if (card.ExpiresAt < DateTime.UtcNow) throw new ApiConflictException("Gift card has expired.");
        if (req.Amount <= 0 || req.Amount > card.Balance) throw new ApiValidationException("Invalid redeem amount.");

        card.Balance -= req.Amount;
        if (card.Balance == 0) card.Status = GiftCardStatus.Used;
        await db.SaveChangesAsync();
        return GiftCardDto.From(card);
    }

    private static string GenerateReferralCode(string name)
    {
        var slug = new string(name.Where(char.IsLetter).ToArray()).ToUpperInvariant();
        return $"{(slug.Length >= 4 ? slug[..4] : slug.PadRight(4, 'X'))}{Random.Shared.Next(100, 999)}";
    }

    private static string GenerateCode(string prefix) => $"{prefix}-{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}";
}
