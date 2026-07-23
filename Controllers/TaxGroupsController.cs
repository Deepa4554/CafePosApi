using CafePOS.Api.Contracts;
using CafePOS.Api.Data;
using CafePOS.Api.Domain;
using CafePOS.Api.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CafePOS.Api.Controllers;

/// <summary>Managed per-tenant tax slabs (e.g. "GST 5%", "GST 12%") that menu items are
/// assigned to, so one bill can carry more than one rate. See <see cref="TaxGroup"/> for how
/// a line's rate resolves and why it's snapshotted onto the order.
///
/// A cafe that never creates a group keeps billing everything at CafeSettings.TaxRatePct,
/// exactly as before — nothing here is required to place an order.</summary>
[ApiController]
[Route("api/tax-groups")]
[Authorize]
public class TaxGroupsController(CafePosDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<IEnumerable<TaxGroupDto>> List() =>
        await db.TaxGroups.OrderBy(t => t.RatePct).ThenBy(t => t.Name)
            .Select(t => new TaxGroupDto(t.Id, t.Name, t.RatePct, t.IsDefault))
            .ToListAsync();

    [Authorize(Policy = Policies.OwnerOrManager)]
    [HttpPost]
    public async Task<ActionResult<TaxGroupDto>> Create(CreateTaxGroupRequest req)
    {
        var name = req.Name?.Trim();
        if (string.IsNullOrWhiteSpace(name))
            throw new ApiValidationException("Tax group name is required.");
        if (name.Length > 50)
            throw new ApiValidationException("Tax group name cannot exceed 50 characters.");
        if (req.RatePct is < 0 or > 100)
            throw new ApiValidationException("Tax rate must be between 0 and 100.");

        var duplicate = await db.TaxGroups.AnyAsync(t => t.Name.ToLower() == name.ToLower());
        if (duplicate)
            throw new ApiValidationException("A tax group with this name already exists.");

        var group = new TaxGroup { Name = name, RatePct = req.RatePct, IsDefault = req.IsDefault };
        db.TaxGroups.Add(group);
        if (req.IsDefault) await ClearOtherDefaultsAsync(exceptId: null);
        await db.SaveChangesAsync();
        return CreatedAtAction(nameof(List), TaxGroupDto.From(group));
    }

    [Authorize(Policy = Policies.OwnerOrManager)]
    [HttpPatch("{id:int}")]
    public async Task<ActionResult<TaxGroupDto>> Update(int id, UpdateTaxGroupRequest req)
    {
        var group = await db.TaxGroups.FirstOrDefaultAsync(t => t.Id == id);
        if (group is null) return NotFound();

        if (req.Name is not null)
        {
            var name = req.Name.Trim();
            if (string.IsNullOrWhiteSpace(name))
                throw new ApiValidationException("Tax group name is required.");
            if (name.Length > 50)
                throw new ApiValidationException("Tax group name cannot exceed 50 characters.");
            var duplicate = await db.TaxGroups.AnyAsync(t => t.Id != id && t.Name.ToLower() == name.ToLower());
            if (duplicate)
                throw new ApiValidationException("A tax group with this name already exists.");
            group.Name = name;
        }

        if (req.RatePct is not null)
        {
            if (req.RatePct is < 0 or > 100)
                throw new ApiValidationException("Tax rate must be between 0 and 100.");
            // Only future orders move — every already-placed line kept its own snapshot of
            // the old rate (OrderItem.TaxRatePct), so past bills stay exactly as printed.
            group.RatePct = req.RatePct.Value;
        }

        if (req.IsDefault is not null)
        {
            group.IsDefault = req.IsDefault.Value;
            if (group.IsDefault) await ClearOtherDefaultsAsync(exceptId: id);
        }

        await db.SaveChangesAsync();
        return TaxGroupDto.From(group);
    }

    [Authorize(Policy = Policies.OwnerOrManager)]
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        var group = await db.TaxGroups.FirstOrDefaultAsync(t => t.Id == id);
        if (group is null) return NotFound();

        // Items pointing here fall back to the default group (or CafeSettings.TaxRatePct)
        // from their next order onwards. Deliberately allowed rather than blocked: past
        // orders are unaffected either way because they snapshotted their rate.
        var assigned = await db.MenuItems.Where(m => m.TaxGroupId == id).ToListAsync();
        foreach (var item in assigned) item.TaxGroupId = null;

        db.TaxGroups.Remove(group);
        await db.SaveChangesAsync();
        return NoContent();
    }

    /// <summary>At most one default per tenant — setting a new one demotes the old.</summary>
    private async Task ClearOtherDefaultsAsync(int? exceptId)
    {
        var others = await db.TaxGroups
            .Where(t => t.IsDefault && (exceptId == null || t.Id != exceptId))
            .ToListAsync();
        foreach (var other in others) other.IsDefault = false;
    }
}
