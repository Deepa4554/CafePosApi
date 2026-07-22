using CafePOS.Api.Data;
using CafePOS.Api.Domain;
using FirebaseAdmin;
using FirebaseAdmin.Messaging;
using Microsoft.EntityFrameworkCore;

namespace CafePOS.Api.Infrastructure;

public class FcmOptions
{
    /// <summary>Filesystem path to the Firebase Admin SDK service-account JSON key
    /// (Firebase console → Project settings → Service accounts → Generate new private key).
    /// Left blank, push sending just no-ops — see FcmPushNotificationSender.</summary>
    public string CredentialsPath { get; set; } = "";
}

public interface IPushNotificationSender
{
    /// <summary>Fans a push out to every device token belonging to an active user of this
    /// tenant, mirroring NotificationsController.List's role filter (kitchen roles only ever
    /// get OrderPlaced) — unless <paramref name="targetUserId"/> is set, in which case only
    /// that one user's own devices get it (see AppNotification.TargetUserId). Never throws —
    /// a push failure must never surface as an API error to whatever save triggered it (see
    /// CafePosDbContext.SaveChangesAsync).</summary>
    Task NotifyTenantAsync(int tenantId, NotificationCategory category, string title, string body, string? actionUrl, int? targetUserId = null, CancellationToken ct = default);
}

/// <summary>
/// Sends via Firebase Cloud Messaging if Fcm:CredentialsPath is configured and points at a
/// real service-account key; otherwise logs and no-ops, so local dev/CI never gets blocked on
/// push setup — same fallback shape as SmtpEmailService for Email:GmailAddress/GmailAppPassword.
///
/// Registered as a singleton (see Program.cs) but opens its own DbContext scope per call via
/// IServiceScopeFactory rather than taking a CafePosDbContext directly: the caller
/// (CafePosDbContext.SaveChangesAsync) invokes this fire-and-forget after its own save already
/// completed, so by the time this runs the request's DbContext/scope may already be disposed.
/// </summary>
public class FcmPushNotificationSender(IServiceScopeFactory scopeFactory, ILogger<FcmPushNotificationSender> logger) : IPushNotificationSender
{
    public async Task NotifyTenantAsync(int tenantId, NotificationCategory category, string title, string body, string? actionUrl, int? targetUserId = null, CancellationToken ct = default)
    {
        if (FirebaseApp.DefaultInstance is null) return;

        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<CafePosDbContext>();

            List<string> tokens;
            if (targetUserId is int uid)
            {
                // Targeted (e.g. "task assigned to you") — this one user's devices only,
                // no tenant/role fan-out at all.
                tokens = await db.DeviceTokens.IgnoreQueryFilters()
                    .Where(t => t.TenantId == tenantId && t.UserId == uid)
                    .Select(t => t.Token)
                    .Distinct()
                    .ToListAsync(ct);
            }
            else
            {
                // Kitchen-facing roles only ever see OrderPlaced in-app (NotificationsController.
                // List) — mirrored here so a KDS tablet doesn't buzz for a confirmation prompt or
                // a billing/staff notice it has no screen to act on.
                var kitchenOnlyOrderPlaced = category != NotificationCategory.OrderPlaced;

                // AppUser is deliberately not ITenantScoped (see its own doc comment), and this
                // runs detached from any request — IgnoreQueryFilters on DeviceToken so it isn't
                // at the mercy of whatever ambient tenant a background task happens to inherit.
                tokens = await db.Users
                    .Where(u => u.TenantId == tenantId && u.IsActive)
                    .Where(u => !kitchenOnlyOrderPlaced || (u.Role != AppRole.Chef && u.Role != AppRole.KitchenStaff))
                    .Join(
                        db.DeviceTokens.IgnoreQueryFilters().Where(t => t.TenantId == tenantId),
                        u => u.Id, t => t.UserId, (u, t) => t.Token)
                    .Distinct()
                    .ToListAsync(ct);
            }

            if (tokens.Count == 0) return;

            // MulticastMessage.Tokens is flagged obsolete in favor of .Fids, but Fids addresses
            // Firebase Installation IDs — a different SDK/targeting scheme entirely, not what
            // the RN client registers (see pushNotifications.ts's messaging().getToken()). Plain
            // FCM registration tokens are still fully supported; this just silences that warning.
#pragma warning disable CS0618
            var message = new MulticastMessage
            {
                Tokens = tokens,
                Notification = new Notification { Title = title, Body = body },
                Data = new Dictionary<string, string>
                {
                    ["category"] = category.ToString(),
                    ["actionUrl"] = actionUrl ?? "",
                },
            };
#pragma warning restore CS0618

            var response = await FirebaseMessaging.DefaultInstance.SendEachForMulticastAsync(message, ct);
            if (response.FailureCount == 0) return;

            // A token FCM reports as unregistered/invalid is permanently dead (app uninstalled,
            // Firebase state reset, ...) — clean it up now instead of paying for a failed send to
            // it on every future notification.
            var deadTokens = response.Responses
                .Select((r, i) => (r, token: tokens[i]))
                .Where(x => !x.r.IsSuccess && x.r.Exception?.MessagingErrorCode is
                    MessagingErrorCode.Unregistered or MessagingErrorCode.InvalidArgument)
                .Select(x => x.token)
                .ToList();

            if (deadTokens.Count > 0)
            {
                var stale = await db.DeviceTokens.IgnoreQueryFilters().Where(t => deadTokens.Contains(t.Token)).ToListAsync(ct);
                db.DeviceTokens.RemoveRange(stale);
                await db.SaveChangesAsync(ct);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "FCM push failed for tenant {TenantId}, category {Category}", tenantId, category);
        }
    }
}
