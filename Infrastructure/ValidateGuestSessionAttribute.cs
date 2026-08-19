using CafePOS.Api.Data;
using CafePOS.Api.Domain;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;

namespace CafePOS.Api.Infrastructure;

public static class GuestSessionHttpContextExtensions
{
    private const string ItemKey = "GuestSession";

    public static GuestSession GetGuestSession(this HttpContext context) =>
        (GuestSession)context.Items[ItemKey]!;
}

/// <summary>
/// Implements the doc's validateSession() (Section 4) exactly, run on every
/// guest-session-scoped request except `scan`/`join` (the entry points — see
/// GuestSessionController). On success, stashes the loaded (tracked) GuestSession on
/// HttpContext.Items so the action can read/mutate it via GetGuestSession() without a
/// second query re-tracking the same row.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public class ValidateGuestSessionAttribute(bool isWrite = false) : Attribute, IAsyncActionFilter
{
    private static readonly TimeSpan InactivityLimit = TimeSpan.FromMinutes(45);

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var db = context.HttpContext.RequestServices.GetRequiredService<CafePosDbContext>();
        var sessionService = context.HttpContext.RequestServices.GetRequiredService<IGuestSessionService>();

        var rawToken = sessionService.ReadCookie(context.HttpContext.Request);
        if (rawToken is null)
        {
            context.Result = Problem(StatusCodes.Status401Unauthorized, "Scan the QR code again.");
            return;
        }

        var tokenHash = sessionService.Hash(rawToken);
        var device = await db.SessionDevices.IgnoreQueryFilters().FirstOrDefaultAsync(d => d.TokenHash == tokenHash);
        if (device is null)
        {
            context.Result = Problem(StatusCodes.Status401Unauthorized, "Session not found.");
            return;
        }
        var session = await db.GuestSessions.IgnoreQueryFilters().FirstOrDefaultAsync(s => s.Id == device.SessionId);
        if (session is null)
        {
            context.Result = Problem(StatusCodes.Status401Unauthorized, "Session not found.");
            return;
        }

        if (session.Status is GuestSessionStatus.Closed or GuestSessionStatus.Revoked)
        {
            sessionService.ClearCookie(context.HttpContext.Response);
            // StaffClosed + a cancelled order = staff rejected/cancelled this guest's order
            // (see OrdersController.Cancel's session hook) — say so, rather than the generic
            // "ended" a settled bill gets, so the guest isn't left staring at a reset menu.
            var declined = session.ClosedReason == SessionCloseReason.StaffClosed
                && session.OrderId is int closedOrderId
                && await db.Orders.IgnoreQueryFilters().AnyAsync(o => o.Id == closedOrderId && o.Cancelled);
            context.Result = Problem(StatusCodes.Status410Gone, declined
                ? "The cafe couldn't accept your order. Please speak to a staff member."
                : "This session has ended.", SettledReceiptToken(context.HttpContext, session, declined));
            return;
        }

        var now = DateTime.UtcNow;
        if (session.Status == GuestSessionStatus.Expired || now > session.ExpiresAt || now - session.LastActivity > InactivityLimit)
        {
            session.Status = GuestSessionStatus.Expired;
            session.ClosedReason = SessionCloseReason.Timeout;
            session.ClosedAt = now;
            await db.SaveChangesAsync();
            sessionService.ClearCookie(context.HttpContext.Response);
            context.Result = Problem(StatusCodes.Status410Gone, "Session expired — please scan the QR code again.");
            return;
        }

        if (session.Status == GuestSessionStatus.Locked && isWrite)
        {
            context.Result = Problem(StatusCodes.Status423Locked, "Bill requested — ordering is closed.");
            return;
        }

        // Cross-check with POS state (doc Section 4, "CRITICAL"): the cashier may have
        // settled this table from the POS in the same window the guest's own cookie is
        // still technically alive — catch it here rather than waiting on the sweep.
        if (session.OrderId is int orderId)
        {
            var orderState = await db.Orders.IgnoreQueryFilters()
                .Where(o => o.Id == orderId).Select(o => new { o.Paid, o.Status }).FirstOrDefaultAsync();
            if (orderState is { Paid: true, Status: OrderStatus.Served })
            {
                session.Status = GuestSessionStatus.Closed;
                session.ClosedReason = SessionCloseReason.Settled;
                session.ClosedAt = now;
                await db.SaveChangesAsync();
                sessionService.ClearCookie(context.HttpContext.Response);
                context.Result = Problem(StatusCodes.Status410Gone, "This session has ended.",
                    SettledReceiptToken(context.HttpContext, session, declined: false));
                return;
            }
        }

        session.LastActivity = now;
        device.LastSeen = now;
        await db.SaveChangesAsync();

        context.HttpContext.Items["GuestSession"] = session;
        await next();
    }

    /// <summary>
    /// The public bill-PDF token for a session that ended because its bill was SETTLED, and
    /// null for every other ending.
    ///
    /// A settled session is the one ending that isn't a failure: the guest ate, staff took the
    /// money, and the only thing left to hand over is the bill. Until this, all they got was a
    /// dead "This session has ended." — so the 410 carries the token and the page turns it into
    /// a bill they can read and download (see PublicController.GetReceipt, which renders the
    /// same ReceiptPdfBuilder output the WhatsApp bill link uses).
    ///
    /// Safe to hand out here: reaching this line at all required presenting the session cookie
    /// for THIS session, so the caller is the device that placed the order. The token is
    /// signed and scoped to that one order (see ReceiptTokenService) — it grants nothing else.
    /// </summary>
    private static string? SettledReceiptToken(HttpContext http, GuestSession session, bool declined)
    {
        if (declined || session.ClosedReason != SessionCloseReason.Settled) return null;
        if (session.OrderId is not int orderId) return null;
        return http.RequestServices.GetRequiredService<ReceiptTokenService>().Encode(orderId);
    }

    private static ObjectResult Problem(int status, string message, string? receiptToken = null)
    {
        var details = new ProblemDetails { Status = status, Title = message };
        if (receiptToken is not null) details.Extensions["receiptToken"] = receiptToken;
        return new ObjectResult(details) { StatusCode = status };
    }
}
