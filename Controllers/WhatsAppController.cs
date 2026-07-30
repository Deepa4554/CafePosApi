using CafePOS.Api.Contracts;
using CafePOS.Api.Data;
using CafePOS.Api.Domain;
using CafePOS.Api.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CafePOS.Api.Controllers;

/// <summary>
/// Staff-facing WhatsApp pairing/settings API — the RN app never talks to the Node
/// whatsapp-service directly, only through here (see WhatsAppNodeClient), so tenant auth
/// (the normal JWT/OwnerOrManager gate) is enforced in exactly one place. Connecting a
/// business WhatsApp number is an owner/manager action, same bar as IntegrationsController.
/// </summary>
[ApiController]
[Route("api/whatsapp")]
[Authorize(Policy = Policies.OwnerOrManager)]
[Authorize(Policy = Policies.RequirePlus)]
public class WhatsAppController(CafePosDbContext db, ITenantContext tenantContext, WhatsAppNodeClient nodeClient) : ControllerBase
{
    [HttpGet("status")]
    public async Task<ActionResult<WhatsAppStaffStatusDto>> Status()
    {
        var tenantId = tenantContext.TenantIdOrDefault;
        var session = await db.WhatsAppSessions.FirstOrDefaultAsync(s => s.TenantId == tenantId);
        var settings = await db.Settings.FirstAsync();
        return new WhatsAppStaffStatusDto(
            (session?.Status ?? WhatsAppSessionStatus.Disconnected).ToString(),
            session?.PhoneNumberE164,
            session?.ConnectedAt,
            settings.WhatsAppOrderUpdatesEnabled);
    }

    [HttpPost("pair")]
    public async Task<ActionResult<WhatsAppPairResponseDto>> Pair()
    {
        if (!nodeClient.IsConfigured) throw new ApiConflictException("The WhatsApp connector service isn't configured on this server yet.");
        var result = await nodeClient.PairAsync(tenantContext.TenantIdOrDefault, HttpContext.RequestAborted);
        if (result is null) throw new ApiConflictException("Couldn't reach the WhatsApp connector service — check it's running.");
        return new WhatsAppPairResponseDto(result.QrDataUrl, result.Status);
    }

    /// <summary>Proxies whatsapp-service's live QR-refresh/connection-status SSE stream —
    /// keeps Node fully behind CafePosApi (see WhatsAppNodeClient's doc comment).</summary>
    [HttpGet("qr-stream")]
    public async Task QrStream()
    {
        if (!nodeClient.IsConfigured)
        {
            Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }

        using var upstream = await nodeClient.OpenQrStreamAsync(tenantContext.TenantIdOrDefault, HttpContext.RequestAborted);
        Response.StatusCode = (int)upstream.StatusCode;
        Response.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache";
        var stream = await upstream.Content.ReadAsStreamAsync(HttpContext.RequestAborted);
        await stream.CopyToAsync(Response.Body, HttpContext.RequestAborted);
    }

    [HttpPost("logout")]
    public async Task<IActionResult> Logout()
    {
        var tenantId = tenantContext.TenantIdOrDefault;
        if (nodeClient.IsConfigured) await nodeClient.LogoutAsync(tenantId, HttpContext.RequestAborted);

        var session = await db.WhatsAppSessions.FirstOrDefaultAsync(s => s.TenantId == tenantId);
        if (session is not null)
        {
            session.Status = WhatsAppSessionStatus.Disconnected;
            session.PhoneNumberE164 = null;
            session.ConnectedAt = null;
        }

        var integration = await db.Integrations.FirstOrDefaultAsync(i => i.Name == "WhatsApp Business");
        if (integration is not null)
        {
            integration.Status = IntegrationStatus.Disconnected;
            integration.ConnectedAt = null;
        }

        await db.SaveChangesAsync();
        return NoContent();
    }

    [HttpPatch("settings")]
    public async Task<IActionResult> UpdateSettings(UpdateWhatsAppSettingsRequest req)
    {
        var settings = await db.Settings.FirstAsync();
        settings.WhatsAppOrderUpdatesEnabled = req.UpdatesEnabled;
        await db.SaveChangesAsync();
        return NoContent();
    }
}
