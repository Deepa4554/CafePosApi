using CafePOS.Api.Contracts;
using CafePOS.Api.Data;
using CafePOS.Api.Domain;
using CafePOS.Api.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CafePOS.Api.Controllers;

[ApiController]
[Route("api/menu-items")]
public class MenuController(CafePosDbContext db, IImageStorageService imageStorage) : ControllerBase
{
    /// <summary>Public — powers the customer-facing QR Menu as well as the internal POS grid.</summary>
    [AllowAnonymous]
    [HttpGet]
    public async Task<IEnumerable<MenuItem>> List() =>
        await db.MenuItems.OrderBy(m => m.Category).ThenBy(m => m.Name).ToListAsync();

    private const int BestSellerCount = 3;

    /// <summary>
    /// Ranks menu items by units actually sold — not the manually-set Popular flag, so it
    /// stays honest as real demand shifts.
    /// period=month (default): last 30 days, top 3. A brand-new cafe with too little order
    /// history (fewer than 3 items with any sales) gets the remaining slots backfilled from
    /// Popular-flagged items (UnitsSold 0) so the section never renders empty on day one.
    /// period=today: midnight UTC through now, top 1 — "today's most-selling item so far".
    /// No Popular backfill here: an empty list before the first sale of the day is the
    /// correct answer, not a gap to paper over.
    /// </summary>
    [AllowAnonymous]
    [HttpGet("best-sellers")]
    public async Task<IEnumerable<BestSellerDto>> BestSellers([FromQuery] string period = "month")
    {
        var isToday = period.Equals("today", StringComparison.OrdinalIgnoreCase);
        var cutoff = isToday ? DateTime.UtcNow.Date : DateTime.UtcNow.AddDays(-30);
        var take = isToday ? 1 : BestSellerCount;

        var sales = await (
            from oi in db.OrderItems
            join o in db.Orders on oi.OrderId equals o.Id
            where o.CreatedAt >= cutoff
            group oi.Qty by oi.MenuItemId into g
            select new { MenuItemId = g.Key, UnitsSold = g.Sum() })
            .OrderByDescending(x => x.UnitsSold)
            .Take(take)
            .ToListAsync();

        var menuById = await db.MenuItems.Where(m => sales.Select(s => s.MenuItemId).Contains(m.Id))
            .ToDictionaryAsync(m => m.Id);

        var results = sales
            .Where(s => menuById.ContainsKey(s.MenuItemId))
            .Select(s => ToDto(menuById[s.MenuItemId], s.UnitsSold))
            .ToList();

        if (!isToday && results.Count < take)
        {
            var usedIds = results.Select(r => r.Id).ToHashSet();
            var fallback = await db.MenuItems.Where(m => m.Popular && !usedIds.Contains(m.Id))
                .Take(take - results.Count).ToListAsync();
            results.AddRange(fallback.Select(m => ToDto(m, 0)));
        }

        return results;
    }

    private static BestSellerDto ToDto(MenuItem m, int unitsSold) =>
        new(m.Id, m.Name, m.Category, m.Price, m.Icon, m.Image, m.Subtitle, m.Available, m.Popular, unitsSold);

    [Authorize(Policy = Policies.OwnerOrManager)]
    [HttpPost]
    public async Task<ActionResult<MenuItem>> Create(CreateMenuItemRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Name))
            throw new ApiValidationException("Name is required.");
        if (req.Name.Trim().Length > 200)
            throw new ApiValidationException("Name cannot exceed 200 characters.");
        if (req.Price < 0)
            throw new ApiValidationException("Price cannot be negative.");
        if (!string.IsNullOrWhiteSpace(req.ShortCode) && req.ShortCode.Length > 5)
            throw new ApiValidationException("Short code cannot exceed 5 characters.");
        if (!string.IsNullOrWhiteSpace(req.ShortCode))
        {
            var existing = await db.MenuItems
                .FirstOrDefaultAsync(m => m.ShortCode == req.ShortCode.ToUpper());
            if (existing is not null)
                throw new ApiValidationException("This short code is already in use.");
        }

        var imageUrl = await imageStorage.ResolveAsync("menu-items", req.Image);
        var item = new MenuItem
        {
            Name = req.Name.Trim(),
            Category = string.IsNullOrWhiteSpace(req.Category) ? "Food" : req.Category.Trim(),
            Price = req.Price,
            Icon = req.Icon ?? "silverware-fork-knife",
            Subtitle = req.Subtitle ?? "",
            Image = imageUrl ?? "",
            Description = req.Description,
            ProductType = req.ProductType ?? ProductType.Prepared,
            LinkedInventoryItemId = req.ProductType == ProductType.Independent ? req.LinkedInventoryItemId : null,
            ShortCode = string.IsNullOrWhiteSpace(req.ShortCode) ? null : req.ShortCode.ToUpper(),
            KitchenStation = req.KitchenStation ?? "KITCHEN",
        };
        if (Enum.TryParse<ItemType>(req.ItemType ?? "Recipe", ignoreCase: true, out var itemType))
            item.ItemType = itemType;
        if (!string.IsNullOrWhiteSpace(req.VegNonVegType) && Enum.TryParse<VegNonVegType>(req.VegNonVegType, ignoreCase: true, out var vegType))
            item.VegNonVegType = vegType;
        db.MenuItems.Add(item);
        await db.SaveChangesAsync();
        return CreatedAtAction(nameof(List), new { id = item.Id }, item);
    }

    /// <summary>
    /// Powers CSV import (onboarding "Import from CSV" and any future bulk-add flow).
    /// Rows missing a name or with a negative/zero price are silently skipped rather
    /// than failing the whole import — a CSV commonly has a stray header/blank row.
    /// </summary>
    [Authorize(Policy = Policies.OwnerOrManager)]
    [HttpPost("bulk")]
    public async Task<ActionResult<BulkImportResultDto>> BulkCreate(List<CreateMenuItemRequest> items)
    {
        if (items is null || items.Count == 0)
            throw new ApiValidationException("No items to import.");
        if (items.Count > 500)
            throw new ApiValidationException("Cannot import more than 500 items at once.");

        var valid = items.Where(req => !string.IsNullOrWhiteSpace(req.Name) && req.Price > 0).ToList();
        var created = new List<MenuItem>();
        foreach (var req in valid)
        {
            created.Add(new MenuItem
            {
                Name = req.Name.Trim(),
                Category = string.IsNullOrWhiteSpace(req.Category) ? "Food" : req.Category.Trim(),
                Price = req.Price,
                Icon = req.Icon ?? "silverware-fork-knife",
                Subtitle = req.Subtitle ?? "",
                Image = await imageStorage.ResolveAsync("menu-items", req.Image) ?? "",
                Description = req.Description,
            });
        }

        db.MenuItems.AddRange(created);
        await db.SaveChangesAsync();
        return new BulkImportResultDto(created.Count, items.Count - created.Count);
    }

    [Authorize(Policy = Policies.OwnerOrManager)]
    [HttpPatch("{id:int}")]
    public async Task<ActionResult<MenuItem>> Update(int id, UpdateMenuItemRequest req)
    {
        var item = await db.MenuItems.FindAsync(id);
        if (item is null) return NotFound();

        if (req.Name is not null)
        {
            if (string.IsNullOrWhiteSpace(req.Name)) throw new ApiValidationException("Name cannot be blank.");
            if (req.Name.Trim().Length > 200) throw new ApiValidationException("Name cannot exceed 200 characters.");
            item.Name = req.Name.Trim();
        }
        if (req.Category is not null) item.Category = req.Category.Trim();
        if (req.Price is not null)
        {
            if (req.Price < 0) throw new ApiValidationException("Price cannot be negative.");
            item.Price = req.Price.Value;
        }
        if (req.Available is not null) item.Available = req.Available.Value;
        if (req.Subtitle is not null) item.Subtitle = req.Subtitle;
        if (req.Image is not null) item.Image = await imageStorage.ResolveAsync("menu-items", req.Image) ?? "";
        if (req.Description is not null) item.Description = req.Description;
        if (req.Popular is not null) item.Popular = req.Popular.Value;
        if (req.ProductType is not null) item.ProductType = req.ProductType.Value;
        if (req.LinkedInventoryItemId is not null) item.LinkedInventoryItemId = req.LinkedInventoryItemId;
        // Flipping back to Prepared drops any stale Independent stock link — the item
        // should consume via its Recipe again, not still drain the old linked ingredient.
        if (item.ProductType == ProductType.Prepared) item.LinkedInventoryItemId = null;
        if (req.ShortCode is not null)
        {
            if (req.ShortCode.Length > 5)
                throw new ApiValidationException("Short code cannot exceed 5 characters.");
            if (!string.IsNullOrWhiteSpace(req.ShortCode))
            {
                var existing = await db.MenuItems
                    .FirstOrDefaultAsync(m => m.ShortCode == req.ShortCode.ToUpper() && m.Id != id);
                if (existing is not null)
                    throw new ApiValidationException("This short code is already in use.");
            }
            item.ShortCode = string.IsNullOrWhiteSpace(req.ShortCode) ? null : req.ShortCode.ToUpper();
        }
        if (req.KitchenStation is not null) item.KitchenStation = req.KitchenStation;
        if (!string.IsNullOrWhiteSpace(req.ItemType) && Enum.TryParse<ItemType>(req.ItemType, ignoreCase: true, out var itemType))
            item.ItemType = itemType;
        if (!string.IsNullOrWhiteSpace(req.VegNonVegType) && Enum.TryParse<VegNonVegType>(req.VegNonVegType, ignoreCase: true, out var vegType))
            item.VegNonVegType = vegType;

        await db.SaveChangesAsync();
        return item;
    }

    /// <summary>Instant availability flip — the "unavailable item" toggle in the app.</summary>
    [Authorize]
    [HttpPatch("{id:int}/toggle-availability")]
    public async Task<ActionResult<MenuItem>> ToggleAvailability(int id)
    {
        var item = await db.MenuItems.FindAsync(id);
        if (item is null) return NotFound();

        item.Available = !item.Available;
        await db.SaveChangesAsync();
        return item;
    }

    [Authorize(Policy = Policies.OwnerOrManager)]
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        var item = await db.MenuItems.FindAsync(id);
        if (item is null) return NotFound();

        // MenuItemImage rows reference MenuItemId as a plain int (no DB-level FK/cascade
        // — same loose-reference pattern used elsewhere in this schema), so they'd
        // otherwise be orphaned once the item itself is gone.
        var images = await db.MenuItemImages.Where(i => i.MenuItemId == id).ToListAsync();
        db.MenuItemImages.RemoveRange(images);

        db.MenuItems.Remove(item);
        await db.SaveChangesAsync();
        return NoContent();
    }

    /// <summary>Extra photos beyond the item's single cover Image — a gallery an owner
    /// can build up over time (plating shot, ingredients close-up, etc.).</summary>
    [Authorize]
    [HttpGet("{id:int}/images")]
    public async Task<ActionResult<List<MenuItemImageDto>>> ListImages(int id)
    {
        var images = await db.MenuItemImages
            .Where(i => i.MenuItemId == id)
            .OrderBy(i => i.SortOrder)
            .ToListAsync();
        return images.Select(MenuItemImageDto.From).ToList();
    }

    [Authorize(Policy = Policies.OwnerOrManager)]
    [HttpPost("{id:int}/images")]
    public async Task<ActionResult<MenuItemImageDto>> AddImage(int id, AddMenuItemImageRequest req)
    {
        var itemExists = await db.MenuItems.AnyAsync(m => m.Id == id);
        if (!itemExists) return NotFound();

        if (string.IsNullOrWhiteSpace(req.DataUri) || !req.DataUri.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
            throw new ApiValidationException("That doesn't look like a valid image.");

        const int maxImagesPerItem = 8;
        var existingCount = await db.MenuItemImages.CountAsync(i => i.MenuItemId == id);
        if (existingCount >= maxImagesPerItem)
            throw new ApiValidationException($"An item can have at most {maxImagesPerItem} photos — remove one first.");

        var url = await imageStorage.UploadDataUriAsync("menu-items-gallery", req.DataUri);
        var image = new MenuItemImage { MenuItemId = id, DataUri = url, SortOrder = existingCount };
        db.MenuItemImages.Add(image);
        await db.SaveChangesAsync();
        return MenuItemImageDto.From(image);
    }

    [Authorize(Policy = Policies.OwnerOrManager)]
    [HttpDelete("{id:int}/images/{imageId:int}")]
    public async Task<IActionResult> RemoveImage(int id, int imageId)
    {
        var image = await db.MenuItemImages.FirstOrDefaultAsync(i => i.Id == imageId && i.MenuItemId == id);
        if (image is null) return NotFound();

        db.MenuItemImages.Remove(image);
        await db.SaveChangesAsync();
        return NoContent();
    }

    // ========== VARIANTS (Half/Full Plate) ==========
    [Authorize]
    [HttpPost("{menuItemId:int}/variants")]
    public async Task<ActionResult<VariantDto>> CreateVariant(int menuItemId, CreateVariantRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Name))
            throw new ApiValidationException("Variant name is required.");
        if (req.Price < 0)
            throw new ApiValidationException("Price cannot be negative.");

        var item = await db.MenuItems.FindAsync(menuItemId);
        if (item is null) return NotFound();

        var variant = new Variant
        {
            MenuItemId = menuItemId,
            Name = req.Name.Trim(),
            Price = req.Price,
            IsAvailable = true,
            SortOrder = await db.Variants.CountAsync(v => v.MenuItemId == menuItemId)
        };

        db.Variants.Add(variant);
        await db.SaveChangesAsync();
        return CreatedAtAction(nameof(GetVariant), new { menuItemId, id = variant.Id }, VariantDto.From(variant));
    }

    [Authorize]
    [HttpGet("{menuItemId:int}/variants")]
    public async Task<List<VariantDto>> ListVariants(int menuItemId) =>
        (await db.Variants.Where(v => v.MenuItemId == menuItemId).OrderBy(v => v.SortOrder).ToListAsync())
        .Select(VariantDto.From).ToList();

    [Authorize]
    [HttpGet("{menuItemId:int}/variants/{id:int}")]
    public async Task<ActionResult<VariantDto>> GetVariant(int menuItemId, int id)
    {
        var variant = await db.Variants.FirstOrDefaultAsync(v => v.Id == id && v.MenuItemId == menuItemId);
        return variant is null ? NotFound() : VariantDto.From(variant);
    }

    [Authorize(Policy = Policies.OwnerOrManager)]
    [HttpPatch("{menuItemId:int}/variants/{id:int}")]
    public async Task<ActionResult<VariantDto>> UpdateVariant(int menuItemId, int id, UpdateVariantRequest req)
    {
        var variant = await db.Variants.FirstOrDefaultAsync(v => v.Id == id && v.MenuItemId == menuItemId);
        if (variant is null) return NotFound();

        if (req.Name is not null) variant.Name = req.Name.Trim();
        if (req.Price is not null && req.Price >= 0) variant.Price = req.Price.Value;
        if (req.IsAvailable is not null) variant.IsAvailable = req.IsAvailable.Value;

        await db.SaveChangesAsync();
        return VariantDto.From(variant);
    }

    [Authorize(Policy = Policies.OwnerOrManager)]
    [HttpDelete("{menuItemId:int}/variants/{id:int}")]
    public async Task<IActionResult> DeleteVariant(int menuItemId, int id)
    {
        var variant = await db.Variants.FirstOrDefaultAsync(v => v.Id == id && v.MenuItemId == menuItemId);
        if (variant is null) return NotFound();

        db.Variants.Remove(variant);
        await db.SaveChangesAsync();
        return NoContent();
    }

    // ========== MODIFIERS (Spice, Add-ons, etc.) ==========
    [Authorize(Policy = Policies.OwnerOrManager)]
    [HttpPost("{menuItemId:int}/modifiers")]
    public async Task<ActionResult<ModifierDto>> CreateModifier(int menuItemId, CreateModifierRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Name))
            throw new ApiValidationException("Modifier name is required.");

        var item = await db.MenuItems.FindAsync(menuItemId);
        if (item is null) return NotFound();

        var modifier = new Modifier
        {
            MenuItemId = menuItemId,
            Name = req.Name.Trim(),
            Type = req.Type ?? "MultiSelect",
            IsRequired = req.IsRequired,
            SortOrder = await db.Modifiers.CountAsync(m => m.MenuItemId == menuItemId)
        };

        db.Modifiers.Add(modifier);
        await db.SaveChangesAsync();
        return CreatedAtAction(nameof(GetModifier), new { menuItemId, id = modifier.Id }, ModifierDto.From(modifier));
    }

    [Authorize]
    [HttpGet("{menuItemId:int}/modifiers")]
    public async Task<List<ModifierDto>> ListModifiers(int menuItemId) =>
        (await db.Modifiers
            .Where(m => m.MenuItemId == menuItemId)
            .Include(m => m.Options)
            .OrderBy(m => m.SortOrder)
            .ToListAsync())
        .Select(ModifierDto.From).ToList();

    [Authorize]
    [HttpGet("{menuItemId:int}/modifiers/{id:int}")]
    public async Task<ActionResult<ModifierDto>> GetModifier(int menuItemId, int id)
    {
        var modifier = await db.Modifiers
            .Include(m => m.Options)
            .FirstOrDefaultAsync(m => m.Id == id && m.MenuItemId == menuItemId);
        return modifier is null ? NotFound() : ModifierDto.From(modifier);
    }

    [Authorize(Policy = Policies.OwnerOrManager)]
    [HttpPatch("{menuItemId:int}/modifiers/{id:int}")]
    public async Task<ActionResult<ModifierDto>> UpdateModifier(int menuItemId, int id, UpdateModifierRequest req)
    {
        var modifier = await db.Modifiers
            .Include(m => m.Options)
            .FirstOrDefaultAsync(m => m.Id == id && m.MenuItemId == menuItemId);
        if (modifier is null) return NotFound();

        if (req.Name is not null) modifier.Name = req.Name.Trim();
        if (req.Type is not null) modifier.Type = req.Type;
        if (req.IsRequired is not null) modifier.IsRequired = req.IsRequired.Value;

        await db.SaveChangesAsync();
        return ModifierDto.From(modifier);
    }

    [Authorize(Policy = Policies.OwnerOrManager)]
    [HttpDelete("{menuItemId:int}/modifiers/{id:int}")]
    public async Task<IActionResult> DeleteModifier(int menuItemId, int id)
    {
        var modifier = await db.Modifiers.FirstOrDefaultAsync(m => m.Id == id && m.MenuItemId == menuItemId);
        if (modifier is null) return NotFound();

        db.Modifiers.Remove(modifier);
        await db.SaveChangesAsync();
        return NoContent();
    }

    [Authorize(Policy = Policies.OwnerOrManager)]
    [HttpPost("{menuItemId:int}/modifiers/{modifierId:int}/options")]
    public async Task<ActionResult<ModifierOptionDto>> CreateModifierOption(int menuItemId, int modifierId, CreateModifierOptionRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Name))
            throw new ApiValidationException("Option name is required.");

        var modifier = await db.Modifiers.FirstOrDefaultAsync(m => m.Id == modifierId && m.MenuItemId == menuItemId);
        if (modifier is null) return NotFound();

        var option = new ModifierOption
        {
            ModifierId = modifierId,
            Name = req.Name.Trim(),
            Price = req.Price,
            SortOrder = await db.ModifierOptions.CountAsync(o => o.ModifierId == modifierId)
        };

        db.ModifierOptions.Add(option);
        await db.SaveChangesAsync();
        return CreatedAtAction(nameof(GetModifierOption), new { menuItemId, modifierId, id = option.Id }, ModifierOptionDto.From(option));
    }

    [Authorize]
    [HttpGet("{menuItemId:int}/modifiers/{modifierId:int}/options")]
    public async Task<List<ModifierOptionDto>> ListModifierOptions(int menuItemId, int modifierId)
    {
        var modifier = await db.Modifiers.FindAsync(modifierId);
        if (modifier is null || modifier.MenuItemId != menuItemId) return [];

        return (await db.ModifierOptions
            .Where(o => o.ModifierId == modifierId)
            .OrderBy(o => o.SortOrder)
            .ToListAsync())
        .Select(ModifierOptionDto.From).ToList();
    }

    [Authorize]
    [HttpGet("{menuItemId:int}/modifiers/{modifierId:int}/options/{id:int}")]
    public async Task<ActionResult<ModifierOptionDto>> GetModifierOption(int menuItemId, int modifierId, int id)
    {
        var modifier = await db.Modifiers.FindAsync(modifierId);
        if (modifier is null || modifier.MenuItemId != menuItemId) return NotFound();

        var option = await db.ModifierOptions.FindAsync(id);
        return option is null || option.ModifierId != modifierId ? NotFound() : ModifierOptionDto.From(option);
    }

    [Authorize(Policy = Policies.OwnerOrManager)]
    [HttpPatch("{menuItemId:int}/modifiers/{modifierId:int}/options/{id:int}")]
    public async Task<ActionResult<ModifierOptionDto>> UpdateModifierOption(int menuItemId, int modifierId, int id, UpdateModifierOptionRequest req)
    {
        var modifier = await db.Modifiers.FindAsync(modifierId);
        if (modifier is null || modifier.MenuItemId != menuItemId) return NotFound();

        var option = await db.ModifierOptions.FindAsync(id);
        if (option is null || option.ModifierId != modifierId) return NotFound();

        if (req.Name is not null) option.Name = req.Name.Trim();
        if (req.Price is not null && req.Price >= -9999) option.Price = req.Price.Value;

        await db.SaveChangesAsync();
        return ModifierOptionDto.From(option);
    }

    [Authorize(Policy = Policies.OwnerOrManager)]
    [HttpDelete("{menuItemId:int}/modifiers/{modifierId:int}/options/{id:int}")]
    public async Task<IActionResult> DeleteModifierOption(int menuItemId, int modifierId, int id)
    {
        var modifier = await db.Modifiers.FindAsync(modifierId);
        if (modifier is null || modifier.MenuItemId != menuItemId) return NotFound();

        var option = await db.ModifierOptions.FindAsync(id);
        if (option is null || option.ModifierId != modifierId) return NotFound();

        db.ModifierOptions.Remove(option);
        await db.SaveChangesAsync();
        return NoContent();
    }

}
