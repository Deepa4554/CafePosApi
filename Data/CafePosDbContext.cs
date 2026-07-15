using System.Reflection;
using CafePOS.Api.Domain;
using CafePOS.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace CafePOS.Api.Data;

public class CafePosDbContext(DbContextOptions<CafePosDbContext> options, ITenantContext tenant) : DbContext(options)
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
    public DbSet<MenuItemImage> MenuItemImages => Set<MenuItemImage>();
    public DbSet<CafeTable> Tables => Set<CafeTable>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderItem> OrderItems => Set<OrderItem>();
    public DbSet<InventoryItem> InventoryItems => Set<InventoryItem>();
    public DbSet<CafeSettings> Settings => Set<CafeSettings>();

    // Auth
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<RefreshTokenEntry> RefreshTokens => Set<RefreshTokenEntry>();
    public DbSet<EmailOtp> EmailOtps => Set<EmailOtp>();

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
    public DbSet<SupportTicket> SupportTickets => Set<SupportTicket>();
    public DbSet<SupportTicketMessage> SupportTicketMessages => Set<SupportTicketMessage>();

    // Recipe-based inventory
    public DbSet<Recipe> Recipes => Set<Recipe>();
    public DbSet<RecipeItem> RecipeItems => Set<RecipeItem>();
    public DbSet<InventoryTransaction> InventoryTransactions => Set<InventoryTransaction>();
    public DbSet<PurchaseOrder> PurchaseOrders => Set<PurchaseOrder>();
    public DbSet<PurchaseItem> PurchaseItems => Set<PurchaseItem>();

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

        // Store enums as readable strings rather than opaque ints.
        modelBuilder.Entity<Order>().Property(o => o.Status).HasConversion<string>();
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
        modelBuilder.Entity<SupportTicket>().Property(t => t.Status).HasConversion<string>();
        modelBuilder.Entity<MenuItem>().Property(m => m.ProductType).HasConversion<string>();
        modelBuilder.Entity<InventoryTransaction>().Property(t => t.Type).HasConversion<string>();

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
        // Looked up on every single authenticated request that needs a token refresh.
        modelBuilder.Entity<RefreshTokenEntry>().HasIndex(t => t.Token).IsUnique();
        modelBuilder.Entity<RefreshTokenEntry>().HasIndex(t => t.UserId);
        // Always sorted newest-first and commonly filtered by status — this table
        // grows with every failed request, so both need to stay index-backed.
        modelBuilder.Entity<ApiFailureLog>().HasIndex(f => f.Timestamp);
        modelBuilder.Entity<ApiFailureLog>().HasIndex(f => f.StatusCode);

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

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        StampTenantIds();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }
}
