using CafePOS.Api.Contracts;
using CafePOS.Api.Data;
using CafePOS.Api.Domain;
using CafePOS.Api.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CafePOS.Api.Controllers;

/// <summary>Managed per-tenant kitchen prep stations (e.g. "Main Kitchen", "Bar", "Dessert")
/// — replaces the old free-text MenuItem.KitchenStation field. Drives KDS station filtering
/// and per-station KOT print routing (see MenuItem.StationId, OrderItem.StationName).</summary>
[ApiController]
[Route("api/stations")]
[Authorize]
public class StationsController(CafePosDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<IEnumerable<StationDto>> List() =>
        await db.Stations.OrderBy(s => s.SortOrder).ThenBy(s => s.Name)
            .Select(s => new StationDto(s.Id, s.Name, s.Icon, s.SortOrder, s.Active))
            .ToListAsync();

    [Authorize(Policy = Policies.OwnerOrManager)]
    [HttpPost]
    public async Task<ActionResult<StationDto>> Create(CreateStationRequest req)
    {
        var name = req.Name?.Trim();
        if (string.IsNullOrWhiteSpace(name))
            throw new ApiValidationException("Station name is required.");
        if (name.Length > 50)
            throw new ApiValidationException("Station name cannot exceed 50 characters.");

        var existing = await db.Stations.FirstOrDefaultAsync(s => s.Name.ToLower() == name.ToLower());
        if (existing is not null)
            throw new ApiValidationException("A station with this name already exists.");

        var maxSortOrder = await db.Stations.Select(s => (int?)s.SortOrder).MaxAsync() ?? -1;
        var station = new Station { Name = name, Icon = req.Icon ?? "chef-hat", SortOrder = maxSortOrder + 1 };
        db.Stations.Add(station);
        await db.SaveChangesAsync();
        return CreatedAtAction(nameof(List), StationDto.From(station));
    }

    [Authorize(Policy = Policies.OwnerOrManager)]
    [HttpPatch("{id:int}")]
    public async Task<ActionResult<StationDto>> Update(int id, UpdateStationRequest req)
    {
        var station = await db.Stations.FindAsync(id);
        if (station is null) return NotFound();

        if (req.Name is not null)
        {
            var name = req.Name.Trim();
            if (string.IsNullOrWhiteSpace(name)) throw new ApiValidationException("Station name cannot be blank.");
            if (name.Length > 50) throw new ApiValidationException("Station name cannot exceed 50 characters.");
            var existing = await db.Stations.FirstOrDefaultAsync(s => s.Name.ToLower() == name.ToLower() && s.Id != id);
            if (existing is not null) throw new ApiValidationException("A station with this name already exists.");
            station.Name = name;
        }
        if (req.Icon is not null) station.Icon = req.Icon;
        if (req.SortOrder is not null) station.SortOrder = req.SortOrder.Value;
        if (req.Active is not null) station.Active = req.Active.Value;

        await db.SaveChangesAsync();
        return StationDto.From(station);
    }

    /// <summary>Blocked while any MenuItem still references this station — must reassign
    /// those items (or deactivate the station instead, which hides it from pickers without
    /// breaking history/FK integrity) before it can be removed outright.</summary>
    [Authorize(Policy = Policies.OwnerOrManager)]
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        var station = await db.Stations.FindAsync(id);
        if (station is null) return NotFound();

        var inUse = await db.MenuItems.AnyAsync(m => m.StationId == id);
        if (inUse)
            throw new ApiConflictException("This station still has menu items assigned to it — reassign them first, or deactivate the station instead.");

        db.Stations.Remove(station);
        await db.SaveChangesAsync();
        return NoContent();
    }
}
