using CafePOS.Api.Contracts;
using CafePOS.Api.Data;
using CafePOS.Api.Domain;
using CafePOS.Api.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace CafePOS.Api.Controllers;

/// <summary>
/// Guest QR-ordering session lifecycle — scan → validate → cart → order → bill (Phase 1
/// of docs/qr-ordering-session-plan; see GuestSession/SessionDevice for the data model).
/// Every route sits under the same encrypted QrToken (tenantId+tableCode, see
/// QrTokenService) PublicController/OrdersController.CreatePublic already use — this
/// controller only adds the session layer on top, it doesn't replace that token scheme.
/// scan/join are the two entry points (no session cookie required yet); every other
/// action is gated by ValidateGuestSessionAttribute, which implements the doc's
/// validateSession() exactly.
/// </summary>
[ApiController]
[Route("api/public/{token}/session")]
[AllowAnonymous]
[EnableRateLimiting("GuestSessionLimiter")]
public class GuestSessionController(
    CafePosDbContext db, QrTokenService qrTokens, IGuestSessionService sessionService, IOrderBuildingService orderBuilder) : ControllerBase
{
    private const int HardTtlHours = 4;

    private async Task<(int TenantId, string TableCode, CafeTable Table)?> ResolveAsync(string token)
    {
        var decoded = qrTokens.TryDecode(token);
        if (decoded is null) return null;
        var (tenantId, tableCode) = decoded.Value;
        // Empty table code is the generic "menu only" QR (see TablesController.GetMenuOnlyQrToken)
        // — no seat, so no session either, same rule OrdersController.CreatePublic enforces.
        if (string.IsNullOrEmpty(tableCode)) return null;
        var table = await db.Tables.IgnoreQueryFilters().FirstOrDefaultAsync(t => t.TenantId == tenantId && t.Code == tableCode);
        return table is null ? null : (tenantId, tableCode, table);
    }

    private async Task<Order?> LoadOrderAsync(int? orderId) =>
        orderId is int oid
            ? await db.Orders.IgnoreQueryFilters().Include(o => o.Items).ThenInclude(i => i.SelectedModifiers).Include(o => o.FireBatches).Include(o => o.Payments).FirstOrDefaultAsync(o => o.Id == oid)
            : null;

    /// <summary>Entry point — implements the doc's 5-CASE decision tree (Section 3). No
    /// session cookie required to call this; it's what creates/finds/refuses one.</summary>
    [HttpPost("scan")]
    public async Task<ActionResult<ScanResultDto>> Scan(string token)
    {
        var resolved = await ResolveAsync(token);
        if (resolved is null) throw new ApiValidationException("This ordering link is invalid. Please re-scan the QR code.");
        var (tenantId, tableCode, table) = resolved.Value;

        var session = await db.GuestSessions.IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.TableId == table.Id && (s.Status == GuestSessionStatus.Active || s.Status == GuestSessionStatus.Locked));

        if (session is null)
        {
            // CASE 5: no live session, but an unpaid/unserved order already sits here
            // (e.g. a waiter took it, or the previous guest's session expired without
            // settling) — never silently start a fresh session over someone else's tab.
            var staffAssist = await db.Orders.IgnoreQueryFilters()
                .AnyAsync(o => o.TenantId == tenantId && o.TableCode == tableCode && !o.Cancelled && (!o.Paid || o.Status != OrderStatus.Served));
            if (staffAssist) return new ScanResultDto("STAFF_ASSIST", null);

            // CASE 1: fresh table, fresh session.
            session = new GuestSession { TenantId = tenantId, TableId = table.Id, ExpiresAt = DateTime.UtcNow.AddHours(HardTtlHours) };
            db.GuestSessions.Add(session);
            await db.SaveChangesAsync();

            var (raw, hash) = sessionService.IssueToken();
            db.SessionDevices.Add(new SessionDevice { TenantId = tenantId, SessionId = session.Id, TokenHash = hash, UserAgent = Request.Headers["User-Agent"].ToString() });
            await db.SaveChangesAsync();
            sessionService.SetCookie(Response, raw, session.ExpiresAt);

            return new ScanResultDto("READY", GuestSessionStateDto.From(session, tableCode, null));
        }

        if (session.Status == GuestSessionStatus.Locked)
            return new ScanResultDto("BILL_LOCKED", GuestSessionStateDto.From(session, tableCode, await LoadOrderAsync(session.OrderId)));

        // ACTIVE — is the caller's own cookie already one of this session's devices
        // (CASE 2, e.g. a page refresh), or a phone that's never seen this table (CASE 3)?
        var rawCookie = sessionService.ReadCookie(Request);
        if (rawCookie is not null)
        {
            var hash = sessionService.Hash(rawCookie);
            var isOwn = await db.SessionDevices.IgnoreQueryFilters().AnyAsync(d => d.SessionId == session.Id && d.TokenHash == hash);
            if (isOwn) return new ScanResultDto("READY", GuestSessionStateDto.From(session, tableCode, await LoadOrderAsync(session.OrderId)));
        }

        return new ScanResultDto("JOIN", null);
    }

    /// <summary>CASE 3 confirmation — "yes, I'm with this table". Mints THIS device its own
    /// token (see SessionDevice doc comment) rather than trying to hand over the existing
    /// one, which the server can't recover once hashed.</summary>
    [HttpPost("join")]
    public async Task<ActionResult<GuestSessionStateDto>> Join(string token)
    {
        var resolved = await ResolveAsync(token);
        if (resolved is null) throw new ApiValidationException("This ordering link is invalid. Please re-scan the QR code.");
        var (tenantId, tableCode, table) = resolved.Value;

        var session = await db.GuestSessions.IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.TableId == table.Id && (s.Status == GuestSessionStatus.Active || s.Status == GuestSessionStatus.Locked));
        if (session is null) throw new ApiConflictException("No active session on this table to join — please scan again.");

        var (raw, hash) = sessionService.IssueToken();
        db.SessionDevices.Add(new SessionDevice { TenantId = tenantId, SessionId = session.Id, TokenHash = hash, UserAgent = Request.Headers["User-Agent"].ToString() });
        await db.SaveChangesAsync();
        sessionService.SetCookie(Response, raw, session.ExpiresAt);

        return GuestSessionStateDto.From(session, tableCode, await LoadOrderAsync(session.OrderId));
    }

    /// <summary>Polled by the guest page (no WebSocket in this stack — see plan) to reflect
    /// KOT status changes and a bill-lock triggered from another joined device.</summary>
    [ValidateGuestSession]
    [HttpGet("state")]
    public async Task<ActionResult<GuestSessionStateDto>> State()
    {
        var session = HttpContext.GetGuestSession();
        var table = await db.Tables.IgnoreQueryFilters().FirstAsync(t => t.Id == session.TableId);
        return GuestSessionStateDto.From(session, table.Code, await LoadOrderAsync(session.OrderId));
    }

    /// <summary>Adds/updates/removes one cart line (qty 0 = remove). Lazily creates the
    /// underlying (still-unfired) Order on the very first call — see the cart-model note on
    /// GuestSession.OrderId.</summary>
    [ValidateGuestSession(isWrite: true)]
    [HttpPost("cart/items")]
    public async Task<ActionResult<GuestSessionStateDto>> AddCartItem(AddCartItemRequest req)
    {
        var session = HttpContext.GetGuestSession();
        var table = await db.Tables.IgnoreQueryFilters().FirstAsync(t => t.Id == session.TableId);

        if (session.OrderId is null)
        {
            if (req.Qty <= 0) throw new ApiValidationException("Quantity must be at least 1.");
            // Same mandatory-phone rule as OrdersController.Create/CreatePublic — matches
            // this guest's order to a Customer by phone (see FindOrCreateCustomerAsync).
            var normalizedPhone = string.IsNullOrWhiteSpace(req.GuestPhone) ? null : new string(req.GuestPhone.Where(char.IsDigit).ToArray());
            if (normalizedPhone is null || normalizedPhone.Length != 10)
                throw new ApiValidationException("A valid 10-digit mobile number is required.");

            var order = await orderBuilder.BuildOrderAsync(db, "DINE_IN", table.Code, req.GuestName,
                items: [new CreateOrderItemDto(req.MenuItemId, req.Qty, req.Modifier, req.VariantId, req.ModifierOptionIds)],
                discountPct: 0, user: null, explicitTenantId: session.TenantId, guestPhone: normalizedPhone);
            session.OrderId = order.Id;
            await db.SaveChangesAsync();
            return GuestSessionStateDto.From(session, table.Code, order);
        }

        var existingOrder = await LoadOrderAsync(session.OrderId) ?? throw new ApiConflictException("Order not found.");
        if (existingOrder.Paid) throw new ApiConflictException("This order has already been paid.");

        await orderBuilder.AddOrUpdateCartItemAsync(db, existingOrder, req.MenuItemId, req.Qty, req.Modifier, session.TenantId, req.VariantId, req.ModifierOptionIds);
        await db.SaveChangesAsync();
        return GuestSessionStateDto.From(session, table.Code, existingOrder);
    }

    /// <summary>Fires everything currently unfired in the cart — first call creates the KOT,
    /// later calls fire newly-added items as a fresh batch on the same order (doc Section 6
    /// step 5, "repeat orders"). If CafeSettings.RequireStaffOrderConfirmation is on AND this
    /// is the session's very first submission (CurrentFireBatch still 0), it doesn't fire at
    /// all — it flags the order PendingStaffConfirmation and waits for a staff member to hit
    /// Confirm (OrdersController.ConfirmGuestOrder) instead. Every later call on an
    /// already-confirmed session (CurrentFireBatch > 0) fires immediately, same as before —
    /// only the very first table-trust check is gated, not every re-order.</summary>
    [ValidateGuestSession(isWrite: true)]
    [HttpPost("order")]
    public async Task<ActionResult<GuestSessionStateDto>> PlaceOrder()
    {
        var session = HttpContext.GetGuestSession();
        var table = await db.Tables.IgnoreQueryFilters().FirstAsync(t => t.Id == session.TableId);
        if (session.OrderId is null) throw new ApiValidationException("Your cart is empty.");

        var order = await LoadOrderAsync(session.OrderId) ?? throw new ApiConflictException("Order not found.");
        if (order.Paid) throw new ApiConflictException("This order has already been paid.");

        var hasUnfiredItems = order.Items.Any(i => i.FireBatch == 0 && !i.Voided);
        if (!hasUnfiredItems && !order.PendingStaffConfirmation)
            throw new ApiValidationException("No new items to fire — add something to your cart first.");

        try
        {
            var settings = await db.Settings.IgnoreQueryFilters().FirstAsync(s => s.TenantId == session.TenantId);
            var needsConfirmation = settings.RequireStaffOrderConfirmation && order.CurrentFireBatch == 0;

            if (needsConfirmation)
            {
                orderBuilder.MarkPendingConfirmation(db, order, session.TenantId);
            }
            else if (!await orderBuilder.FireUnfiredItemsAsync(db, order, session.TenantId))
            {
                throw new ApiValidationException("No new items to fire — add something to your cart first.");
            }
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            throw new ApiConflictException("This order was already placed — please try again.");
        }
        return GuestSessionStateDto.From(session, table.Code, order);
    }

    /// <summary>Doc Section 5.2 — soft invalidation. Blocks further ordering (see
    /// ValidateGuestSessionAttribute's isWrite check) without ending the session; the guest
    /// can still view the bill and pay.</summary>
    [ValidateGuestSession]
    [HttpPost("request-bill")]
    public async Task<ActionResult<GuestSessionStateDto>> RequestBill()
    {
        var session = HttpContext.GetGuestSession();
        var table = await db.Tables.IgnoreQueryFilters().FirstAsync(t => t.Id == session.TableId);
        if (session.OrderId is null) throw new ApiValidationException("You haven't placed an order yet.");

        session.Status = GuestSessionStatus.Locked;
        await db.SaveChangesAsync();

        return GuestSessionStateDto.From(session, table.Code, await LoadOrderAsync(session.OrderId));
    }

    /// <summary>Bill view — "Pay at Counter" only; no online gateway exists in this codebase
    /// yet (Phase 2, see plan). Settling (staff-side OrdersController.Pay) is what actually
    /// closes the session (doc Section 5.1) — this endpoint is read-only.</summary>
    [ValidateGuestSession]
    [HttpGet("bill")]
    public async Task<ActionResult<OrderDto>> Bill()
    {
        var session = HttpContext.GetGuestSession();
        if (session.OrderId is null) return NotFound();
        var order = await LoadOrderAsync(session.OrderId);
        return order is null ? NotFound() : OrderDto.From(order);
    }
}
