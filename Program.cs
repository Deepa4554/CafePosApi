using System.Text;
using System.Threading.RateLimiting;
using CafePOS.Api.Data;
using CafePOS.Api.Domain;
using CafePOS.Api.Infrastructure;
using FirebaseAdmin;
using Google.Apis.Auth.OAuth2;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;

// Render's containers cap inotify watches low enough that ASP.NET Core's default
// appsettings.json file-watcher (for config hot-reload) can crash the process at startup
// with "configured user limit (128) on the number of inotify instances has been reached" —
// we don't rely on config hot-reload (a deploy always restarts the process anyway), so
// disable it before CreateBuilder ever sets the watcher up.
Environment.SetEnvironmentVariable("DOTNET_hostBuilder:reloadConfigOnChange", "false");

var builder = WebApplication.CreateBuilder(args);

// ---------- Controllers, Swagger ----------
builder.Services.AddControllers(o =>
    // Server-side half of per-staff Custom screen access — no-ops unless the endpoint
    // carries [RequireScreen]. See RequireScreenAttribute.cs.
    o.Filters.Add<ScreenAccessFilter>())
    // Accept/return enums as their string names ("Owner", "Percent", ...) instead
    // of raw ints — every request DTO with an enum field (Role, CouponType,
    // TaskPriority, ApprovalType, ...) relies on this.
    .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo { Title = "CafePOS API", Version = "v1" });
    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Description = "JWT Authorization header using the Bearer scheme. Example: \"Bearer {token}\"",
        Name = "Authorization",
        In = ParameterLocation.Header,
        Type = SecuritySchemeType.ApiKey,
        Scheme = "Bearer",
    });
    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme { Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" } },
            Array.Empty<string>()
        },
    });
});

// ---------- Multi-tenancy ----------
// ITenantContext reads the tenantId claim off the current request's validated
// JWT; CafePosDbContext takes a dependency on it to auto-filter/auto-stamp every
// tenant-scoped query. See docs/multi-tenant-saas-plan.md.
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ITenantContext, TenantContext>();
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<ITaxRateCache, TaxRateCache>();
builder.Services.AddSingleton<ISubscriptionCache, SubscriptionCache>();

// ---------- QR token encryption ----------
// Keys live in the database so already-printed QR codes keep working across restarts AND
// redeploys. Disk was the obvious place and it was wrong: `keys/` is gitignored, never
// lands in the Docker image, and Render throws the container filesystem away every
// release — so each deploy minted a fresh key and silently invalidated every QR code
// already printed and taped to a table. The database is the only thing here that outlives
// a deploy (and it's shared, so this keeps working if we ever run more than one instance).
//
// SetApplicationName pins the key-derivation isolation to a constant. Left unset it
// defaults to the content root path, which would make keys stop matching the moment the
// app runs from a different directory — the same class of "works locally, breaks in the
// container" bug this whole change is fixing.
builder.Services.AddDataProtection()
    .SetApplicationName("CafePOS")
    .PersistKeysToDbContext<CafePosDbContext>();
builder.Services.AddSingleton<QrTokenService>();
builder.Services.AddSingleton<ReceiptTokenService>();

// ---------- PDF generation (bill receipts) ----------
// QuestPDF's free Community license — this project qualifies (small business, non-SaaS-
// resale of the PDF feature itself). Must be set once at startup or every GeneratePdf()
// call throws.
QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;

// ---------- Database ----------
// Use Postgres (Supabase) if a connection string is configured, otherwise fall
// back to an in-memory database so the API runs today with zero setup. Once you
// have the Supabase connection string, drop it into appsettings.Development.json
// ("ConnectionStrings:CafePos") or the ConnectionStrings__CafePos env var — no
// code change needed. See README.md.
var connectionString = builder.Configuration.GetConnectionString("CafePos");
builder.Services.AddDbContext<CafePosDbContext>(options =>
{
    if (string.IsNullOrWhiteSpace(connectionString))
        options.UseInMemoryDatabase("CafePosDev");
    else
        options.UseNpgsql(WithPoolDefaults(connectionString), npgsql =>
        {
            // Without this, a single transient Postgres blip (a Supabase failover, a
            // momentary network drop, a pooler restart) surfaces as a hard 500 to every
            // tenant at once instead of a retry nobody notices. Retries are transient-only
            // — a real constraint violation or a concurrency conflict still fails fast.
            //
            // Note this makes EF refuse plainly-opened transactions: every explicit
            // transaction has to go through the execution strategy instead. That's what
            // DbConcurrency.InTransactionAsync is for — use it, don't call
            // BeginTransactionAsync directly.
            npgsql.EnableRetryOnFailure(maxRetryCount: 3, maxRetryDelay: TimeSpan.FromSeconds(5), errorCodesToAdd: null);
            // A query that's still running after 30s is never going to make a POS screen
            // useful — fail it and give the connection back to the pool rather than letting
            // it sit and starve everyone else.
            npgsql.CommandTimeout(30);
        });
});

// Npgsql's implicit default is a 100-connection pool per process, which is more than most
// managed Postgres instances allow in TOTAL — a moderate burst (order fire + KDS polling +
// dashboards) could open enough connections to lock every tenant out of the database rather
// than queue politely behind a sane cap. These are floor values only: anything the deployed
// connection string states explicitly always wins.
static string WithPoolDefaults(string raw)
{
    var b = new Npgsql.NpgsqlConnectionStringBuilder(raw);
    if (!b.ContainsKey("Maximum Pool Size")) b.MaxPoolSize = 50;
    if (!b.ContainsKey("Minimum Pool Size")) b.MinPoolSize = 2;
    // How long a request waits for a free connection before failing — the "graceful
    // queuing" half of the cap above.
    if (!b.ContainsKey("Timeout")) b.Timeout = 20;
    // Managed Postgres proxies drop idle connections silently; recycling ours first turns
    // what would be a broken-pipe 500 into a fresh connection.
    if (!b.ContainsKey("Connection Idle Lifetime")) b.ConnectionIdleLifetime = 60;
    if (!b.ContainsKey("Keepalive")) b.KeepAlive = 30;
    return b.ConnectionString;
}

// ---------- Auth: JWT + password hashing ----------
var jwtSecret = builder.Configuration["Jwt:Secret"];
if (string.IsNullOrWhiteSpace(jwtSecret))
{
    // Dev-only fallback so the API still boots with zero config. Outside Development
    // this refuses to start instead: the fallback string is public in this repo, so
    // running production on it would let anyone forge admin tokens. Set env var
    // Jwt__Secret to a real random 32+ byte value.
    if (!builder.Environment.IsDevelopment())
        throw new InvalidOperationException(
            "Jwt:Secret is not configured. Set the Jwt__Secret environment variable to a random 32+ byte value before running outside Development.");
    jwtSecret = "dev-only-insecure-secret-key-change-me-before-prod-32chars!";
}
builder.Services.Configure<JwtOptions>(options =>
{
    builder.Configuration.GetSection("Jwt").Bind(options);
    if (string.IsNullOrWhiteSpace(options.Secret)) options.Secret = jwtSecret;
});
builder.Services.AddSingleton<IJwtTokenService, JwtTokenService>();
builder.Services.AddSingleton<IPasswordHasher<AppUser>, PasswordHasher<AppUser>>();
builder.Services.AddScoped<IAuditService, AuditService>();

// ---------- Order building (staff POS + anonymous QR + guest-session cart, shared) ----------
builder.Services.AddScoped<IOrderBuildingService, OrderBuildingService>();

// ---------- Guest QR-ordering sessions (see docs/qr-ordering-session-plan) ----------
builder.Services.AddScoped<IGuestSessionService, GuestSessionService>();
builder.Services.AddHostedService<GuestSessionSweepService>();

// ---------- Email (cafe-signup OTP) ----------
// Sends via Brevo's HTTPS API if Email:BrevoApiKey/SenderEmail are configured; otherwise
// logs the code so local dev isn't blocked. See appsettings.Development.json.
builder.Services.Configure<EmailOptions>(builder.Configuration.GetSection("Email"));
builder.Services.AddHttpClient<IEmailService, BrevoEmailService>();

// ---------- AI (Gemini) ----------
// Explicit timeouts on both AI clients: the HttpClient default is 100 seconds, so a flaky
// or slow upstream model could hold a request slot (and its database connection) open for
// well over a minute each. A burst of those pins down capacity for every tenant, not just
// the one using AI — the reason AIController also carries its own rate-limit policy.
builder.Services.Configure<GeminiOptions>(builder.Configuration.GetSection("Gemini"));
builder.Services.AddHttpClient<IGeminiService, GeminiService>(client =>
    client.Timeout = TimeSpan.FromSeconds(30));

// ---------- AI menu-photo import (Claude -> Google Vision + Groq fallback chain) ----------
// Reads ANTHROPIC_API_KEY / GOOGLE_APPLICATION_CREDENTIALS / GROQ_API_KEY directly as flat
// env vars (no appsettings section) — see MenuPhotoAiService's doc comment.
// Longer than the chat client above because this one uploads a photo and walks a provider
// fallback chain, but still far short of the 100s default.
builder.Services.AddHttpClient<IMenuPhotoAiService, MenuPhotoAiService>(client =>
    client.Timeout = TimeSpan.FromSeconds(60));

// ---------- Image storage (Supabase Storage) ----------
builder.Services.Configure<SupabaseStorageOptions>(builder.Configuration.GetSection("Supabase"));
builder.Services.AddHttpClient<IImageStorageService, SupabaseImageStorageService>();

// ---------- Push notifications (Firebase Cloud Messaging) ----------
// Only initializes the Firebase Admin SDK if a real service-account key is configured; until
// then FcmPushNotificationSender sees no FirebaseApp.DefaultInstance and no-ops on every send —
// same "log and skip" fallback as SmtpEmailService when Email:GmailAddress/GmailAppPassword
// aren't set. See appsettings.json's Fcm:CredentialsPath comment for how to obtain the key.
builder.Services.Configure<FcmOptions>(builder.Configuration.GetSection("Fcm"));
var fcmCredentialsPath = builder.Configuration["Fcm:CredentialsPath"];
if (!string.IsNullOrWhiteSpace(fcmCredentialsPath) && File.Exists(fcmCredentialsPath) && FirebaseApp.DefaultInstance is null)
{
    // GoogleCredential.FromStream is flagged obsolete in favor of the newer CredentialFactory
    // API, which (as of Google.Apis.Auth 1.73) has no stable documented equivalent yet for
    // "load a service-account key from a local file" — this is still the Firebase Admin SDK's
    // own documented approach, so the warning is suppressed rather than chasing a moving target.
#pragma warning disable CS0618
    using var fcmCredentialsStream = File.OpenRead(fcmCredentialsPath);
    FirebaseApp.Create(new AppOptions { Credential = GoogleCredential.FromStream(fcmCredentialsStream) });
#pragma warning restore CS0618
}
builder.Services.AddSingleton<IPushNotificationSender, FcmPushNotificationSender>();

// ---------- WhatsApp order-tracking module (Baileys, standalone Node service) ----------
// Queue jobs directly into Postgres (WhatsAppMessageLog) — the standalone whatsapp-service
// polls the queue table every 2–5 seconds. See WhatsAppEventPublisher (writes Postgres),
// WhatsAppInternalController (queue polling endpoints), WhatsAppController (staff pairing UI
// proxy). Left unconfigured, the module is a no-op — zero impact on order/billing/print flows.
builder.Services.Configure<WhatsAppServiceOptions>(builder.Configuration.GetSection("WhatsAppService"));
builder.Services.AddScoped<IWhatsAppEventPublisher, WhatsAppEventPublisher>();
builder.Services.AddHttpClient<WhatsAppNodeClient>((sp, client) =>
{
    var opts = sp.GetRequiredService<IOptions<WhatsAppServiceOptions>>().Value;
    if (!string.IsNullOrWhiteSpace(opts.BaseUrl)) client.BaseAddress = new Uri(opts.BaseUrl);
    client.Timeout = TimeSpan.FromSeconds(10);
});

builder.Services
    .AddAuthentication(options =>
    {
        options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
    })
    .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, ServiceApiKeyAuthenticationHandler>(
        ServiceApiKeyDefaults.AuthenticationScheme, _ => { })
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = builder.Configuration["Jwt:Issuer"] ?? "CafePOS.Api",
            ValidAudience = builder.Configuration["Jwt:Audience"] ?? "CafePOS.App",
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecret)),
            ClockSkew = TimeSpan.FromSeconds(30),
        };
        // Browsers can't attach an Authorization header to a WebSocket handshake — SignalR's
        // JS client sends the token as an "access_token" query param instead, only for hub
        // requests (every other endpoint keeps using the real header).
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                var accessTokenQuery = context.Request.Query["access_token"];
                if (!string.IsNullOrEmpty(accessTokenQuery) && context.HttpContext.Request.Path.StartsWithSegments("/hubs"))
                {
                    context.Token = accessTokenQuery;
                    return Task.CompletedTask;
                }

                // Web build carries no Authorization header at all (see tokenStore.ts —
                // the access token is never put anywhere JS can read it); the httpOnly
                // cookie AuthCookies sets on login/refresh rides along automatically on
                // every same-site request, including this one, so fall back to it only
                // when there's no real header to prefer.
                if (string.IsNullOrEmpty(context.Request.Headers.Authorization) &&
                    context.Request.Cookies.TryGetValue(AuthCookies.AccessToken, out var cookieToken) &&
                    !string.IsNullOrEmpty(cookieToken))
                {
                    context.Token = cookieToken;
                }

                return Task.CompletedTask;
            },
        };
    });

// Policies mirror src/core/auth/permissions.ts in the RN app exactly.
builder.Services.AddAuthorizationBuilder()
    .SetFallbackPolicy(new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build())
    .AddPolicy(Policies.OwnerOnly, p => p.RequireAuthenticatedUser().RequireRole(nameof(AppRole.Owner)))
    .AddPolicy(Policies.OwnerOrManager, p => p.RequireAuthenticatedUser().RequireRole(nameof(AppRole.Owner), nameof(AppRole.Manager)))
    .AddPolicy(Policies.CanReadInventory, p => p.RequireAuthenticatedUser().RequireAssertion(ctx =>
        !ctx.User.IsInRole(nameof(AppRole.Waiter)) &&
        !ctx.User.IsInRole(nameof(AppRole.Chef)) &&
        !ctx.User.IsInRole(nameof(AppRole.KitchenStaff))))
    .AddPolicy(Policies.PlatformAdminOnly, p => p.RequireAuthenticatedUser().RequireAssertion(ctx =>
        ctx.User.FindFirst("isPlatformAdmin")?.Value == "true"))
    .AddPolicy(Policies.RequirePlus, p => p.RequireAuthenticatedUser().AddRequirements(new RequirePlanRequirement(PlanCategory.Plus)))
    .AddPolicy(Policies.RequirePremium, p => p.RequireAuthenticatedUser().AddRequirements(new RequirePlanRequirement(PlanCategory.Premium)))
    .AddPolicy(Policies.ServiceOnly, p => p.AddAuthenticationSchemes(ServiceApiKeyDefaults.AuthenticationScheme).RequireAuthenticatedUser());

builder.Services.AddScoped<IAuthorizationHandler, RequirePlanHandler>();

// ---------- Real-time (SignalR) ----------
// Replaces the 5-8s polling Orders/KDS/Tables used before (see useOrders.ts/useTables.ts) —
// CafePosDbContext pushes a tenant-scoped "ordersChanged" event on every relevant save
// (see CafePosDbContext.SaveChangesAsync), no controller has to remember to notify.
//
// In-process only — hub group membership lives on whichever instance holds the socket. Fine
// as long as this stays a single API instance; if it's ever scaled to two or more, add a
// Redis (or other) SignalR backplane so a save on instance A can still reach a device
// connected to instance B.
builder.Services.AddSignalR();
builder.Services.AddSingleton<IRealtimeNotifier, RealtimeNotifier>();

// ---------- CORS ----------
// The RN app runs on the Metro/webpack dev servers during development. Also allows
// any private-LAN origin on those same ports (192.168.x.x / 10.x.x.x / 172.16-31.x.x)
// so a phone on the same Wi-Fi can open http://<lan-ip>:3000 and actually log in —
// not just localhost, which only ever means "this machine" to whichever device is
// asking. AllowCredentials is required now that the web build authenticates via the
// httpOnly cookies AuthCookies sets (see its doc comment) — safe to combine with a
// dynamic origin check like this since neither policy ever falls back to a literal "*".
const string DevCorsPolicy = "DevCors";
const string ProdCorsPolicy = "ProdCors";
var lanOriginPattern = new System.Text.RegularExpressions.Regex(
    @"^http://(localhost|127\.0\.0\.1|192\.168\.\d{1,3}\.\d{1,3}|10\.\d{1,3}\.\d{1,3}\.\d{1,3}|172\.(1[6-9]|2\d|3[01])\.\d{1,3}\.\d{1,3}):\d+$");
// Real deployed frontend origin(s) — set via appsettings.Production.json /
// env var "AllowedOrigins__0" etc. once the frontend has a real domain. Empty
// by default so production doesn't silently fall back to the dev LAN policy.
var allowedProdOrigins = builder.Configuration.GetSection("AllowedOrigins").Get<string[]>() ?? [];
builder.Services.AddCors(options =>
{
    options.AddPolicy(DevCorsPolicy, policy =>
    {
        policy
            .SetIsOriginAllowed(origin => lanOriginPattern.IsMatch(origin))
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials();
    });
    options.AddPolicy(ProdCorsPolicy, policy =>
    {
        if (allowedProdOrigins.Length > 0)
        {
            policy.WithOrigins(allowedProdOrigins);
        }
        else
        {
            // No real frontend domain configured yet — fall back to the same
            // localhost/LAN pattern used for DevCorsPolicy so a local frontend
            // can still reach this deployed API while testing. Once AllowedOrigins
            // is set to a real domain, that takes over above.
            policy.SetIsOriginAllowed(origin => lanOriginPattern.IsMatch(origin));
        }
        policy.AllowAnyHeader().AllowAnyMethod().AllowCredentials();
    });
});

// ---------- Rate limiting ----------
// Generous global cap so a runaway client can't hammer the API, PLUS a much
// stricter per-IP limit on auth endpoints specifically (login, OTP request,
// cafe registration) — 200/min globally is nowhere near tight enough to stop
// password/OTP brute-forcing against a single account.
const string AuthLimiterPolicy = "AuthLimiter";
// Matches the literal string GuestSessionController's [EnableRateLimiting(...)] attribute
// uses — attribute arguments must be compile-time constants, so that reference can't
// share this const directly across files.
const string GuestSessionLimiterPolicy = "GuestSessionLimiter";
// AI endpoints call out to paid third-party providers (Gemini/Claude/Vision/Groq) that are
// slow and occasionally flaky — a burst of them can pin down a large share of this API's
// request capacity for every tenant, not just the tenant asking for AI. Kept well below the
// global cap on purpose: nothing about the AI features is latency-critical to service.
const string AiLimiterPolicy = "AiLimiter";
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    // Partitioned by signed-in identity where there is one, falling back to IP only for
    // anonymous traffic. Partitioning on IP alone punished the normal case: every tablet in
    // one cafe shares a single NAT/CGNAT address, so a busy floor's devices ate each other's
    // budget. The second, wider partition adds the aggregate cap IP-only had no way to
    // express — one tenant (however many addresses it comes from) can't crowd out the rest.
    options.GlobalLimiter = PartitionedRateLimiter.CreateChained(
        PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
            RateLimitPartition.GetFixedWindowLimiter(
                partitionKey: CallerPartitionKey(httpContext),
                factory: _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 200,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                })),
        PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
        {
            var tenantId = httpContext.User.FindFirst("tenantId")?.Value;
            // Anonymous/guest traffic has no tenant to aggregate over — the per-caller
            // partition above is already its only cap, so don't double-count it here.
            return tenantId is null
                ? RateLimitPartition.GetNoLimiter("anonymous")
                : RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: $"tenant:{tenantId}",
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 3000,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0,
                    });
        }));

    // One café's staff on a shared IP are distinct callers; an anonymous flood from one
    // address still collapses to a single partition exactly as before.
    static string CallerPartitionKey(HttpContext httpContext)
    {
        var userId = httpContext.User.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value
            ?? httpContext.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (userId is not null)
            return $"user:{httpContext.User.FindFirst("tenantId")?.Value}:{userId}";
        return $"ip:{httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown"}";
    }

    options.AddPolicy(AuthLimiterPolicy, httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 800,
                Window = TimeSpan.FromMinutes(5),
                QueueLimit = 0,
            }));

    // Guest QR-ordering endpoints (GuestSessionController) had no dedicated limit at all
    // before — just the generous 200/min global cap — despite being the one surface a
    // stranger can hit with nothing but a photo of a printed QR code (doc Section 9 #1/#5).
    options.AddPolicy(GuestSessionLimiterPolicy, httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 60,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            }));

    // See AiLimiterPolicy's declaration above. Partitioned per tenant, not per IP: the cost
    // being protected (upstream API spend + a request slot held open for seconds) is the
    // cafe's, however many of its devices asked.
    options.AddPolicy(AiLimiterPolicy, httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.User.FindFirst("tenantId")?.Value
                ?? httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 20,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            }));
});

// ---------- Health checks ----------
builder.Services.AddHealthChecks()
    .AddDbContextCheck<CafePosDbContext>("database");

// ---------- Global exception handling ----------
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddProblemDetails();

var app = builder.Build();

// Apply migrations (Postgres) or just ensure-created (InMemory), then seed the
// catalog data so the app has menu items / tables / inventory on first run.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<CafePosDbContext>();
    if (string.IsNullOrWhiteSpace(connectionString))
        db.Database.EnsureCreated();
    else
        db.Database.Migrate();

    SeedData.Apply(db);

    // Throwaway HR demo data for looking at the Attendance/Payroll/Payslip screens with
    // something in them. Off unless DemoData names a cafe (by slug or by owner e-mail),
    // and never outside Development — see DemoHrSeedData for what it writes and how to
    // remove it again.
    if (app.Environment.IsDevelopment())
    {
        DemoHrSeedData.Apply(
            db,
            scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("DemoData"),
            builder.Configuration["DemoData:SeedHrForTenantSlug"],
            builder.Configuration["DemoData:SeedHrForOwnerEmail"],
            builder.Configuration.GetValue("DemoData:PurgeHrDemo", false));
    }
}

app.UseExceptionHandler(_ => { }); // delegates entirely to GlobalExceptionHandler above

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(); // http://localhost:5xxx/swagger
}

app.UseCors(app.Environment.IsDevelopment() ? DevCorsPolicy : ProdCorsPolicy);

app.UseAuthentication();

// Deliberately AFTER UseAuthentication: the limiter partitions on the caller's tenant/user
// claims (see AddRateLimiter above), which don't exist until the JWT has been validated.
// Validating a token is cheap next to anything it gates, and the anonymous surfaces that
// actually attract abuse (login, OTP, guest QR) keep their own stricter per-IP policies.
app.UseRateLimiter();

app.UseMiddleware<SubscriptionExpiryMiddleware>();
app.UseAuthorization();

app.MapControllers();
app.MapHub<OrdersHub>("/hubs/orders");
app.MapHealthChecks("/health").AllowAnonymous();

app.Run();
