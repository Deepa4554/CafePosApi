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
builder.Services.AddControllers()
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

// ---------- QR token encryption ----------
// Keys persisted to disk so already-printed QR codes keep working across restarts
// (the default is in-memory-only keys, which would invalidate every QR code on every
// deploy/restart — unacceptable for something printed and taped to a table).
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(builder.Environment.ContentRootPath, "keys")));
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
        options.UseNpgsql(connectionString);
});

// ---------- Auth: JWT + password hashing ----------
var jwtSecret = builder.Configuration["Jwt:Secret"];
if (string.IsNullOrWhiteSpace(jwtSecret))
{
    // Dev-only fallback so the API still boots with zero config. Production
    // MUST set Jwt:Secret (env var Jwt__Secret) to a real random 32+ byte value.
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
builder.Services.Configure<GeminiOptions>(builder.Configuration.GetSection("Gemini"));
builder.Services.AddHttpClient<IGeminiService, GeminiService>();

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

builder.Services
    .AddAuthentication(options =>
    {
        options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
    })
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
                var accessToken = context.Request.Query["access_token"];
                if (!string.IsNullOrEmpty(accessToken) && context.HttpContext.Request.Path.StartsWithSegments("/hubs"))
                    context.Token = accessToken;
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
    .AddPolicy(Policies.RequirePremium, p => p.RequireAuthenticatedUser().AddRequirements(new RequirePlanRequirement(PlanCategory.Premium)));

builder.Services.AddScoped<IAuthorizationHandler, RequirePlanHandler>();

// ---------- Real-time (SignalR) ----------
// Replaces the 5-8s polling Orders/KDS/Tables used before (see useOrders.ts/useTables.ts) —
// CafePosDbContext pushes a tenant-scoped "ordersChanged" event on every relevant save
// (see CafePosDbContext.SaveChangesAsync), no controller has to remember to notify.
builder.Services.AddSignalR();
builder.Services.AddSingleton<IRealtimeNotifier, RealtimeNotifier>();

// ---------- CORS ----------
// The RN app runs on the Metro/webpack dev servers during development. Also allows
// any private-LAN origin on those same ports (192.168.x.x / 10.x.x.x / 172.16-31.x.x)
// so a phone on the same Wi-Fi can open http://<lan-ip>:3000 and actually log in —
// not just localhost, which only ever means "this machine" to whichever device is
// asking. No cookies/credentials are involved (access + refresh tokens both travel
// in the request/response body), so origins can just be allowed outright instead of
// juggling AllowCredentials' wildcard restrictions.
const string DevCorsPolicy = "DevCors";
const string ProdCorsPolicy = "ProdCors";
var lanOriginPattern = new System.Text.RegularExpressions.Regex(
    @"^http://(localhost|192\.168\.\d{1,3}\.\d{1,3}|10\.\d{1,3}\.\d{1,3}\.\d{1,3}|172\.(1[6-9]|2\d|3[01])\.\d{1,3}\.\d{1,3}):(3000|8081)$");
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
            .AllowAnyMethod();
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
        policy.AllowAnyHeader().AllowAnyMethod();
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
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 200,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            }));

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

app.UseRateLimiter();

app.UseAuthentication();
app.UseMiddleware<SubscriptionExpiryMiddleware>();
app.UseAuthorization();

app.MapControllers();
app.MapHub<OrdersHub>("/hubs/orders");
app.MapHealthChecks("/health").AllowAnonymous();

app.Run();
