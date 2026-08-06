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
public class MenuController(CafePosDbContext db, IImageStorageService imageStorage, IMenuPhotoAiService menuPhotoAi) : ControllerBase
{
    /// <summary>Public — powers the customer-facing QR Menu as well as the internal POS grid.
    /// Eager-loads Variants/Modifiers (with Options) — this is the one endpoint the POS grid
    /// and customer QR menu both use to render every item's ordering options in a single
    /// round trip, instead of an extra fetch per item once tapped.</summary>
    [AllowAnonymous]
    [HttpGet]
    public async Task<IEnumerable<MenuItem>> List() =>
        await db.MenuItems
            .Include(m => m.Variants.Where(v => v.IsAvailable).OrderBy(v => v.SortOrder))
            .Include(m => m.Modifiers.OrderBy(mo => mo.SortOrder)).ThenInclude(mo => mo.Options.OrderBy(o => o.SortOrder))
            .Include(m => m.Station)
            .OrderBy(m => m.Category).ThenBy(m => m.Name).ToListAsync();

    /// <summary>Resolves the (tracked) station a Create/Update/BulkCreate request should use:
    /// the requested StationId if it's valid for this tenant, otherwise the tenant's first
    /// (lowest SortOrder) active station — creating a default "Kitchen" station on first use
    /// so a tenant that's never touched Kitchen Stations still gets a valid, non-orphaned
    /// item. Returns the tracked entity (not just its id) so assigning it to
    /// MenuItem.Station keeps the in-memory nav property (and StationName projection) in
    /// sync immediately, without needing a reload.</summary>
    private async Task<Station> ResolveStationAsync(int? requestedStationId)
    {
        if (requestedStationId is int sid)
        {
            var requested = await db.Stations.FirstOrDefaultAsync(s => s.Id == sid);
            if (requested is null) throw new ApiValidationException("Selected kitchen station not found.");
            return requested;
        }

        var defaultStation = await db.Stations.OrderBy(s => s.SortOrder).FirstOrDefaultAsync(s => s.Active);
        if (defaultStation is not null) return defaultStation;

        var created = new Station { Name = "Kitchen" };
        db.Stations.Add(created);
        await db.SaveChangesAsync();
        return created;
    }

    private const int BestSellerCount = 3;

    /// <summary>
    /// Ranks menu items by units actually sold — not the manually-set Popular flag, so it
    /// stays honest as real demand shifts.
    /// period=month (default): last 30 days, top 3. A brand-new cafe with too little order
    /// history (fewer than 3 items with any sales) gets the remaining slots backfilled from
    /// Popular-flagged items (UnitsSold 0) so the section never renders empty on day one.
    /// period=today: cafe-local midnight (IST) through now, top 3 — "today's best-selling items so far".
    /// No Popular backfill here: a short or empty list before the day's sales pick up is the
    /// correct answer, not a gap to paper over.
    /// </summary>
    [AllowAnonymous]
    [HttpGet("best-sellers")]
    public async Task<IEnumerable<BestSellerDto>> BestSellers([FromQuery] string period = "month")
    {
        var isToday = period.Equals("today", StringComparison.OrdinalIgnoreCase);
        // "Today" starts at the cafe's midnight (see IstClock), not UTC's — a UTC cutoff starts
        // the day at 5:30am IST, which drops the whole after-midnight trade off this list.
        var cutoff = isToday
            ? IstClock.IstDateStartUtc(DateOnly.FromDateTime(IstClock.NowIst))
            : DateTime.UtcNow.AddDays(-30);
        var take = BestSellerCount;

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
        var nameLower = req.Name.Trim().ToLower();
        if (await db.MenuItems.AnyAsync(m => m.Name.ToLower() == nameLower))
            throw new ApiValidationException("A menu item with this name already exists.");
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
            Station = await ResolveStationAsync(req.StationId),
        };
        if (Enum.TryParse<ItemType>(req.ItemType ?? "Recipe", ignoreCase: true, out var itemType))
            item.ItemType = itemType;
        if (!string.IsNullOrWhiteSpace(req.VegNonVegType) && Enum.TryParse<VegNonVegType>(req.VegNonVegType, ignoreCase: true, out var vegType))
            item.VegNonVegType = vegType;
        item.TaxGroupId = await ValidatedTaxGroupIdAsync(req.TaxGroupId);
        db.MenuItems.Add(item);
        await db.SaveChangesAsync();
        return CreatedAtAction(nameof(List), new { id = item.Id }, item);
    }

    /// <summary>
    /// Powers "Import from Photo" (onboarding and MenuScreen) — parses a photographed menu
    /// into draft items via MenuPhotoAiService's Claude / Google Vision + Groq fallback
    /// chain. Returns the raw list for the client to show in its existing review-before-save
    /// screen (same one CSV import uses) rather than creating items directly, since AI
    /// output — like OCR output — should always be checked before it hits the real menu.
    /// An empty result isn't an error: it just means every AI tier is unconfigured/failed,
    /// and the client falls back to on-device OCR on its own (see menuPhotoImport.ts).
    /// </summary>
    [Authorize(Policy = Policies.OwnerOrManager)]
    [HttpPost("import-photo")]
    public async Task<ActionResult<List<CreateMenuItemRequest>>> ImportPhoto(ImportMenuPhotoRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.ImageDataUri))
            throw new ApiValidationException("An image is required.");
        return await menuPhotoAi.ExtractMenuItemsAsync(req.ImageDataUri);
    }

    /// <summary>Categorizes menu text the client already OCR'd on-device (Tesseract.js) via
    /// Groq — the free tier that doesn't need Google Cloud billing set up (see
    /// MenuPhotoAiService.CategorizeOcrTextAsync). Same empty-isn't-an-error contract as
    /// ImportPhoto above.</summary>
    [Authorize(Policy = Policies.OwnerOrManager)]
    [HttpPost("categorize-text")]
    public async Task<ActionResult<List<CreateMenuItemRequest>>> CategorizeText(CategorizeMenuTextRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.OcrText))
            throw new ApiValidationException("OCR text is required.");
        return await menuPhotoAi.CategorizeOcrTextAsync(req.OcrText);
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
        var defaultStation = await ResolveStationAsync(null);
        // Names already on the menu (plus names seen earlier in this same file) count as
        // duplicates and are skipped, so re-importing the same CSV can't double the menu.
        var existingNames = (await db.MenuItems.Select(m => m.Name).ToListAsync())
            .Select(n => n.Trim())
            .ToList();
        var seenNamesLower = existingNames.Select(n => n.ToLowerInvariant()).ToHashSet();
        var created = new List<MenuItem>();
        foreach (var req in valid)
        {
            var name = req.Name.Trim();
            var nameLower = name.ToLowerInvariant();
            if (seenNamesLower.Contains(nameLower)) continue;
            // AI/OCR reading the same photo twice (Import from Photo, re-scanned) doesn't
            // reliably produce byte-identical text — "Nachos Loaded" vs "Nachoes Loaded" is
            // the same item, not two — so an exact-match check alone lets it back in on a
            // second scan. Catch that near-miss case too, without touching manual single-item
            // Create/Update, where a person typing a name has full control over what they meant.
            if (existingNames.Any(existing => IsNearDuplicateName(existing, name))) continue;
            seenNamesLower.Add(nameLower);
            existingNames.Add(name);
            var item = new MenuItem
            {
                Name = req.Name.Trim(),
                Category = string.IsNullOrWhiteSpace(req.Category) ? "Food" : req.Category.Trim(),
                Price = req.Price,
                Icon = req.Icon ?? "silverware-fork-knife",
                Subtitle = req.Subtitle ?? "",
                Image = await imageStorage.ResolveAsync("menu-items", req.Image) ?? "",
                Description = req.Description,
                Station = req.StationId is int sid ? await ResolveStationAsync(sid) : defaultStation,
            };
            if (!string.IsNullOrWhiteSpace(req.VegNonVegType) && Enum.TryParse<VegNonVegType>(req.VegNonVegType, ignoreCase: true, out var vegType))
                item.VegNonVegType = vegType;
            created.Add(item);
        }

        db.MenuItems.AddRange(created);
        await db.SaveChangesAsync();
        return new BulkImportResultDto(created.Count, items.Count - created.Count);
    }

    /// <summary>Strips everything but letters/digits and lowercases, so near-duplicate
    /// comparison runs on the actual characters, not spacing/punctuation OCR noise
    /// ("Nachos  Loaded!" vs "nachos loaded" compare identically once normalized).</summary>
    private static string NormalizeForFuzzyMatch(string name) =>
        new(name.Where(char.IsLetterOrDigit).ToArray());

    /// <summary>True when two names are close enough to be the same item read slightly
    /// differently by AI/OCR on two separate scans of the same photo (see BulkCreate).
    /// Ratio-based, not a fixed edit distance, so it scales with name length: a 1-character
    /// difference on a long name ("Nachos Loaded" / "Nachoes Loaded") is a near-duplicate,
    /// but the same edit distance on a short name ("Tea" / "Pea") is a different item.</summary>
    private static bool IsNearDuplicateName(string a, string b)
    {
        var na = NormalizeForFuzzyMatch(a).ToLowerInvariant();
        var nb = NormalizeForFuzzyMatch(b).ToLowerInvariant();
        if (na.Length == 0 || nb.Length == 0) return na == nb;
        return (double)LevenshteinDistance(na, nb) / Math.Max(na.Length, nb.Length) <= 0.15;
    }

    private static int LevenshteinDistance(string a, string b)
    {
        var dp = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++) dp[i, 0] = i;
        for (var j = 0; j <= b.Length; j++) dp[0, j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                dp[i, j] = Math.Min(Math.Min(dp[i - 1, j] + 1, dp[i, j - 1] + 1), dp[i - 1, j - 1] + cost);
            }
        }
        return dp[a.Length, b.Length];
    }

    [Authorize(Policy = Policies.OwnerOrManager)]
    [HttpPatch("{id:int}")]
    public async Task<ActionResult<MenuItem>> Update(int id, UpdateMenuItemRequest req)
    {
        var item = await db.MenuItems.Include(m => m.Station).FirstOrDefaultAsync(m => m.Id == id);
        if (item is null) return NotFound();

        if (req.Name is not null)
        {
            if (string.IsNullOrWhiteSpace(req.Name)) throw new ApiValidationException("Name cannot be blank.");
            if (req.Name.Trim().Length > 200) throw new ApiValidationException("Name cannot exceed 200 characters.");
            var nameLower = req.Name.Trim().ToLower();
            if (await db.MenuItems.AnyAsync(m => m.Id != id && m.Name.ToLower() == nameLower))
                throw new ApiValidationException("A menu item with this name already exists.");
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
        if (req.StationId is not null) item.Station = await ResolveStationAsync(req.StationId);
        if (!string.IsNullOrWhiteSpace(req.ItemType) && Enum.TryParse<ItemType>(req.ItemType, ignoreCase: true, out var itemType))
            item.ItemType = itemType;
        if (!string.IsNullOrWhiteSpace(req.VegNonVegType) && Enum.TryParse<VegNonVegType>(req.VegNonVegType, ignoreCase: true, out var vegType))
            item.VegNonVegType = vegType;
        // 0 is the "clear it" sentinel — a null TaxGroupId on a PATCH means "leave unchanged"
        // like every other field here, so it can't also mean "back to the tenant default".
        if (req.TaxGroupId is not null)
            item.TaxGroupId = req.TaxGroupId == 0 ? null : await ValidatedTaxGroupIdAsync(req.TaxGroupId);

        await db.SaveChangesAsync();
        return item;
    }

    /// <summary>Rejects a tax group id that doesn't belong to this tenant, so an item can't
    /// end up pointing at a slab that will never resolve (and would silently bill at the
    /// default instead). Null passes through as "no explicit group".</summary>
    private async Task<int?> ValidatedTaxGroupIdAsync(int? taxGroupId)
    {
        if (taxGroupId is not int id || id == 0) return null;
        var exists = await db.TaxGroups.AnyAsync(t => t.Id == id);
        if (!exists) throw new ApiValidationException("Selected tax group not found.");
        return id;
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

    /// <summary>Pins/unpins an item to the front of the POS grid — see MenuItem.Pinned. Same
    /// any-authenticated-staff bar as ToggleAvailability above: it's a till-layout preference,
    /// not a price or a recipe, and the person who needs it changed is whoever is on the till.
    /// Note List() is deliberately left ordering by Category/Name — the POS applies this itself,
    /// so the customer QR menu (same endpoint) keeps its own alphabetical order.</summary>
    [Authorize]
    [HttpPatch("{id:int}/toggle-pinned")]
    public async Task<ActionResult<MenuItem>> TogglePinned(int id)
    {
        var item = await db.MenuItems.FindAsync(id);
        if (item is null) return NotFound();

        item.Pinned = !item.Pinned;
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

    /// <summary>Wipes the entire menu in one go — "start over" for a cafe that imported a
    /// bad CSV/photo batch and would rather clear everything than delete items one at a
    /// time. Past orders are untouched: OrderItem keeps its own name/price snapshot and
    /// only loosely references MenuItemId (no DB-level FK), same as every other loose
    /// reference in this schema, so order history and reports stay intact even though the
    /// menu items themselves are gone. Same per-item image cleanup as the single Delete
    /// above, just for every item at once.</summary>
    [Authorize(Policy = Policies.OwnerOrManager)]
    [HttpDelete]
    public async Task<IActionResult> DeleteAll()
    {
        var images = await db.MenuItemImages.ToListAsync();
        db.MenuItemImages.RemoveRange(images);

        var items = await db.MenuItems.ToListAsync();
        db.MenuItems.RemoveRange(items);

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

        // Only one variant can be "the" default (pre-selected in the ordering picker) —
        // marking a new one clears it off every existing sibling.
        if (req.IsDefault)
            foreach (var sibling in await db.Variants.Where(v => v.MenuItemId == menuItemId && v.IsDefault).ToListAsync())
                sibling.IsDefault = false;

        var variant = new Variant
        {
            MenuItemId = menuItemId,
            Name = req.Name.Trim(),
            Price = req.Price,
            IsAvailable = true,
            IsDefault = req.IsDefault,
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
        if (req.IsDefault is true)
        {
            foreach (var sibling in await db.Variants.Where(v => v.MenuItemId == menuItemId && v.Id != id && v.IsDefault).ToListAsync())
                sibling.IsDefault = false;
            variant.IsDefault = true;
        }
        else if (req.IsDefault is false)
        {
            variant.IsDefault = false;
        }

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
