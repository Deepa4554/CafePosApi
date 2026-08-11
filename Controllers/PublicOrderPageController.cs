using CafePOS.Api.Data;
using CafePOS.Api.Infrastructure;
using CafePOS.Api.Public;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CafePOS.Api.Controllers;

/// <summary>
/// Serves the customer-facing QR ordering page. The encrypted token in the URL is
/// purely for the browser/QR image — the page itself is one static document for every
/// table of every cafe; it reads the token client-side (location.pathname) and calls
/// the token-aware anonymous ordering APIs (see PublicController) with it. Neither the
/// cafe nor the table is ever visible in plain text here.
/// </summary>
[ApiController]
[AllowAnonymous]
public class PublicOrderPageController(CafePosDbContext db, QrTokenService qrTokens) : ControllerBase
{
    [HttpGet("/order/{token}")]
    public async Task<IActionResult> Get(string token)
    {
        // The one exception to "always serve the ordering page": a cafe that has uploaded a
        // designed PDF menu and switched it on wants its GENERAL (menu-only) QR to open that
        // PDF instead of the live digital menu. Only the menu-only token qualifies — table QRs
        // (real ordering) and the delivery QR always keep their live flow, so a decode failure
        // or any other mode falls straight through to the normal page below.
        //
        // Serve the in-browser PDF VIEWER page (not the raw file, and not a redirect): a raw
        // application/pdf response is downloaded rather than shown on Android Chrome and on any
        // desktop browser set to "download PDFs", and a redirect into that download is what some
        // phones reject outright ("file cannot be downloaded"). The viewer renders the PDF to
        // canvas with pdf.js so the menu shows inline everywhere. It fetches the bytes itself
        // from the same-origin serve endpoint (PublicController.GetMenuPdf), which stays as the
        // raw-bytes source. Only the menu-only token qualifies — table/delivery QRs keep their
        // live flow, so any other mode falls straight through to the ordering page below.
        var decoded = qrTokens.TryDecode(token);
        if (decoded is not null && QrTokenService.ModeFor(decoded.Value.TableCode) == "menu")
        {
            var hasEnabledPdf = await db.MenuPdfs.IgnoreQueryFilters().AsNoTracking()
                .AnyAsync(p => p.TenantId == decoded.Value.TenantId && p.Enabled);
            if (hasEnabledPdf)
                return Content(MenuPdfViewerPage.Html, "text/html; charset=utf-8");
        }

        return Content(CustomerOrderPage.Html, "text/html; charset=utf-8");
    }
}
