using System.Reflection;
using System.Text.Json;
using CafePOS.Api.Domain;
using CafePOS.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace CafePOS.Api.Data;

public class CafePosDbContext(DbContextOptions<CafePosDbContext> options, ITenantContext tenant, IRealtimeNotifier realtime, IPushNotificationSender pushSender, IWhatsAppEventPublisher whatsAppEvents) : DbContext(options)
{
    private readonly ITenantContext _tenant = tenant;

    // Npgsql only accepts Kind=Utc DateTimes for "timestamp with time zone"
    // columns. Client JSON (e.g. Task.DueDate, Shift times, Coupon.ExpiresAt)
    // deserializes with an offset into Kind=Local/Unspecified, which throws at
    // save time otherwise. Applying this to every DateTime property in the
    // model — rather than fixing it endpoint-by-endpoint — means it can never
    // silently regress when a new date field is added later.
    private sealed class UtcDateTimeConverter() : ValueConverter<DateTime, DateTime>(
        v => v.Kind == DateTimeKind.Utc ? v : v.ToUniversalTime(),
        v => DateTime.SpecifyKind(v, DateTimeKind.Utc));

    private sealed class UtcNullableDateTimeConverter() : ValueConverter<DateTime?, DateTime?>(
        v => v.HasValue ? (v.Value.Kind == DateTimeKind.Utc ? v.Value : v.Value.ToUniversalTime()) : v,
        v => v.HasValue ? DateTime.SpecifyKind(v.Value, DateTimeKind.Utc) : v);

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>();
        configurationBuilder.Properties<DateTime?>().HaveConversion<UtcNullableDateTimeConverter>();
    }

    // Multi-tenancy
    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<PlatformExpense> PlatformExpenses => Set<PlatformExpense>();

    // Core catalog / ordering
    public DbSet<MenuItem> MenuItems => Set<MenuItem>();
    public DbSet<Station> Stations => Set<Station>();
    public DbSet<MenuCategory> MenuCategories => Set<MenuCategory>();
    public DbSet<TaxGroup> TaxGroups => Set<TaxGroup>();
    public DbSet<MenuItemImage> MenuItemImages => Set<MenuItemImage>();
    public DbSet<Variant> Variants => Set<Variant>();
    public DbSet<Modifier> Modifiers => Set<Modifier>();
    public DbSet<ModifierOption> ModifierOptions => Set<ModifierOption>();
    public DbSet<CafeTable> Tables => Set<CafeTable>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderItem> OrderItems => Set<OrderItem>();
    public DbSet<OrderItemModifier> OrderItemModifiers => Set<OrderItemModifier>();
    public DbSet<OrderFireBatch> OrderFireBatches => Set<OrderFireBatch>();
    public DbSet<OrderPayment> OrderPayments => Set<OrderPayment>();
    public DbSet<InventoryItem> InventoryItems => Set<InventoryItem>();
    public DbSet<Vendor> Vendors => Set<Vendor>();
    public DbSet<CafeSettings> Settings => Set<CafeSettings>();
    public DbSet<GuestSession> GuestSessions => Set<GuestSession>();
    public DbSet<SessionDevice> SessionDevices => Set<SessionDevice>();
    public DbSet<TokenCounter> TokenCounters => Set<TokenCounter>();
    public DbSet<OrderNoteSuggestion> OrderNoteSuggestions => Set<OrderNoteSuggestion>();

    // Auth
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<RefreshTokenEntry> RefreshTokens => Set<RefreshTokenEntry>();
    public DbSet<EmailOtp> EmailOtps => Set<EmailOtp>();
    public DbSet<DeviceToken> DeviceTokens => Set<DeviceToken>();

    // CRM
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<Coupon> Coupons => Set<Coupon>();
    public DbSet<GiftCard> GiftCards => Set<GiftCard>();
    public DbSet<Reward> Rewards => Set<Reward>();
    public DbSet<FavoriteItem> FavoriteItems => Set<FavoriteItem>();

    // Management
    public DbSet<StaffTask> Tasks => Set<StaffTask>();
    public DbSet<AppNotification> Notifications => Set<AppNotification>();
    public DbSet<ApprovalRequest> Approvals => Set<ApprovalRequest>();
    public DbSet<AuditLogEntry> AuditLog => Set<AuditLogEntry>();
    public DbSet<ApiFailureLog> ApiFailureLogs => Set<ApiFailureLog>();
    public DbSet<StaffMember> Staff => Set<StaffMember>();
    public DbSet<Shift> Shifts => Set<Shift>();
    public DbSet<LeaveRequest> LeaveRequests => Set<LeaveRequest>();
    public DbSet<CafeExpense> CafeExpenses => Set<CafeExpense>();
    public DbSet<Branch> Branches => Set<Branch>();
    public DbSet<Subscription> Subscriptions => Set<Subscription>();
    public DbSet<Integration> Integrations => Set<Integration>();
    public DbSet<WhatsAppSession> WhatsAppSessions => Set<WhatsAppSession>();
    public DbSet<WhatsAppOrderTracking> WhatsAppTracking => Set<WhatsAppOrderTracking>();
    public DbSet<WhatsAppMessageLog> WhatsAppMessageLogs => Set<WhatsAppMessageLog>();
    public DbSet<SupportTicket> SupportTickets => Set<SupportTicket>();
    public DbSet<SupportTicketMessage> SupportTicketMessages => Set<SupportTicketMessage>();
    public DbSet<AttendanceLog> AttendanceLogs => Set<AttendanceLog>();
    public DbSet<AttendanceRecord> AttendanceRecords => Set<AttendanceRecord>();
    public DbSet<StaffLoan> StaffLoans => Set<StaffLoan>();
    public DbSet<PayrollRun> PayrollRuns => Set<PayrollRun>();
    public DbSet<PayrollLine> PayrollLines => Set<PayrollLine>();

    // Recipe-based inventory
    public DbSet<Recipe> Recipes => Set<Recipe>();
    public DbSet<RecipeItem> RecipeItems => Set<RecipeItem>();
    public DbSet<InventoryTransaction> InventoryTransactions => Set<InventoryTransaction>();
    public DbSet<PurchaseOrder> PurchaseOrders => Set<PurchaseOrder>();
    public DbSet<PurchaseItem> PurchaseItems => Set<PurchaseItem>();
    public DbSet<StockTake> StockTakes => Set<StockTake>();
    public DbSet<StockTakeLine> StockTakeLines => Set<StockTakeLine>();
    public DbSet<MissingRecipeAlert> MissingRecipeAlerts => Set<MissingRecipeAlert>();
    public DbSet<InventoryBatch> InventoryBatches => Set<InventoryBatch>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Order>()
            .HasMany(o => o.Items)
            .WithOne()
            .HasForeignKey(i => i.OrderId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<Order>()
            .HasOne(o => o.Customer)
            .WithMany(c => c.Orders)
            .HasForeignKey(o => o.CustomerId)
            .OnDelete(DeleteBehavior.SetNull);

        modelBuilder.Entity<Order>()
            .HasMany(o => o.FireBatches)
            .WithOne()
            .HasForeignKey(b => b.OrderId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<Order>()
            .HasMany(o => o.Payments)
            .WithOne()
            .HasForeignKey(p => p.OrderId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<OrderItem>()
            .HasMany(i => i.SelectedModifiers)
            .WithOne()
            .HasForeignKey(m => m.OrderItemId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<OrderFireBatch>()
            .HasIndex(b => new { b.OrderId, b.BatchNumber })
            .IsUnique();

        modelBuilder.Entity<Customer>()
            .HasMany(c => c.Coupons)
            .WithOne()
            .HasForeignKey(c => c.CustomerId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<Customer>()
            .HasMany(c => c.GiftCards)
            .WithOne()
            .HasForeignKey(g => g.CustomerId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<Customer>()
            .HasMany(c => c.FavoriteItems)
            .WithOne(f => f.Customer)
            .HasForeignKey(f => f.CustomerId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<SupportTicket>()
            .HasMany(t => t.Messages)
            .WithOne()
            .HasForeignKey(m => m.TicketId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<Modifier>()
            .HasMany(m => m.Options)
            .WithOne()
            .HasForeignKey(o => o.ModifierId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<Recipe>()
            .HasMany(r => r.Items)
            .WithOne()
            .HasForeignKey(i => i.RecipeId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<PurchaseOrder>()
            .HasMany(p => p.Items)
            .WithOne()
            .HasForeignKey(i => i.PurchaseOrderId)
            .OnDelete(DeleteBehavior.Cascade);

        // Vendors are deactivated, never deleted, once referenced by a PO — SetNull is
        // defense-in-depth only, same convention as InventoryTransaction.Batch below.
        modelBuilder.Entity<PurchaseOrder>()
            .HasOne<Vendor>()
            .WithMany()
            .HasForeignKey(p => p.VendorId)
            .OnDelete(DeleteBehavior.SetNull);

        // Unidirectional — InventoryBatch has no collection navigation back, matching the
        // ledger's other loose string-reference fields. SetNull rather than cascade: a
        // depleted batch is never deleted, only zeroed out, so this is defense-in-depth only.
        modelBuilder.Entity<InventoryTransaction>()
            .HasOne(t => t.Batch)
            .WithMany()
            .HasForeignKey(t => t.InventoryBatchId)
            .OnDelete(DeleteBehavior.SetNull);

        modelBuilder.Entity<StockTake>()
            .HasMany(s => s.Lines)
            .WithOne()
            .HasForeignKey(l => l.StockTakeId)
            .OnDelete(DeleteBehavior.Cascade);

        // The one real navigation+cascade in the whole Payroll feature — a PayrollLine
        // has no existence outside its parent run, same reasoning as Order.Items.
        modelBuilder.Entity<PayrollRun>()
            .HasMany(r => r.Lines)
            .WithOne()
            .HasForeignKey(l => l.PayrollRunId)
            .OnDelete(DeleteBehavior.Cascade);

        // Store enums as readable strings rather than opaque ints.
        modelBuilder.Entity<Order>().Property(o => o.Status).HasConversion<string>();
        modelBuilder.Entity<OrderFireBatch>().Property(b => b.Status).HasConversion<string>();
        modelBuilder.Entity<OrderItem>().Property(i => i.Status).HasConversion<string>();
        modelBuilder.Entity<AppUser>().Property(u => u.Role).HasConversion<string>();
        modelBuilder.Entity<Coupon>().Property(c => c.Type).HasConversion<string>();
        modelBuilder.Entity<GiftCard>().Property(g => g.Status).HasConversion<string>();
        modelBuilder.Entity<StaffTask>().Property(t => t.Priority).HasConversion<string>();
        modelBuilder.Entity<StaffTask>().Property(t => t.Status).HasConversion<string>();
        modelBuilder.Entity<AppNotification>().Property(n => n.Category).HasConversion<string>();
        modelBuilder.Entity<AppNotification>().Property(n => n.Channel).HasConversion<string>();
        modelBuilder.Entity<AppNotification>().Property(n => n.DeliveryStatus).HasConversion<string>();
        modelBuilder.Entity<ApprovalRequest>().Property(a => a.Type).HasConversion<string>();
        modelBuilder.Entity<ApprovalRequest>().Property(a => a.Status).HasConversion<string>();
        modelBuilder.Entity<AuditLogEntry>().Property(a => a.Action).HasConversion<string>();
        modelBuilder.Entity<AuditLogEntry>().Property(a => a.Resource).HasConversion<string>();
        modelBuilder.Entity<AuditLogEntry>().Property(a => a.Severity).HasConversion<string>();
        modelBuilder.Entity<StaffMember>().Property(s => s.Status).HasConversion<string>();
        modelBuilder.Entity<Subscription>().Property(s => s.Plan).HasConversion<string>();
        modelBuilder.Entity<Integration>().Property(i => i.Status).HasConversion<string>();
        modelBuilder.Entity<WhatsAppSession>().Property(s => s.Status).HasConversion<string>();
        modelBuilder.Entity<WhatsAppMessageLog>().Property(m => m.Direction).HasConversion<string>();
        modelBuilder.Entity<WhatsAppMessageLog>().Property(m => m.Type).HasConversion<string>();
        modelBuilder.Entity<WhatsAppMessageLog>().Property(m => m.Status).HasConversion<string>();
        modelBuilder.Entity<SupportTicket>().Property(t => t.Status).HasConversion<string>();
        modelBuilder.Entity<MenuItem>().Property(m => m.ProductType).HasConversion<string>();
        modelBuilder.Entity<MenuItem>().Property(m => m.ItemType).HasConversion<string>();
        modelBuilder.Entity<MenuItem>().Property(m => m.VegNonVegType).HasConversion<string>();
        modelBuilder.Entity<OrderItem>().Property(i => i.VegNonVegType).HasConversion<string>();
        modelBuilder.Entity<InventoryTransaction>().Property(t => t.Type).HasConversion<string>();
        modelBuilder.Entity<InventoryTransaction>().Property(t => t.WasteReasonCode).HasConversion<string>();
        modelBuilder.Entity<StockTake>().Property(s => s.Status).HasConversion<string>();
        modelBuilder.Entity<PurchaseOrder>().Property(p => p.Status).HasConversion<string>();
        modelBuilder.Entity<GuestSession>().Property(s => s.Status).HasConversion<string>();
        modelBuilder.Entity<GuestSession>().Property(s => s.ClosedReason).HasConversion<string>();
        modelBuilder.Entity<AppUser>().Property(u => u.AccessMode).HasConversion<string>();
        modelBuilder.Entity<StaffMember>().Property(s => s.SalaryType).HasConversion<string>();
        modelBuilder.Entity<AttendanceLog>().Property(a => a.Type).HasConversion<string>();
        modelBuilder.Entity<AttendanceLog>().Property(a => a.Source).HasConversion<string>();
        modelBuilder.Entity<AttendanceRecord>().Property(a => a.Status).HasConversion<string>();
        modelBuilder.Entity<StaffLoan>().Property(l => l.Type).HasConversion<string>();
        modelBuilder.Entity<StaffLoan>().Property(l => l.Status).HasConversion<string>();
        modelBuilder.Entity<PayrollRun>().Property(r => r.Status).HasConversion<string>();
        modelBuilder.Entity<PayrollLine>().Property(l => l.SalaryType).HasConversion<string>();

        // AllowanceLine list — same reasoning as AppUser.AllowedScreens just below: a
        // short list only ever read/written whole, never queried by individual entry.
        modelBuilder.Entity<PayrollLine>().Property(l => l.Allowances)
            .HasConversion(
                v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                v => JsonSerializer.Deserialize<List<AllowanceLine>>(v, (JsonSerializerOptions?)null) ?? new())
            .Metadata.SetValueComparer(new ValueComparer<List<AllowanceLine>>(
                (a, b) => (a ?? new()).SequenceEqual(b ?? new()),
                v => v.Aggregate(0, (h, s) => HashCode.Combine(h, s.GetHashCode())),
                v => v.ToList()));

        // AllowedScreens is a short list of catalog keys (e.g. ["POS","KDS"]) — stored as
        // a JSON array rather than a join table since it's only ever read/written whole,
        // never queried by individual key, same reasoning as PhotoUrl's inline data URI.
        modelBuilder.Entity<AppUser>().Property(u => u.AllowedScreens)
            .HasConversion(
                v => v == null ? null : JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                v => v == null ? null : JsonSerializer.Deserialize<List<string>>(v, (JsonSerializerOptions?)null))
            .Metadata.SetValueComparer(new ValueComparer<List<string>?>(
                (a, b) => (a ?? new()).SequenceEqual(b ?? new()),
                v => v == null ? 0 : v.Aggregate(0, (h, s) => HashCode.Combine(h, s.GetHashCode())),
                v => v == null ? null : v.ToList()));

        // Codes only need to be unique within a cafe — two different tenants can
        // both have a table "T1" or a coupon "WELCOME10" without colliding.
        modelBuilder.Entity<CafeTable>().HasIndex(t => new { t.TenantId, t.Code }).IsUnique();
        modelBuilder.Entity<Customer>().HasIndex(c => c.Name);
        // AppUser.Email stays globally unique (not tenant-scoped): one email = one
        // login, and login must resolve it before any tenant is known.
        modelBuilder.Entity<AppUser>().HasIndex(u => u.Email).IsUnique();
        modelBuilder.Entity<Coupon>().HasIndex(c => new { c.TenantId, c.Code }).IsUnique();
        modelBuilder.Entity<GiftCard>().HasIndex(g => new { g.TenantId, g.Code }).IsUnique();
        modelBuilder.Entity<Tenant>().HasIndex(t => t.Slug).IsUnique();
        modelBuilder.Entity<EmailOtp>().HasIndex(o => o.Email);
        // One recipe per menu item per tenant.
        modelBuilder.Entity<Recipe>().HasIndex(r => new { r.TenantId, r.MenuItemId }).IsUnique();
        modelBuilder.Entity<MenuItemImage>().HasIndex(i => i.MenuItemId);
        // ShortCode is optional but must be unique when set (per tenant).
        modelBuilder.Entity<MenuItem>().HasIndex(m => new { m.TenantId, m.ShortCode }).IsUnique()
            .HasFilter("\"ShortCode\" IS NOT NULL");
        // Looked up on every single authenticated request that needs a token refresh.
        modelBuilder.Entity<RefreshTokenEntry>().HasIndex(t => t.Token).IsUnique();
        modelBuilder.Entity<RefreshTokenEntry>().HasIndex(t => t.UserId);
        // Enforces "one active session per table" at the DB level (doc Section 7) — a
        // second scan while one is ACTIVE/LOCKED must go through the join flow, never
        // create a second row.
        modelBuilder.Entity<GuestSession>().HasIndex(s => s.TableId).IsUnique()
            .HasFilter("\"Status\" IN ('Active','Locked')");
        // Looked up on every guest-session-scoped request (see ValidateGuestSessionAttribute)
        // — one row per device credential, not per session (see SessionDevice doc comment).
        modelBuilder.Entity<SessionDevice>().HasIndex(d => d.TokenHash).IsUnique();
        modelBuilder.Entity<SessionDevice>().HasIndex(d => d.SessionId);
        // Always sorted newest-first and commonly filtered by status — this table
        // grows with every failed request, so both need to stay index-backed.
        modelBuilder.Entity<ApiFailureLog>().HasIndex(f => f.Timestamp);
        modelBuilder.Entity<ApiFailureLog>().HasIndex(f => f.StatusCode);

        // Idempotency guard — one Sale-type (fire-time) deduction per (OrderItem,
        // Ingredient, Batch). Batch is part of the key (not just OrderItem+Ingredient)
        // because ConsumeFifoAsync can legitimately write more than one Sale row for the
        // same order line's ingredient — a draw that spans two lots is two correctly
        // batch-tagged rows, not a duplicate; only a second row for the SAME batch means an
        // actual retry/duplicate fire. Existing rows all have OrderItemId == NULL; Postgres
        // treats NULLs as distinct in a unique index, so historical Sale rows never collide
        // with each other or with this filter — no backfill needed.
        modelBuilder.Entity<InventoryTransaction>()
            .HasIndex(t => new { t.OrderItemId, t.InventoryItemId, t.InventoryBatchId })
            .IsUnique()
            .HasFilter("\"Type\" = 'Sale' AND \"OrderItemId\" IS NOT NULL");

        modelBuilder.Entity<MissingRecipeAlert>().HasIndex(a => new { a.TenantId, a.MenuItemId }).IsUnique();

        // One counter row per (tenant, day) — NextTokenNumberAsync UPSERTs into this via
        // ON CONFLICT, so the constraint must match exactly what the UPSERT targets.
        modelBuilder.Entity<TokenCounter>().HasIndex(c => new { c.TenantId, c.Date }).IsUnique();

        // One row per distinct note text per tenant — repeating an already-known note just
        // bumps its UsageCount/LastUsedAt instead of creating a duplicate suggestion.
        modelBuilder.Entity<OrderNoteSuggestion>().HasIndex(s => new { s.TenantId, s.Text }).IsUnique();

        // One row per distinct station name per tenant — same "no silent typo duplicates"
        // guarantee the free-text KitchenStation field never had.
        modelBuilder.Entity<Station>().HasIndex(s => new { s.TenantId, s.Name }).IsUnique();
        modelBuilder.Entity<MenuCategory>().HasIndex(c => new { c.TenantId, c.Name }).IsUnique();

        // FIFO consumption walks batches ordered by (InventoryItemId, ExpiryDate, ReceivedAt)
        // for one ingredient at a time — this compound index backs that query directly.
        modelBuilder.Entity<InventoryBatch>().HasIndex(b => new { b.InventoryItemId, b.ExpiryDate, b.ReceivedAt });

        modelBuilder.Entity<DeviceToken>().HasIndex(t => t.Token).IsUnique();

        // One derived attendance row per staff per day.
        modelBuilder.Entity<AttendanceRecord>().HasIndex(a => new { a.TenantId, a.StaffId, a.Date }).IsUnique();
        modelBuilder.Entity<AttendanceLog>().HasIndex(a => new { a.TenantId, a.StaffId, a.Timestamp });
        modelBuilder.Entity<StaffLoan>().HasIndex(l => new { l.TenantId, l.StaffId, l.Status });
        // One payroll run per exact period per tenant — regenerating for the same
        // dates must go through Delete-then-recreate, never silently duplicate.
        modelBuilder.Entity<PayrollRun>().HasIndex(r => new { r.TenantId, r.PeriodStart, r.PeriodEnd }).IsUnique();
        modelBuilder.Entity<PayrollLine>().HasIndex(l => new { l.PayrollRunId, l.StaffId });

        // One Baileys session per tenant.
        modelBuilder.Entity<WhatsAppSession>().HasIndex(s => s.TenantId).IsUnique();
        // TrackingId is the QR payload — must resolve unambiguously on an inbound message with
        // no tenant context yet, so it's globally unique, not just per-tenant. OrderId is unique
        // too: one tracking row per order, a reprint just reuses the existing row (see
        // OrdersController.GetOrCreateWhatsAppTracking).
        modelBuilder.Entity<WhatsAppOrderTracking>().HasIndex(t => t.TrackingId).IsUnique();
        modelBuilder.Entity<WhatsAppOrderTracking>().HasIndex(t => t.OrderId).IsUnique();

        // The queue poll (WhatsAppInternalController.GetPendingQueueJobs) runs every few
        // seconds per connected tenant and filters on exactly these columns — without this
        // it degrades into a full scan of an append-only audit table that only ever grows.
        modelBuilder.Entity<WhatsAppMessageLog>().HasIndex(m => new { m.TenantId, m.Direction, m.Status, m.NextAttemptAt });

        ApplyTenantIsolation(modelBuilder);
    }

    /// <summary>
    /// For every entity implementing ITenantScoped: adds a global query filter so it's
    /// automatically restricted to the current request's tenant (no controller needs to
    /// remember `.Where(x => x.TenantId == ...)`), an index for the filtered queries,
    /// and a DB-level default of Tenant.DefaultTenantId so the migration can add the
    /// column to already-populated tables without a separate backfill step.
    /// </summary>
    private void ApplyTenantIsolation(ModelBuilder modelBuilder)
    {
        var applyGeneric = typeof(CafePosDbContext)
            .GetMethod(nameof(ApplyTenantIsolationFor), BindingFlags.NonPublic | BindingFlags.Instance)!;

        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            if (!typeof(ITenantScoped).IsAssignableFrom(entityType.ClrType)) continue;
            applyGeneric.MakeGenericMethod(entityType.ClrType).Invoke(this, [modelBuilder]);
        }
    }

    private void ApplyTenantIsolationFor<T>(ModelBuilder modelBuilder) where T : class, ITenantScoped
    {
        modelBuilder.Entity<T>().Property(e => e.TenantId).HasDefaultValue(Tenant.DefaultTenantId);
        modelBuilder.Entity<T>().HasIndex(e => e.TenantId);
        modelBuilder.Entity<T>().HasQueryFilter(e => e.TenantId == _tenant.TenantIdOrDefault);
    }

    /// <summary>Stamps TenantId onto every newly-added tenant-scoped entity so
    /// controllers never have to set it themselves. Skips entities that already have
    /// an explicit non-zero TenantId (e.g. register-cafe creating rows for a brand
    /// new tenant before that tenant is "current").</summary>
    private void StampTenantIds()
    {
        foreach (var entry in ChangeTracker.Entries())
        {
            if (entry.State == EntityState.Added && entry.Entity is ITenantScoped scoped && scoped.TenantId == 0)
                scoped.TenantId = _tenant.TenantIdOrDefault;
        }
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        StampTenantIds();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    // The entity types Orders/KDS/Tables screens actually poll for — see useOrders.ts /
    // useTables.ts on the client. Anything else changing (menu, inventory, staff, ...) keeps
    // its existing on-demand-refetch behaviour; only these are latency-sensitive enough to
    // warrant a push.
    private static readonly HashSet<Type> RealtimeEntityTypes =
        [typeof(Order), typeof(OrderItem), typeof(OrderItemModifier), typeof(OrderFireBatch), typeof(CafeTable)];

    /// <summary>Reads TenantIds off every added/modified/deleted Order/Table-family entity
    /// BEFORE SaveChanges runs — ChangeTracker entries flip to Unchanged (and Added/Deleted
    /// entries get detached) once SaveChanges succeeds, so this has to be captured up front,
    /// not after.</summary>
    private HashSet<int> CollectRealtimeAffectedTenantIds() =>
        ChangeTracker.Entries()
            .Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted
                && RealtimeEntityTypes.Contains(e.Entity.GetType()))
            .Select(e => ((ITenantScoped)e.Entity).TenantId)
            .Where(id => id != 0)
            .ToHashSet();

    /// <summary>Same "read before SaveChanges, act after" shape as
    /// CollectRealtimeAffectedTenantIds — an Added AppNotification's entry flips to Unchanged
    /// once the save succeeds, but the entity object itself (Title/Body/TenantId/...) is still
    /// fully populated, so no separate DTO capture is needed.</summary>
    private List<AppNotification> CollectNewNotifications() =>
        ChangeTracker.Entries<AppNotification>().Where(e => e.State == EntityState.Added).Select(e => e.Entity).ToList();

    /// <summary>Cafe Settings → Notification Preferences toggle for each category a
    /// notification-producing flow actually creates (see OrderBuildingService's three
    /// AppNotification sites, InventoryBatchService's low-stock check, and
    /// AttendanceController.PunchOut's shift report). System/Billing/Marketing/AiInsight
    /// have no producer anywhere yet, so they're deliberately not in this map — nothing to
    /// gate, and a toggle for them would control nothing (the exact bug this fixes).</summary>
    private static readonly Dictionary<NotificationCategory, Func<CafeSettings, bool>> NotificationGates = new()
    {
        [NotificationCategory.OrderPlaced] = s => s.OrderPlacedAlertsEnabled,
        [NotificationCategory.OrderPendingConfirmation] = s => s.OrderPendingConfirmationAlertsEnabled,
        [NotificationCategory.Order] = s => s.OrderReadyAlertsEnabled,
        [NotificationCategory.Inventory] = s => s.InventoryAlertsEnabled,
        [NotificationCategory.Staff] = s => s.ShiftReportsEnabled,
    };

    /// <summary>Runs BEFORE the actual save so a gated-off notification is detached and
    /// never persisted at all (not saved, not pushed) — not just filtered out of the push,
    /// which would still leave a dead row cluttering the in-app Notification Center. Reads
    /// TenantId off each entry the same "before SaveChanges" way CollectRealtimeAffectedTenantIds
    /// does, since StampTenantIds above already populated it.</summary>
    private async Task SuppressDisabledNotificationsAsync(CancellationToken ct)
    {
        var gated = ChangeTracker.Entries<AppNotification>()
            .Where(e => e.State == EntityState.Added && NotificationGates.ContainsKey(e.Entity.Category))
            .ToList();
        if (gated.Count == 0) return;

        var tenantIds = gated.Select(e => e.Entity.TenantId).Distinct().ToList();
        var settingsByTenant = await Settings.IgnoreQueryFilters()
            .Where(s => tenantIds.Contains(s.TenantId))
            .ToDictionaryAsync(s => s.TenantId, ct);

        foreach (var entry in gated)
        {
            if (settingsByTenant.TryGetValue(entry.Entity.TenantId, out var settings) && !NotificationGates[entry.Entity.Category](settings))
                entry.State = EntityState.Detached;
        }
    }

    // Only these two QSR/token-order transitions ever push a WhatsApp update — New (shown to
    // the customer as "Pending") is answered by the inbound TRACK reply instead, since no
    // WhatsApp number is linked yet at order-creation time, and Served is explicitly excluded
    // per product decision (see docs/plans — "never send on Served").
    private static readonly HashSet<OrderStatus> WhatsAppNotifiableStatuses = [OrderStatus.Preparing, OrderStatus.Ready];

    /// <summary>Same "read before SaveChanges, act after" shape as
    /// CollectRealtimeAffectedTenantIds — scoped to QSR/token orders only (this module's
    /// current pass) and to the two statuses that ever trigger an automatic WhatsApp push.
    /// Purely observational over the ChangeTracker: no existing call site of
    /// OrderBuildingService.RecomputeOrderStatus changes.</summary>
    private List<(int TenantId, int OrderId, OrderStatus NewStatus)> CollectWhatsAppStatusTransitions() =>
        ChangeTracker.Entries<Order>()
            .Where(e => e.State == EntityState.Modified && e.Entity.OrderType == "QSR"
                && e.Property(nameof(Order.Status)).IsModified
                && WhatsAppNotifiableStatuses.Contains(e.Entity.Status)
                && (OrderStatus)e.OriginalValues[nameof(Order.Status)]! != e.Entity.Status)
            .Select(e => (e.Entity.TenantId, e.Entity.Id, e.Entity.Status))
            .ToList();

    /// <summary>Fires once a QSR order flips Paid false -> true (see OrdersController.
    /// CloseOrderAsync) — the trigger for the automatic bill-PDF WhatsApp send.</summary>
    private List<(int TenantId, int OrderId)> CollectWhatsAppBillGeneratedOrders() =>
        ChangeTracker.Entries<Order>()
            .Where(e => e.State == EntityState.Modified && e.Entity.OrderType == "QSR"
                && e.Property(nameof(Order.Paid)).IsModified
                && (bool)e.OriginalValues[nameof(Order.Paid)]! == false && e.Entity.Paid)
            .Select(e => (e.Entity.TenantId, e.Entity.Id))
            .ToList();

    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        StampTenantIds();
        var affectedTenantIds = CollectRealtimeAffectedTenantIds();
        await SuppressDisabledNotificationsAsync(cancellationToken);
        var newNotifications = CollectNewNotifications();
        var whatsAppStatusTransitions = CollectWhatsAppStatusTransitions();
        var whatsAppBillGenerated = CollectWhatsAppBillGeneratedOrders();
        var result = await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        // Fire-and-forget: the save already succeeded, a slow/failed push must never make the
        // caller's request slower or fail because of it (see RealtimeNotifier).
        if (affectedTenantIds.Count > 0)
            _ = realtime.NotifyOrdersChangedAsync(affectedTenantIds);
        // Every AppNotification create path (OrderBuildingService's order-placed/pending-
        // confirmation/ready-to-serve, plus NotificationsController's manual POST) gets a push
        // for free from this single hook — no call site has to remember to fire one itself.
        // IPushNotificationSender opens its own DbContext scope (see its own comment), so this
        // can't reuse `this` — it may already be disposed by the time the fire-and-forget task
        // actually runs.
        // CancellationToken.None, not the request's token: this keeps running after the
        // request scope (and its RequestAborted token) is gone, same as the realtime notify above.
        foreach (var n in newNotifications)
            _ = pushSender.NotifyTenantAsync(n.TenantId, n.Category, n.Title, n.Body, n.ActionUrl, n.TargetUserId, CancellationToken.None);
        // WhatsApp order-tracking module (see docs/plans — "WhatsApp Order Tracking Module").
        // IWhatsAppEventPublisher is a thin, swappable HTTP push into the standalone Node
        // whatsapp-service — CafePosApi has no idea whether Baileys, WhatsApp Cloud API, or
        // nothing is listening; same fire-and-forget/never-throw contract as the pushes above.
        foreach (var (tenantId, orderId, newStatus) in whatsAppStatusTransitions)
            _ = whatsAppEvents.NotifyOrderStatusChangedAsync(tenantId, orderId, newStatus, CancellationToken.None);
        foreach (var (tenantId, orderId) in whatsAppBillGenerated)
            _ = whatsAppEvents.NotifyBillGeneratedAsync(tenantId, orderId, CancellationToken.None);
        return result;
    }
}
