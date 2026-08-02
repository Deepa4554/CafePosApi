using CafePOS.Api.Domain;
using Microsoft.AspNetCore.Identity;

namespace CafePOS.Api.Data;

/// <summary>
/// Catalog/config seed only (menu, tables, inventory, settings, subscription,
/// branches, integrations) — matches the React Native app's initial slices.
/// Transactional data (orders, customers, tasks, notifications, approvals,
/// audit log, staff) always starts empty: it is created live through the API.
/// </summary>
public static class SeedData
{
    public static void Apply(CafePosDbContext db)
    {
        // Must be the very first row ever inserted into Tenants so it gets Id == 1
        // (Tenant.DefaultTenantId) — every catalog item seeded below auto-stamps to
        // this tenant (see CafePosDbContext.StampTenantIds), and anonymous requests
        // (public QR menu) fall back to it until that flow carries its own tenant slug.
        if (!db.Tenants.Any())
        {
            db.Tenants.Add(new Tenant { Name = "Default Cafe", Slug = "default", Status = TenantStatus.Active });
            db.SaveChanges();
        }

        if (!db.Settings.Any())
        {
            db.Settings.Add(new CafeSettings());
        }

        if (!db.Subscriptions.Any())
        {
            db.Subscriptions.Add(new Subscription
            {
                Plan = SubscriptionTier.FreeTrial,
                PlanExpiresAt = DateTime.UtcNow.AddDays(14),
            });
        }

        if (!db.Branches.Any())
        {
            db.Branches.Add(new Branch { Name = "Downtown HQ", Address = "123 Coffee Street, Downtown" });
        }

        if (!db.Integrations.Any())
        {
            db.Integrations.AddRange(
                new Integration { Name = "UberEats", Category = "Delivery" },
                new Integration { Name = "DoorDash", Category = "Delivery" },
                new Integration { Name = "Deliveroo", Category = "Delivery" },
                new Integration { Name = "Menulog", Category = "Delivery" },
                new Integration { Name = "Stripe", Category = "Payments" },
                new Integration { Name = "WhatsApp Business", Category = "Messaging" });
        }
        // Separate, idempotent check (not folded into the block above) so an already-seeded
        // deployment picks up the "WhatsApp Business" card retroactively instead of only ever
        // getting it on a brand-new database.
        else if (!db.Integrations.Any(i => i.Name == "WhatsApp Business"))
        {
            db.Integrations.Add(new Integration { Name = "WhatsApp Business", Category = "Messaging" });
        }

        // Starting catalog matches the old hardcoded 4-item list the Points screen used to
        // show every tenant identically — seeded once so existing demo behavior is
        // preserved, but every cafe can now add/edit/remove its own from here.
        if (!db.Rewards.Any())
        {
            db.Rewards.AddRange(
                new Reward { Name = "Free Latte", PointsCost = 500, Icon = "coffee" },
                new Reward { Name = "Pastry Box", PointsCost = 750, Icon = "food-croissant" },
                new Reward { Name = "10% Off Order", PointsCost = 300, Icon = "tag-outline" },
                new Reward { Name = "Buy 1 Get 1", PointsCost = 900, Icon = "gift-outline" });
        }

        if (!db.MenuItems.Any())
        {
            db.MenuItems.AddRange(
                new MenuItem { Name = "Double Espresso", Category = "Espresso", Price = 4.50m, Icon = "coffee", Subtitle = "Intense & Creamy", Image = "https://picsum.photos/seed/double-espresso/400/300", Description = "Two shots of our house-blend Arabica, pulled fast and intense.", Popular = true },
                new MenuItem { Name = "Flat White", Category = "Espresso", Price = 5.25m, Icon = "coffee-outline", Subtitle = "Microfoam velvet", Image = "https://picsum.photos/seed/flat-white/400/300", Description = "Espresso with silky steamed milk microfoam." },
                new MenuItem { Name = "Lavender Oat Latte", Category = "Cold Brew", Price = 6.00m, Icon = "cup-outline", Subtitle = "Top paired with Almond Croissant", Image = "https://picsum.photos/seed/lavender-oat-latte/400/300", Description = "Cold brew, oat milk, and a hint of house-made lavender syrup.", Popular = true },
                new MenuItem { Name = "Almond Croissant", Category = "Pastries", Price = 4.00m, Icon = "food-croissant", Subtitle = "Flaky, buttery gold", Image = "https://picsum.photos/seed/almond-croissant/400/300", Description = "Twice-baked croissant filled with almond cream." },
                new MenuItem { Name = "V60 Pour Over", Category = "Espresso", Price = 4.75m, Icon = "coffee-maker-outline", Subtitle = "Ethiopian Yirgacheffe", Image = "https://picsum.photos/seed/v60-pour-over/400/300", Description = "Single-origin Ethiopian Yirgacheffe, hand poured." },
                new MenuItem { Name = "Matcha Ceremonial", Category = "Cold Brew", Price = 3.50m, Icon = "leaf", Subtitle = "Pure Uji Grade", Image = "https://picsum.photos/seed/matcha-ceremonial/400/300", Description = "Ceremonial-grade matcha whisked with oat milk over ice." },
                new MenuItem { Name = "Seasonal Dragon Bowl", Category = "Food", Price = 12.50m, Icon = "bowl-mix-outline", Subtitle = "Fresh dragon fruit blended with oat", Image = "https://picsum.photos/seed/dragon-bowl/400/300", Description = "Dragon fruit, banana, and oat milk smoothie bowl with house granola." },
                new MenuItem { Name = "Poached Avocado Toast", Category = "Food", Price = 11.00m, Icon = "bread-slice-outline", Subtitle = "Sourdough · Poached egg", Image = "https://picsum.photos/seed/avocado-toast/400/300", Description = "Sourdough, smashed avocado, poached egg, chili flakes.", Available = false });
        }

        if (!db.Tables.Any())
        {
            db.Tables.AddRange(
                new CafeTable { Code = "T1", Zone = "Indoor", Seats = 4 },
                new CafeTable { Code = "T2", Zone = "Indoor", Seats = 2 },
                new CafeTable { Code = "T3", Zone = "Indoor", Seats = 4 },
                new CafeTable { Code = "T4", Zone = "Indoor", Seats = 2 },
                new CafeTable { Code = "T5", Zone = "Indoor", Seats = 6 },
                new CafeTable { Code = "T6", Zone = "Indoor", Seats = 4 },
                new CafeTable { Code = "T7", Zone = "Indoor", Seats = 6 },
                new CafeTable { Code = "T8", Zone = "Indoor", Seats = 2 });
        }

        if (!db.InventoryItems.Any())
        {
            db.InventoryItems.AddRange(
                new InventoryItem { Name = "Oat Milk (Barista Edition)", Category = "Dairy Alternatives", Icon = "water-outline", Current = 4, Max = 24, Unit = "L", DailyUsage = 3, UnitCost = 3.20m },
                new InventoryItem { Name = "Whole Milk", Category = "Fresh Dairy", Icon = "water-outline", Current = 12, Max = 50, Unit = "L", DailyUsage = 4, UnitCost = 1.10m },
                new InventoryItem { Name = "Arabica Beans (House Blend)", Category = "Coffee Supply", Icon = "coffee-outline", Current = 18, Max = 20, Unit = "kg", DailyUsage = 1.2, UnitCost = 18.50m },
                new InventoryItem { Name = "Paper Cups (12oz)", Category = "Packaging", Icon = "cup-outline", Current = 850, Max = 1000, Unit = "", DailyUsage = 60, UnitCost = 0.08m });
        }

        BackfillMenuItemMedia(db);
        BackfillMenuItemStations(db);
        BackfillInventoryCost(db);
        BackfillUserTenantIds(db);
        BackfillPlatformAdmin(db);

        // BackfillInventoryBatches needs real InventoryItem ids (a freshly-seeded item
        // above has none yet — identity values only exist post-save on a relational
        // provider), so it runs as its own pass after this save, with its own save.
        db.SaveChanges();
        BackfillInventoryBatches(db);
        db.SaveChanges();
    }

    /// <summary>
    /// Bootstraps the ONE platform-operator account. There is no self-service or API
    /// path to set IsPlatformAdmin=true — this is the only place it ever gets set,
    /// so cafe owners created via /register-cafe can never obtain it.
    /// </summary>
    private static void BackfillPlatformAdmin(CafePosDbContext db)
    {
        const string email = "superadmin@cafepos.local";
        if (db.Users.Any(u => u.Email == email)) return;

        var hasher = new PasswordHasher<AppUser>();
        var admin = new AppUser
        {
            TenantId = Tenant.DefaultTenantId,
            Email = email,
            Name = "Platform Admin",
            Role = AppRole.Owner,
            IsPlatformAdmin = true,
            PasswordHash = "",
        };
        admin.PasswordHash = hasher.HashPassword(admin, "SuperAdmin#2026Demo");
        db.Users.Add(admin);
    }

    /// <summary>
    /// One-time patch for AppUser rows created before the AddMultiTenancy migration —
    /// that migration couldn't assign them a default TenantId (users aren't
    /// ITenantScoped, see Domain/Auth.cs), so they landed on TenantId 0. Without this,
    /// an existing account's JWT would carry tenantId=0 and every query would filter
    /// against a tenant that doesn't exist, making all of that account's data vanish.
    /// </summary>
    private static void BackfillUserTenantIds(CafePosDbContext db)
    {
        foreach (var user in db.Users.Where(u => u.TenantId == 0))
            user.TenantId = Tenant.DefaultTenantId;
    }

    /// <summary>
    /// One-time patch for databases seeded before Image/Description/Popular existed
    /// on MenuItem — the "if !Any()" blocks above only insert on a fully empty table,
    /// so already-seeded environments (e.g. the shared Supabase dev DB) need this too.
    /// </summary>
    private static void BackfillMenuItemMedia(CafePosDbContext db)
    {
        var media = new Dictionary<string, (string Image, string Description, bool Popular)>
        {
            ["Double Espresso"] = ("https://picsum.photos/seed/double-espresso/400/300", "Two shots of our house-blend Arabica, pulled fast and intense.", true),
            ["Flat White"] = ("https://picsum.photos/seed/flat-white/400/300", "Espresso with silky steamed milk microfoam.", false),
            ["Lavender Oat Latte"] = ("https://picsum.photos/seed/lavender-oat-latte/400/300", "Cold brew, oat milk, and a hint of house-made lavender syrup.", true),
            ["Almond Croissant"] = ("https://picsum.photos/seed/almond-croissant/400/300", "Twice-baked croissant filled with almond cream.", false),
            ["V60 Pour Over"] = ("https://picsum.photos/seed/v60-pour-over/400/300", "Single-origin Ethiopian Yirgacheffe, hand poured.", false),
            ["Matcha Ceremonial"] = ("https://picsum.photos/seed/matcha-ceremonial/400/300", "Ceremonial-grade matcha whisked with oat milk over ice.", false),
            ["Seasonal Dragon Bowl"] = ("https://picsum.photos/seed/dragon-bowl/400/300", "Dragon fruit, banana, and oat milk smoothie bowl with house granola.", false),
            ["Poached Avocado Toast"] = ("https://picsum.photos/seed/avocado-toast/400/300", "Sourdough, smashed avocado, poached egg, chili flakes.", false),
        };

        foreach (var item in db.MenuItems.Local.Union(db.MenuItems))
        {
            if (!string.IsNullOrEmpty(item.Image)) continue;
            if (!media.TryGetValue(item.Name, out var m)) continue;
            item.Image = m.Image;
            item.Description = m.Description;
            item.Popular = m.Popular;
        }
    }

    /// <summary>Ensures every MenuItem has a real Station — freshly-seeded items above never
    /// set one (StationId defaults to 0, no matching row). On a real Postgres deployment this
    /// is a one-time gap the AddKitchenStations migration's backfill SQL already closed, but
    /// the InMemory dev provider (see Program.cs) uses EnsureCreated instead of Migrate, so
    /// that SQL never runs there — this covers both paths by creating a single default
    /// "Kitchen" station on first use and pointing any StationId==0 item at it.</summary>
    private static void BackfillMenuItemStations(CafePosDbContext db)
    {
        var orphaned = db.MenuItems.Local.Union(db.MenuItems).Where(m => m.StationId == 0).ToList();
        if (orphaned.Count == 0) return;

        var kitchen = db.Stations.Local.FirstOrDefault(s => s.Name == "Kitchen")
            ?? db.Stations.FirstOrDefault(s => s.Name == "Kitchen");
        if (kitchen is null)
        {
            kitchen = new Station { Name = "Kitchen" };
            db.Stations.Add(kitchen);
        }
        foreach (var item in orphaned)
            item.Station = kitchen;
    }

    private static void BackfillInventoryCost(CafePosDbContext db)
    {
        var costs = new Dictionary<string, decimal>
        {
            ["Oat Milk (Barista Edition)"] = 3.20m,
            ["Whole Milk"] = 1.10m,
            ["Arabica Beans (House Blend)"] = 18.50m,
            ["Paper Cups (12oz)"] = 0.08m,
        };

        foreach (var item in db.InventoryItems.Local.Union(db.InventoryItems))
        {
            if (item.UnitCost != 0) continue;
            if (!costs.TryGetValue(item.Name, out var cost)) continue;
            item.UnitCost = cost;
        }
    }

    /// <summary>One-time patch for every InventoryItem that predates batch tracking (FIFO +
    /// expiry) — gives each one a single no-expiry batch matching its current balance, so
    /// FIFO consumption has something to draw from immediately. Runs after the item-creation
    /// save above so ids are real; safe to run on every startup (skips items that already
    /// have a batch).</summary>
    private static void BackfillInventoryBatches(CafePosDbContext db)
    {
        // Materialize both queries up front — Npgsql doesn't allow a nested query to run
        // against the same connection while an outer one is still being enumerated.
        var itemsWithBatches = db.InventoryBatches.Select(b => b.InventoryItemId).Distinct().ToHashSet();
        foreach (var item in db.InventoryItems.ToList())
        {
            if (item.Current == 0) continue;
            if (itemsWithBatches.Contains(item.Id)) continue;
            db.InventoryBatches.Add(new InventoryBatch
            {
                InventoryItemId = item.Id,
                Quantity = item.Current,
                UnitCost = item.UnitCost,
                ExpiryDate = null,
                ReceivedAt = item.LastRestockAt ?? DateTime.UtcNow,
            });
        }
    }
}
