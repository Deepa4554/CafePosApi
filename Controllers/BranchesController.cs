using CafePOS.Api.Contracts;
using CafePOS.Api.Data;
using CafePOS.Api.Domain;
using CafePOS.Api.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CafePOS.Api.Controllers;

[ApiController]
[Route("api/branches")]
[Authorize(Policy = Policies.OwnerOrManager)]
[Authorize(Policy = Policies.RequirePlus)]
public class BranchesController(CafePosDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<IEnumerable<BranchDto>> List()
    {
        var branches = await db.Branches.OrderBy(b => b.Name).ToListAsync();
        return branches.Select(BranchDto.From);
    }

    [HttpPost]
    public async Task<ActionResult<BranchDto>> Create(CreateBranchRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Name)) throw new ApiValidationException("Name is required.");
        if (string.IsNullOrWhiteSpace(req.Address)) throw new ApiValidationException("Address is required.");

        var branch = new Branch { Name = req.Name.Trim(), Address = req.Address.Trim() };
        db.Branches.Add(branch);

        var sub = await db.Subscriptions.FirstAsync();
        var limits = SubscriptionDto.PlanLimits[sub.Plan];
        var currentCount = await db.Branches.CountAsync();
        if (currentCount + 1 > limits.branches)
            throw new ApiConflictException($"Your {sub.Plan} plan allows up to {limits.branches} branches. Upgrade to add more.");

        await db.SaveChangesAsync();
        return CreatedAtAction(nameof(List), BranchDto.From(branch));
    }

    [HttpPatch("{id:int}/deactivate")]
    public async Task<IActionResult> Deactivate(int id)
    {
        var branch = await db.Branches.FindAsync(id);
        if (branch is null) return NotFound();
        branch.IsActive = false;
        await db.SaveChangesAsync();
        return NoContent();
    }
}
