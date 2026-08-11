using CafePOS.Api.Contracts;
using CafePOS.Api.Data;
using CafePOS.Api.Domain;
using CafePOS.Api.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CafePOS.Api.Controllers;

/// <summary>Rule-driven promotions — BOGO, happy hour, category and item discounts — priced by
/// <see cref="OfferEngine"/> rather than redeemed by code like a <see cref="Coupon"/>.
///
/// The preview endpoint is the point of the whole screen: an owner can watch what an offer does
/// to a sample bill while still typing it, instead of saving it, walking to the till, punching a
/// test order, finding it wrong and starting over.</summary>
[ApiController]
[Route("api/offers")]
[Authorize(Policy = Policies.RequirePlus)]
public class OffersController(CafePosDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<IEnumerable<OfferDto>> List([FromQuery] bool includeInactive = false)
    {
        var query = db.Offers.Include(o => o.Items).AsQueryable();
        if (!includeInactive) query = query.Where(o => o.IsActive);
        var offers = await query.OrderByDescending(o => o.Id).ToListAsync();
        return offers.Select(OfferDto.From);
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<OfferDto>> Get(int id)
    {
        var offer = await db.Offers.Include(o => o.Items).FirstOrDefaultAsync(o => o.Id == id);
        return offer is null ? NotFound() : OfferDto.From(offer);
    }

    [Authorize(Policy = Policies.OwnerOrManager)]
    [HttpPost]
    public async Task<ActionResult<OfferDto>> Create(CreateOfferRequest req)
    {
        var offer = new Offer
        {
            Title = (req.Title ?? "").Trim(),
            Type = req.Type,
            Scope = req.Scope,
            CategoryName = req.Scope == OfferScope.Category ? req.CategoryName?.Trim() : null,
            Value = req.Value,
            MaxDiscountAmount = req.MaxDiscountAmount,
            BuyQty = req.BuyQty,
            GetQty = req.GetQty,
            MinOrderValue = req.MinOrderValue,
            MaxApplicationsPerBill = req.MaxApplicationsPerBill,
            Stackable = req.Stackable,
            StartsAtUtc = req.StartsAtUtc,
            EndsAtUtc = req.EndsAtUtc,
            DaysOfWeek = OfferDays.ToCsv(req.DaysOfWeek),
            StartTime = req.StartTime,
            EndTime = req.EndTime,
            AutoApply = req.AutoApply,
        };

        if (req.Scope == OfferScope.SpecificItems)
            offer.Items = [.. (req.MenuItemIds ?? []).Distinct().Select(id => new OfferMenuItem { MenuItemId = id })];

        Validate(offer);
        await ValidateScopeTargetsExistAsync(offer);

        db.Offers.Add(offer);
        await db.SaveChangesAsync();
        return OfferDto.From(offer);
    }

    [Authorize(Policy = Policies.OwnerOrManager)]
    [HttpPatch("{id:int}")]
    public async Task<ActionResult<OfferDto>> Update(int id, UpdateOfferRequest req)
    {
        var offer = await db.Offers.Include(o => o.Items).FirstOrDefaultAsync(o => o.Id == id);
        if (offer is null) return NotFound();

        if (req.Title is not null) offer.Title = req.Title.Trim();
        if (req.Type is not null) offer.Type = req.Type.Value;
        if (req.Scope is not null) offer.Scope = req.Scope.Value;
        if (req.CategoryName is not null) offer.CategoryName = req.CategoryName.Trim();
        if (req.Value is not null) offer.Value = req.Value.Value;
        if (req.MaxDiscountAmount is not null) offer.MaxDiscountAmount = req.MaxDiscountAmount.Value;
        if (req.BuyQty is not null) offer.BuyQty = req.BuyQty.Value;
        if (req.GetQty is not null) offer.GetQty = req.GetQty.Value;
        if (req.MinOrderValue is not null) offer.MinOrderValue = req.MinOrderValue.Value;
        if (req.MaxApplicationsPerBill is not null) offer.MaxApplicationsPerBill = req.MaxApplicationsPerBill.Value;
        if (req.Stackable is not null) offer.Stackable = req.Stackable.Value;
        if (req.StartsAtUtc is not null) offer.StartsAtUtc = req.StartsAtUtc;
        if (req.EndsAtUtc is not null) offer.EndsAtUtc = req.EndsAtUtc;
        if (req.DaysOfWeek is not null) offer.DaysOfWeek = OfferDays.ToCsv(req.DaysOfWeek);
        if (req.StartTime is not null) offer.StartTime = req.StartTime;
        if (req.EndTime is not null) offer.EndTime = req.EndTime;
        if (req.AutoApply is not null) offer.AutoApply = req.AutoApply.Value;
        if (req.IsActive is not null) offer.IsActive = req.IsActive.Value;

        if (req.MenuItemIds is not null)
        {
            db.OfferMenuItems.RemoveRange(offer.Items);
            offer.Items = [.. req.MenuItemIds.Distinct().Select(mid => new OfferMenuItem { MenuItemId = mid, OfferId = offer.Id })];
        }

        // A scope the offer no longer uses must not keep a stale target around — an offer
        // switched from Category to EntireBill that still carried a CategoryName would read as
        // category-scoped the next time someone opened it.
        if (offer.Scope != OfferScope.Category) offer.CategoryName = null;
        if (offer.Scope != OfferScope.SpecificItems && offer.Items.Count > 0)
        {
            db.OfferMenuItems.RemoveRange(offer.Items);
            offer.Items = [];
        }

        Validate(offer);
        await ValidateScopeTargetsExistAsync(offer);

        await db.SaveChangesAsync();
        return OfferDto.From(offer);
    }

    /// <summary>Soft-delete, same as Reward: an offer that already priced past bills stays on
    /// the row so a reprinted receipt can still name what took the money off.</summary>
    [Authorize(Policy = Policies.OwnerOrManager)]
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        var offer = await db.Offers.FindAsync(id);
        if (offer is null) return NotFound();
        offer.IsActive = false;
        await db.SaveChangesAsync();
        return NoContent();
    }

    /// <summary>Prices a cart. With a Draft, only that unsaved offer is evaluated and its
    /// validity window is ignored, so the setup screen shows what the offer WILL do rather than
    /// a discouraging ₹0 because a happy hour starts at four. Without one, every live offer runs
    /// — the POS cart banner.</summary>
    [HttpPost("preview")]
    public async Task<ActionResult<OfferPreviewResult>> Preview(OfferPreviewRequest req)
    {
        var lines = (req.Lines ?? [])
            .Select(l => new OfferCartLine(l.LineKey, l.MenuItemId, l.CategoryName, l.Name ?? "", l.UnitPrice, l.Qty))
            .ToList();

        List<Offer> offers;
        if (req.Draft is not null)
        {
            var draft = new Offer
            {
                Id = 0,
                Title = string.IsNullOrWhiteSpace(req.Draft.Title) ? "This offer" : req.Draft.Title.Trim(),
                Type = req.Draft.Type,
                Scope = req.Draft.Scope,
                CategoryName = req.Draft.CategoryName,
                Value = req.Draft.Value,
                MaxDiscountAmount = req.Draft.MaxDiscountAmount,
                BuyQty = req.Draft.BuyQty,
                GetQty = req.Draft.GetQty,
                MinOrderValue = req.Draft.MinOrderValue,
                MaxApplicationsPerBill = req.Draft.MaxApplicationsPerBill,
                Stackable = req.Draft.Stackable,
                // Validity deliberately left unset — see the summary.
                AutoApply = true,
                IsActive = true,
                Items = [.. (req.Draft.MenuItemIds ?? []).Distinct().Select(id => new OfferMenuItem { MenuItemId = id })],
            };
            offers = [draft];
        }
        else
        {
            offers = await db.Offers.Include(o => o.Items).Where(o => o.IsActive).ToListAsync();
        }

        return OfferPreviewResult.From(OfferEngine.Evaluate(lines, offers, DateTime.UtcNow));
    }

    // ---------- Validation ----------

    /// <summary>Runs against the resolved entity rather than the request so a PATCH that changes
    /// one field is checked against the offer it actually produces.</summary>
    private static void Validate(Offer o)
    {
        if (string.IsNullOrWhiteSpace(o.Title))
            throw new ApiValidationException("Give this offer a name.");

        switch (o.Type)
        {
            case OfferType.Percentage when o.Value is <= 0 or > 100:
                throw new ApiValidationException("Percentage must be between 1 and 100.");
            case OfferType.Flat when o.Value <= 0:
                throw new ApiValidationException("Enter the amount to take off.");
            case OfferType.BuyXGetY when o.BuyQty < 1:
                throw new ApiValidationException("Buy quantity must be at least 1.");
            case OfferType.BuyXGetY when o.GetQty < 1:
                throw new ApiValidationException("Free quantity must be at least 1.");
        }

        if (o.Scope == OfferScope.Category && string.IsNullOrWhiteSpace(o.CategoryName))
            throw new ApiValidationException("Pick the category this offer applies to.");
        if (o.Scope == OfferScope.SpecificItems && o.Items.Count == 0)
            throw new ApiValidationException("Pick at least one item this offer applies to.");

        if (o.MinOrderValue < 0) throw new ApiValidationException("Minimum order value cannot be negative.");
        if (o.MaxDiscountAmount < 0) throw new ApiValidationException("Maximum discount cannot be negative.");
        if (o.MaxApplicationsPerBill < 0) throw new ApiValidationException("Repeat limit cannot be negative.");

        if (o.StartsAtUtc is { } from && o.EndsAtUtc is { } to && from >= to)
            throw new ApiValidationException("The offer's end date must be after its start date.");

        // One half of a happy-hour window is not a window; silently treating it as all-day would
        // hand the cafe a round-the-clock discount it did not ask for.
        if (o.StartTime is null != o.EndTime is null)
            throw new ApiValidationException("Set both a start and an end time, or neither.");
    }

    /// <summary>Rejects a scope pointing at a category or item that does not exist for this
    /// tenant. Without this the offer saves cleanly and then quietly never fires, which reads to
    /// the owner as the feature being broken.</summary>
    private async Task ValidateScopeTargetsExistAsync(Offer o)
    {
        if (o.Scope == OfferScope.Category && !string.IsNullOrWhiteSpace(o.CategoryName))
        {
            if (!await db.MenuCategories.AnyAsync(c => c.Name == o.CategoryName))
                throw new ApiValidationException("That category no longer exists.");
        }

        if (o.Scope == OfferScope.SpecificItems && o.Items.Count > 0)
        {
            var wanted = o.Items.Select(i => i.MenuItemId).ToList();
            var found = await db.MenuItems.Where(m => wanted.Contains(m.Id)).CountAsync();
            if (found != wanted.Count)
                throw new ApiValidationException("One or more of the selected items no longer exists.");
        }
    }
}
