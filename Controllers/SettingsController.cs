using CafePOS.Api.Contracts;
using CafePOS.Api.Data;
using CafePOS.Api.Domain;
using CafePOS.Api.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CafePOS.Api.Controllers;

[ApiController]
[Route("api/settings")]
public class SettingsController(CafePosDbContext db, IAuditService audit, ITaxRateCache taxRateCache, IImageStorageService imageStorage) : ControllerBase
{
    /// <summary>
    /// Public — the app's theme/branding must render before login (splash,
    /// login screen background) and the QR menu is customer-facing too.
    /// Nothing sensitive lives on this record.
    /// </summary>
    [AllowAnonymous]
    [HttpGet]
    public async Task<CafeSettings> Get()
    {
        var settings = await db.Settings.FirstAsync();
        settings.TenantSlug = (await db.Tenants.FindAsync(settings.TenantId))?.Slug;

        // Self-heal: HasCompletedOnboarding only ever gets set by the last step of the
        // onboarding wizard (OnboardingCrewScreen.finish -> POST complete-onboarding). If
        // that step never ran (e.g. crew creation threw partway through and the request
        // was never reached), the flag is permanently stuck false and every login for this
        // tenant gets routed back into onboarding — even though the cafe is already in
        // real use. Treat any tenant with actual menu/staff/order data as onboarded.
        if (!settings.HasCompletedOnboarding &&
            (await db.MenuItems.AnyAsync() || await db.Staff.AnyAsync() || await db.Orders.AnyAsync()))
        {
            settings.HasCompletedOnboarding = true;
            await db.SaveChangesAsync();
        }

        return settings;
    }

    [Authorize(Policy = Policies.OwnerOrManager)]
    [HttpPut]
    public async Task<ActionResult<CafeSettings>> Update(UpdateSettingsRequest req)
    {
        var settings = await db.Settings.FirstAsync();

        if (req.TaxRatePct is not null)
        {
            if (req.TaxRatePct is < 0 or > 100)
                throw new ApiValidationException("Tax rate must be between 0 and 100.");
            settings.TaxRatePct = req.TaxRatePct.Value;
        }
        if (req.Currency is not null) settings.Currency = req.Currency;
        if (req.Region is not null) settings.Region = req.Region;
        if (req.BusinessName is not null) settings.BusinessName = req.BusinessName.Trim();
        if (req.ReceiptHeader is not null) settings.ReceiptHeader = req.ReceiptHeader;
        if (req.ReceiptFooter is not null) settings.ReceiptFooter = req.ReceiptFooter;
        if (req.LogoUrl is not null) settings.LogoUrl = await imageStorage.ResolveAsync("cafe-logo", req.LogoUrl);
        if (req.PrimaryColor is not null) settings.PrimaryColor = req.PrimaryColor;
        if (req.QrStyle is not null) settings.QrStyle = req.QrStyle;
        if (req.ThemeMode is not null) settings.ThemeMode = req.ThemeMode;
        if (req.TwoFactorEnabled is not null) settings.TwoFactorEnabled = req.TwoFactorEnabled.Value;
        if (req.TerminalPasscodeRequired is not null) settings.TerminalPasscodeRequired = req.TerminalPasscodeRequired.Value;
        if (req.InventoryAlertsEnabled is not null) settings.InventoryAlertsEnabled = req.InventoryAlertsEnabled.Value;
        if (req.ShiftReportsEnabled is not null) settings.ShiftReportsEnabled = req.ShiftReportsEnabled.Value;
        if (req.OrderPlacedAlertsEnabled is not null) settings.OrderPlacedAlertsEnabled = req.OrderPlacedAlertsEnabled.Value;
        if (req.OrderPendingConfirmationAlertsEnabled is not null) settings.OrderPendingConfirmationAlertsEnabled = req.OrderPendingConfirmationAlertsEnabled.Value;
        if (req.OrderReadyAlertsEnabled is not null) settings.OrderReadyAlertsEnabled = req.OrderReadyAlertsEnabled.Value;
        if (req.RequireStaffOrderConfirmation is not null) settings.RequireStaffOrderConfirmation = req.RequireStaffOrderConfirmation.Value;
        if (req.Phone is not null) settings.Phone = req.Phone.Trim();
        if (req.Address is not null) settings.Address = req.Address.Trim();
        if (req.StoreHoursJson is not null) settings.StoreHoursJson = req.StoreHoursJson;
        if (req.KdsStageMode is not null)
        {
            if (req.KdsStageMode is not ("TWO_STAGE" or "THREE_STAGE"))
                throw new ApiValidationException("KdsStageMode must be TWO_STAGE or THREE_STAGE.");
            settings.KdsStageMode = req.KdsStageMode;
        }
        if (req.DineInEnabled is not null) settings.DineInEnabled = req.DineInEnabled.Value;
        if (req.TakeawayEnabled is not null) settings.TakeawayEnabled = req.TakeawayEnabled.Value;
        if (req.DeliveryEnabled is not null) settings.DeliveryEnabled = req.DeliveryEnabled.Value;
        if (req.QsrEnabled is not null) settings.QsrEnabled = req.QsrEnabled.Value;
        if (req.CashEnabled is not null) settings.CashEnabled = req.CashEnabled.Value;
        if (!(settings.DineInEnabled || settings.TakeawayEnabled || settings.DeliveryEnabled || settings.QsrEnabled || settings.CashEnabled))
            throw new ApiValidationException("At least one order type must stay enabled.");

        if (req.ReceiptShowAddress is not null) settings.ReceiptShowAddress = req.ReceiptShowAddress.Value;
        if (req.ReceiptShowWaiterName is not null) settings.ReceiptShowWaiterName = req.ReceiptShowWaiterName.Value;
        if (req.ReceiptShowGuestPhone is not null) settings.ReceiptShowGuestPhone = req.ReceiptShowGuestPhone.Value;
        if (req.ReceiptShowItemNotes is not null) settings.ReceiptShowItemNotes = req.ReceiptShowItemNotes.Value;
        if (req.ReceiptShowFooter is not null) settings.ReceiptShowFooter = req.ReceiptShowFooter.Value;
        if (req.GstNumber is not null) settings.GstNumber = req.GstNumber.Trim();
        if (req.Latitude is not null) settings.Latitude = req.Latitude;
        if (req.Longitude is not null) settings.Longitude = req.Longitude;

        await db.SaveChangesAsync();
        taxRateCache.Invalidate(settings.TenantId);
        await audit.LogAsync(AuditAction.SettingsChange, AuditResource.Settings, null, "Cafe settings updated.", AuditSeverity.Medium);
        return settings;
    }

    [Authorize]
    [HttpPost("complete-onboarding")]
    public async Task<IActionResult> CompleteOnboarding()
    {
        var settings = await db.Settings.FirstAsync();
        settings.HasCompletedOnboarding = true;
        await db.SaveChangesAsync();
        return NoContent();
    }
}
