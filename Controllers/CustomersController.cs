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
public class CustomersController(CafePosDbContext db, IGeminiService gemini) : ControllerBase
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
    /// SEGMENTS/NEW_DATA/RETURNING_DATA/REDEMPTION constants. Every number here is
    /// computed from real Customer/Order/Coupon rows; only the closing "Suggestion"
    /// line is Gemini-generated (and only as a plain-English phrasing of the real
    /// segment numbers above it — the model is given the real figures, not asked to
    /// invent any).
    /// </summary>
    [Authorize(Policy = Policies.RequirePlus)]
    [HttpGet("insights")]
    public async Task<CrmInsightsDto> Insights()
    {
        var customers = await db.Customers.ToListAsync();
        var totalCustomers = customers.Count;
        var retentionRate = totalCustomers > 0 ? customers.Count(c => c.VisitCount >= 2) * 100.0 / totalCustomers : 0;
        var avgLtv = totalCustomers > 0 ? customers.Average(c => c.TotalSpent) : 0;

        var weekStart = DateTime.UtcNow.Date.AddDays(-6);
        var recentOrders = await db.Orders
            .Where(o => o.CreatedAt >= weekStart && o.CustomerId != null)
            .Select(o => new { o.CreatedAt, o.CustomerId })
            .ToListAsync();
        var customerJoinDates = customers.ToDictionary(c => c.Id, c => c.JoinedAt.Date);

        var growth = Enumerable.Range(0, 7).Select(offset =>
        {
            var day = weekStart.AddDays(offset);
            var dayCustomerIds = recentOrders.Where(o => o.CreatedAt.Date == day).Select(o => o.CustomerId!.Value).Distinct().ToList();
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

        string? suggestion = null;
        if (gemini.IsConfigured && segments.Any(s => s.CustomerCount > 0))
        {
            try
            {
                var segmentSummary = string.Join("; ", segments.Where(s => s.CustomerCount > 0).Select(s => $"{s.Name}: {s.CustomerCount} customers, avg lifetime spend Rs.{s.AvgSpent:0}"));
                var prompt = $"A cafe's real customer segments right now: {segmentSummary}. Overall retention rate: {retentionRate:0}%. " +
                    "In one short sentence (under 30 words), suggest one specific, actionable promotion targeting whichever segment is most worth focusing on. " +
                    "No preamble, no markdown — just the suggestion itself.";
                suggestion = await gemini.GenerateAsync(prompt);
            }
            catch
            {
                suggestion = null; // the suggestion is a bonus — every number above it is real regardless of whether this call succeeds
            }
        }

        return new CrmInsightsDto(Math.Round(retentionRate, 1), Math.Round(avgLtv, 2), growth, redemptionDtos, segments, suggestion);
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
            o.Id, $"#{1000 + o.Id}", o.CreatedAt, o.Total, (int)Math.Floor(o.Total), o.Items.Select(i => i.Name).ToList()));

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
