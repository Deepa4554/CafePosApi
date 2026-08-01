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
    /// <summary>"name@handle", NPCI's shape for a UPI address. Deliberately loose on the
    /// name half (banks allow letters, digits, dot, hyphen, underscore) and on the handle,
    /// which is just a bank/PSP suffix that new providers keep adding to — the point is to
    /// catch a typo or a phone number typed into the wrong box, not to maintain a list of
    /// every valid PSP.</summary>
    private static readonly System.Text.RegularExpressions.Regex UpiVpaPattern =
        new(@"^[a-zA-Z0-9._-]{2,256}@[a-zA-Z][a-zA-Z0-9.]{1,63}$", System.Text.RegularExpressions.RegexOptions.Compiled);

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
        if (req.BusinessType is not null) settings.BusinessType = req.BusinessType;
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
        if (req.ApprovalAlertsEnabled is not null) settings.ApprovalAlertsEnabled = req.ApprovalAlertsEnabled.Value;
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
        if (req.UpiVpa is not null)
        {
            var vpa = req.UpiVpa.Trim();
            // Empty clears it (the Cafe Settings field emptied out) — only a non-empty value
            // has to look like a real address, since an unparseable VPA produces a QR that
            // every UPI app rejects at scan time, long after the bill is in a guest's hands.
            if (vpa.Length > 0 && !UpiVpaPattern.IsMatch(vpa))
                throw new ApiValidationException("Enter a valid UPI ID, like cafename@okaxis.");
            settings.UpiVpa = vpa.Length > 0 ? vpa : null;
        }
        if (req.Latitude is not null) settings.Latitude = req.Latitude;
        if (req.Longitude is not null) settings.Longitude = req.Longitude;

        if (req.ServiceChargeDefaultPct is < 0 or > 100)
            throw new ApiValidationException("Service charge % must be between 0 and 100.");
        if (req.PackingChargeDefaultAmount is < 0)
            throw new ApiValidationException("Packing charge must be 0 or more.");
        if (req.DeliveryChargeDefaultAmount is < 0)
            throw new ApiValidationException("Delivery charge must be 0 or more.");

        if (req.ServiceChargeClearDefault is true) settings.ServiceChargeDefaultPct = null;
        else if (req.ServiceChargeDefaultPct is not null) settings.ServiceChargeDefaultPct = req.ServiceChargeDefaultPct;
        if (req.ServiceChargeAutoApplyDineIn is not null) settings.ServiceChargeAutoApplyDineIn = req.ServiceChargeAutoApplyDineIn.Value;
        if (req.ServiceChargeAutoApplyTakeaway is not null) settings.ServiceChargeAutoApplyTakeaway = req.ServiceChargeAutoApplyTakeaway.Value;
        if (req.ServiceChargeAutoApplyDelivery is not null) settings.ServiceChargeAutoApplyDelivery = req.ServiceChargeAutoApplyDelivery.Value;
        if (req.ServiceChargeAutoApplyToken is not null) settings.ServiceChargeAutoApplyToken = req.ServiceChargeAutoApplyToken.Value;

        if (req.PackingChargeClearDefault is true) settings.PackingChargeDefaultAmount = null;
        else if (req.PackingChargeDefaultAmount is not null) settings.PackingChargeDefaultAmount = req.PackingChargeDefaultAmount;
        if (req.PackingChargeAutoApplyDineIn is not null) settings.PackingChargeAutoApplyDineIn = req.PackingChargeAutoApplyDineIn.Value;
        if (req.PackingChargeAutoApplyTakeaway is not null) settings.PackingChargeAutoApplyTakeaway = req.PackingChargeAutoApplyTakeaway.Value;
        if (req.PackingChargeAutoApplyDelivery is not null) settings.PackingChargeAutoApplyDelivery = req.PackingChargeAutoApplyDelivery.Value;
        if (req.PackingChargeAutoApplyToken is not null) settings.PackingChargeAutoApplyToken = req.PackingChargeAutoApplyToken.Value;

        if (req.DeliveryChargeClearDefault is true) settings.DeliveryChargeDefaultAmount = null;
        else if (req.DeliveryChargeDefaultAmount is not null) settings.DeliveryChargeDefaultAmount = req.DeliveryChargeDefaultAmount;
        if (req.DeliveryChargeAutoApplyDineIn is not null) settings.DeliveryChargeAutoApplyDineIn = req.DeliveryChargeAutoApplyDineIn.Value;
        if (req.DeliveryChargeAutoApplyTakeaway is not null) settings.DeliveryChargeAutoApplyTakeaway = req.DeliveryChargeAutoApplyTakeaway.Value;
        if (req.DeliveryChargeAutoApplyDelivery is not null) settings.DeliveryChargeAutoApplyDelivery = req.DeliveryChargeAutoApplyDelivery.Value;
        if (req.DeliveryChargeAutoApplyToken is not null) settings.DeliveryChargeAutoApplyToken = req.DeliveryChargeAutoApplyToken.Value;

        await db.SaveChangesAsync();
        taxRateCache.Invalidate(settings.TenantId);
        await audit.LogAsync(AuditAction.SettingsChange, AuditResource.Settings, null, "Cafe settings updated.", AuditSeverity.Medium);
        return settings;
    }

    /// <summary>Every NotificationCategory that does NOT have a dedicated CafeSettings column
    /// (see NotificationPreferences.NamedGates) — computed off the enum itself, so a category
    /// added after this endpoint was written shows up here automatically, with no code change.
    /// This is what closes "a new notification category has no enable/disable option": as soon
    /// as any producer creates an AppNotification of that category, it's already gate-able
    /// through this same list.</summary>
    [Authorize(Policy = Policies.OwnerOrManager)]
    [HttpGet("notification-preferences")]
    public async Task<IEnumerable<NotificationCategoryPreferenceDto>> GetNotificationPreferences()
    {
        var settings = await db.Settings.FirstAsync();
        return NotificationPreferences.GenericCategories(settings)
            .Select(kv => new NotificationCategoryPreferenceDto(kv.Key, kv.Value));
    }

    [Authorize(Policy = Policies.OwnerOrManager)]
    [HttpPut("notification-preferences")]
    public async Task<IActionResult> UpdateNotificationPreference(UpdateNotificationCategoryPreferenceRequest req)
    {
        // A NamedGates category already has its own dedicated field on this same controller's
        // main Update (e.g. InventoryAlertsEnabled) — rejecting here instead of silently
        // no-op-ing/double-writing keeps exactly one source of truth per category.
        if (NotificationPreferences.NamedGates.ContainsKey(req.Category))
            throw new ApiValidationException($"{req.Category} has its own dedicated setting — update it via PUT /api/settings instead.");

        var settings = await db.Settings.FirstAsync();
        NotificationPreferences.SetOverride(settings, req.Category, req.Enabled);
        await db.SaveChangesAsync();
        await audit.LogAsync(AuditAction.SettingsChange, AuditResource.Settings, null,
            $"Notification category '{req.Category}' {(req.Enabled ? "enabled" : "disabled")}.", AuditSeverity.Medium);
        return NoContent();
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
