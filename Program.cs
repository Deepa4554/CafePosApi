using System.Text;
using System.Threading.RateLimiting;
using CafePOS.Api.Data;
using CafePOS.Api.Domain;
using CafePOS.Api.Infrastructure;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;

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

// ---------- Email (cafe-signup OTP) ----------
// Sends via Gmail SMTP if Email:GmailAddress/GmailAppPassword are configured; otherwise
// logs the code so local dev isn't blocked. See appsettings.Development.json.
builder.Services.Configure<EmailOptions>(builder.Configuration.GetSection("Email"));
builder.Services.AddSingleton<IEmailService, SmtpEmailService>();

// ---------- AI (Gemini) ----------
builder.Services.Configure<GeminiOptions>(builder.Configuration.GetSection("Gemini"));
builder.Services.AddHttpClient<IGeminiService, GeminiService>();

// ---------- Image storage (Supabase Storage) ----------
builder.Services.Configure<SupabaseStorageOptions>(builder.Configuration.GetSection("Supabase"));
builder.Services.AddHttpClient<IImageStorageService, SupabaseImageStorageService>();

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

// ---------- CORS ----------
// The RN app runs on the Metro/webpack dev servers during development. Also allows
// any private-LAN origin on those same ports (192.168.x.x / 10.x.x.x / 172.16-31.x.x)
// so a phone on the same Wi-Fi can open http://<lan-ip>:3000 and actually log in —
// not just localhost, which only ever means "this machine" to whichever device is
// asking.
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
        policy
            .WithOrigins(allowedProdOrigins)
            .AllowAnyHeader()
            .AllowAnyMethod();
    });
});

// ---------- Rate limiting ----------
// Generous global cap so a runaway client can't hammer the API, PLUS a much
// stricter per-IP limit on auth endpoints specifically (login, OTP request,
// cafe registration) — 200/min globally is nowhere near tight enough to stop
// password/OTP brute-forcing against a single account.
const string AuthLimiterPolicy = "AuthLimiter";
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
                PermitLimit = 8,
                Window = TimeSpan.FromMinutes(5),
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
app.MapHealthChecks("/health").AllowAnonymous();

app.Run();
