using CafePOS.Api.Contracts;
using CafePOS.Api.Data;
using CafePOS.Api.Domain;
using CafePOS.Api.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CafePOS.Api.Controllers;

/// <summary>The Points screen's "Reward Catalog" — a cafe-owned list of what its own
/// loyalty points redeem for, replacing the old hardcoded 4-item constant
/// (Free Latte/Pastry Box/10% Off/Buy 1 Get 1) that every tenant saw identically
/// regardless of what they actually sell.</summary>
[ApiController]
[Route("api/rewards")]
[Authorize(Policy = Policies.RequirePlus)]
public class RewardsController(CafePosDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<IEnumerable<RewardDto>> List([FromQuery] bool includeInactive = false)
    {
        var query = db.Rewards.AsQueryable();
        if (!includeInactive) query = query.Where(r => r.IsActive);
        var rewards = await query.OrderBy(r => r.PointsCost).ToListAsync();
        return rewards.Select(RewardDto.From);
    }

    [Authorize(Policy = Policies.OwnerOrManager)]
    [HttpPost]
    public async Task<ActionResult<RewardDto>> Create(CreateRewardRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Name))
            throw new ApiValidationException("Enter a name for this reward.");
        if (req.PointsCost <= 0)
            throw new ApiValidationException("Points cost must be greater than 0.");

        var reward = new Reward
        {
            Name = req.Name.Trim(),
            PointsCost = req.PointsCost,
            Icon = string.IsNullOrWhiteSpace(req.Icon) ? "gift-outline" : req.Icon.Trim(),
        };
        db.Rewards.Add(reward);
        await db.SaveChangesAsync();
        return RewardDto.From(reward);
    }

    [Authorize(Policy = Policies.OwnerOrManager)]
    [HttpPatch("{id:int}")]
    public async Task<ActionResult<RewardDto>> Update(int id, UpdateRewardRequest req)
    {
        var reward = await db.Rewards.FindAsync(id);
        if (reward is null) return NotFound();

        if (req.Name is not null)
        {
            if (string.IsNullOrWhiteSpace(req.Name))
                throw new ApiValidationException("Name cannot be empty.");
            reward.Name = req.Name.Trim();
        }
        if (req.PointsCost is not null)
        {
            if (req.PointsCost <= 0)
                throw new ApiValidationException("Points cost must be greater than 0.");
            reward.PointsCost = req.PointsCost.Value;
        }
        if (req.Icon is not null) reward.Icon = req.Icon.Trim();
        if (req.IsActive is not null) reward.IsActive = req.IsActive.Value;

        await db.SaveChangesAsync();
        return RewardDto.From(reward);
    }

    /// <summary>Soft-delete (IsActive = false) rather than a hard row delete — a reward
    /// already referenced by past redemption history stays intact instead of leaving a
    /// dangling reference; List() hides it by default via includeInactive.</summary>
    [Authorize(Policy = Policies.OwnerOrManager)]
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        var reward = await db.Rewards.FindAsync(id);
        if (reward is null) return NotFound();
        reward.IsActive = false;
        await db.SaveChangesAsync();
        return NoContent();
    }
}
