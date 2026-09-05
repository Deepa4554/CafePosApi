using CafePOS.Api.Contracts;
using CafePOS.Api.Data;
using CafePOS.Api.Domain;
using CafePOS.Api.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CafePOS.Api.Controllers;

/// <summary>Owner-configured lifetime-points milestones — "cross N points, the next bill
/// gets X% off" (see LoyaltyMilestone, OrdersController.ApplyBillMilestone). Same shape as
/// RewardsController, one row per tier the Owner has set up.</summary>
[ApiController]
[Route("api/loyalty-milestones")]
[Authorize(Policy = Policies.RequirePlus)]
public class LoyaltyMilestonesController(CafePosDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<IEnumerable<LoyaltyMilestoneDto>> List([FromQuery] bool includeInactive = false)
    {
        var query = db.LoyaltyMilestones.AsQueryable();
        if (!includeInactive) query = query.Where(m => m.IsActive);
        var milestones = await query.OrderBy(m => m.ThresholdPoints).ToListAsync();
        return milestones.Select(LoyaltyMilestoneDto.From);
    }

    [Authorize(Policy = Policies.OwnerOrManager)]
    [HttpPost]
    public async Task<ActionResult<LoyaltyMilestoneDto>> Create(CreateLoyaltyMilestoneRequest req)
    {
        if (req.ThresholdPoints <= 0)
            throw new ApiValidationException("Threshold must be greater than 0 points.");
        if (req.DiscountPct is <= 0 or > 100)
            throw new ApiValidationException("Discount must be between 0 and 100 percent.");
        if (await db.LoyaltyMilestones.AnyAsync(m => m.ThresholdPoints == req.ThresholdPoints))
            throw new ApiValidationException("A milestone at this threshold already exists.");

        var milestone = new LoyaltyMilestone
        {
            ThresholdPoints = req.ThresholdPoints,
            DiscountPct = req.DiscountPct,
        };
        db.LoyaltyMilestones.Add(milestone);
        await db.SaveChangesAsync();
        return LoyaltyMilestoneDto.From(milestone);
    }

    [Authorize(Policy = Policies.OwnerOrManager)]
    [HttpPatch("{id:int}")]
    public async Task<ActionResult<LoyaltyMilestoneDto>> Update(int id, UpdateLoyaltyMilestoneRequest req)
    {
        var milestone = await db.LoyaltyMilestones.FindAsync(id);
        if (milestone is null) return NotFound();

        if (req.ThresholdPoints is not null)
        {
            if (req.ThresholdPoints <= 0)
                throw new ApiValidationException("Threshold must be greater than 0 points.");
            if (await db.LoyaltyMilestones.AnyAsync(m => m.Id != id && m.ThresholdPoints == req.ThresholdPoints))
                throw new ApiValidationException("A milestone at this threshold already exists.");
            milestone.ThresholdPoints = req.ThresholdPoints.Value;
        }
        if (req.DiscountPct is not null)
        {
            if (req.DiscountPct is <= 0 or > 100)
                throw new ApiValidationException("Discount must be between 0 and 100 percent.");
            milestone.DiscountPct = req.DiscountPct.Value;
        }
        if (req.IsActive is not null) milestone.IsActive = req.IsActive.Value;

        await db.SaveChangesAsync();
        return LoyaltyMilestoneDto.From(milestone);
    }

    /// <summary>Soft-delete (IsActive = false), matching Reward — a past order's
    /// MilestoneThresholdApplied stays meaningful even after the tier is retired.</summary>
    [Authorize(Policy = Policies.OwnerOrManager)]
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        var milestone = await db.LoyaltyMilestones.FindAsync(id);
        if (milestone is null) return NotFound();
        milestone.IsActive = false;
        await db.SaveChangesAsync();
        return NoContent();
    }
}
