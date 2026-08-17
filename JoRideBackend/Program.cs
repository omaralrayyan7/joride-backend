using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.OpenApi.Models;
using System.Text;
using System.Threading.RateLimiting;
using JoRideBackend.Data;
using JoRideBackend.Models;
using JoRideBackend.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.IdentityModel.Tokens;
using Polly;
using Polly.Extensions.Http;


var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllersWithViews();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo { Title = "JoRide Backend API", Version = "v1" });
    options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header,
        Description = "Enter a valid JWT bearer token."
    });
    options.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference
                {
                    Type = ReferenceType.SecurityScheme,
                    Id = "Bearer"
                }
            },
            Array.Empty<string>()
        }
    });
});

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        if (builder.Environment.IsDevelopment())
        {
            // Allow any localhost port in development (Flutter picks a random port each run)
            policy.SetIsOriginAllowed(origin =>
                      Uri.TryCreate(origin, UriKind.Absolute, out var uri) &&
                      (uri.Host == "localhost" || uri.Host == "127.0.0.1"))
                  .AllowAnyHeader()
                  .AllowAnyMethod();
        }
        else
        {
            var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
            policy.WithOrigins(allowedOrigins)
                  .AllowAnyHeader()
                  .AllowAnyMethod();
        }
    });
});

builder.Services.AddMemoryCache();
builder.Services.AddHttpClient("traccar"); // used by TraccarService for OsmAnd position pushes

// REST API client for reading devices/positions/server status back from Traccar.
// TRACCAR_BASE_URL / TRACCAR_TOKEN are optional at startup (health check and
// polling simply report unavailable if unset) but required to actually call it.
var traccarBaseUrl = builder.Configuration["TRACCAR_BASE_URL"];
var traccarToken = builder.Configuration["TRACCAR_TOKEN"];
builder.Services.AddHttpClient("traccar-rest", client =>
    {
        if (!string.IsNullOrWhiteSpace(traccarBaseUrl))
        {
            client.BaseAddress = new Uri(traccarBaseUrl.TrimEnd('/') + "/");
        }
        if (!string.IsNullOrWhiteSpace(traccarToken))
        {
            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", traccarToken);
        }
    })
    .AddPolicyHandler(HttpPolicyExtensions
        .HandleTransientHttpError()
        .WaitAndRetryAsync(3, retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt))));

builder.Services.AddSingleton<JoRideBackend.Services.TraccarService>();
builder.Services.AddHostedService<TraccarPollingService>();
builder.Services.AddHostedService<JoRideBackend.Services.OverdueTripMonitorService>();
builder.Services.AddSingleton<IOtpService, OtpService>();
builder.Services.AddScoped<ISmsService, SmsService>();
builder.Services.AddScoped<IEmailService, EmailService>();
builder.Services.AddScoped<ILicenseVerification, LicenseVerification>();

builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection("Jwt"));
builder.Services.AddSingleton<JwtTokenService>();
builder.Services.AddSingleton<IPasswordHasher<User>, PasswordHasher<User>>();
builder.Services.AddSingleton<FirestoreService>();
builder.Services.AddHostedService<FirestoreDataLoader>();

// Relational store for money and device commands only (Firestore remains the store
// of record for users/vehicles/trips). Connection string comes from the
// POSTGRES_CONNECTION_STRING env var; a local default is allowed only in Development
// so `dotnet run` works against docker-compose.dev.yml without extra setup.
var postgresConnectionString = builder.Configuration["POSTGRES_CONNECTION_STRING"];
if (string.IsNullOrWhiteSpace(postgresConnectionString))
{
    if (!builder.Environment.IsDevelopment())
    {
        throw new InvalidOperationException(
            "POSTGRES_CONNECTION_STRING must be set in non-Development environments.");
    }

    postgresConnectionString = "Host=localhost;Port=5433;Database=joride;Username=joride;Password=joride_dev_password";
}

builder.Services.AddDbContext<PaymentsDbContext>(options =>
    options.UseNpgsql(postgresConnectionString));

builder.Services.AddScoped<JoRideBackend.Services.DeviceCommandService>();

// E5.1 groundwork: real gateway only. FakePaymentGateway lives exclusively in the
// JoRideBackend.Tests project (which this project has no reference to at all — see that
// project's TestPaymentGatewayRegistration for the explicit "Testing" environment guard);
// there is no code path here that could ever resolve it in Development or Production.
builder.Services.AddHttpClient("hyperpay");
builder.Services.AddScoped<JoRideBackend.Services.Payments.IPaymentGateway, JoRideBackend.Services.Payments.HyperPayGateway>();
builder.Services.AddScoped<JoRideBackend.Services.Payments.LedgerService>();
builder.Services.AddScoped<JoRideBackend.Services.Payments.HyperPayWebhookService>();
builder.Services.AddScoped<JoRideBackend.Services.Payments.PaymentAdminService>();

// E2.1: refresh token rotation. E2.2: KYC document storage/signing.
builder.Services.AddScoped<JoRideBackend.Services.Auth.RefreshTokenService>();
builder.Services.AddSingleton<JoRideBackend.Services.Auth.KycDocumentStorage>();
builder.Services.AddSingleton<JoRideBackend.Services.Auth.KycSigningService>();

builder.Services.AddHealthChecks()
    .AddCheck<PostgresHealthCheck>("postgres")
    .AddCheck<TraccarHealthCheck>("traccar");

var jwt = builder.Configuration.GetSection("Jwt").Get<JwtOptions>() ?? new JwtOptions();
if (string.IsNullOrWhiteSpace(jwt.Key) || Encoding.UTF8.GetByteCount(jwt.Key) < 32)
{
    throw new InvalidOperationException("Jwt:Key must be configured and at least 32 bytes long.");
}

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        // Keep claim names as issued (e.g. "role") without mapping to URI form.
        // Without this, the "role" claim becomes the full URI claim type and
        // RequireClaim("role","admin") silently fails with 403.
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwt.Issuer,
            ValidAudience = jwt.Audience,
            ClockSkew = TimeSpan.FromMinutes(2),
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.Key))
        };
    });

builder.Services.AddSingleton<Microsoft.AspNetCore.Authorization.IAuthorizationHandler, JoRideBackend.Services.Auth.KycApprovedHandler>();
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("AdminOnly", policy => policy.RequireClaim("role", "admin"));
    options.AddPolicy("KycApproved", policy => policy.Requirements.Add(new JoRideBackend.Services.Auth.KycApprovedRequirement()));
});

// Rate limiting for /api/auth/* — partitioned per client IP so one abusive caller can't
// exhaust another's quota. Login/refresh get room for a genuine mistyped password; OTP is
// stricter since each request costs a real SMS/email send.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    static string PartitionKey(HttpContext http) =>
        http.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    options.AddPolicy("auth-login", http => RateLimitPartition.GetFixedWindowLimiter(
        PartitionKey(http),
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 5,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
        }));

    options.AddPolicy("auth-otp", http => RateLimitPartition.GetFixedWindowLimiter(
        PartitionKey(http),
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 3,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
        }));

    // E8.1: rate limiting audit found auth/OTP were the only gated endpoints — payment and
    // device-actuation endpoints had none. Both are already behind [Authorize] (or, for the
    // webhook, HMAC/AES-GCM payload verification), but auth alone doesn't stop a valid,
    // rate-unlimited caller (or a compromised token) from hammering a gateway call or a
    // vehicle-actuation command.
    options.AddPolicy("payment-checkout", http => RateLimitPartition.GetFixedWindowLimiter(
        PartitionKey(http),
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 10,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
        }));

    // Webhooks are server-to-server (HyperPay calling us, not an end user), so this is
    // deliberately looser than the user-facing policies above — legitimate retry/burst
    // traffic from the provider shouldn't be throttled — but still caps the cost of
    // repeated failed-auth decrypt attempts against this endpoint from any one source.
    options.AddPolicy("payment-webhook", http => RateLimitPartition.GetFixedWindowLimiter(
        PartitionKey(http),
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 60,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
        }));

    options.AddPolicy("device-command", http => RateLimitPartition.GetFixedWindowLimiter(
        PartitionKey(http),
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 20,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
        }));
});

var app = builder.Build();

VehiclesController.Seed();
PricingController.Seed();
VehicleLocationController.Seed();

// Wire TraccarService into the static TripsController field
TripsController._traccar = app.Services.GetRequiredService<JoRideBackend.Services.TraccarService>();
WalletController.SetServiceScopeFactory(app.Services.GetRequiredService<IServiceScopeFactory>());

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}
else
{
    app.UseHsts();
    app.UseHttpsRedirection();
}

// API callers (the mobile app) get ProblemDetails JSON on an unhandled exception, in every
// environment — an HTML MVC error view is unparseable by the mobile client. This must be
// registered before the MVC-view exception handler below so /api/* requests are branched
// off before that handler's Home/Error redirect ever applies.
app.UseWhen(context => context.Request.Path.StartsWithSegments("/api"), apiApp =>
{
    apiApp.UseExceptionHandler(errorApp =>
    {
        errorApp.Run(async context =>
        {
            context.Response.ContentType = "application/problem+json";
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;

            var feature = context.Features.Get<IExceptionHandlerFeature>();
            var problem = new ProblemDetails
            {
                Status = StatusCodes.Status500InternalServerError,
                Title = "An unexpected error occurred.",
                Type = "https://tools.ietf.org/html/rfc7231#section-6.6.1",
                Instance = context.Request.Path
            };
            if (app.Environment.IsDevelopment() && feature?.Error is not null)
            {
                problem.Detail = feature.Error.ToString();
            }

            await context.Response.WriteAsJsonAsync(problem);
        });
    });
});

if (!app.Environment.IsDevelopment())
{
    // Non-API (MVC/Razor admin dashboard) requests still get the HTML error view.
    app.UseExceptionHandler("/Home/Error");
}

// E8.1: standard security response headers, applied to every response (API JSON and the
// Razor admin dashboard alike). HSTS is handled separately above via the built-in
// app.UseHsts() (prod-only, since it doesn't make sense over plain HTTP in dev).
app.Use(async (context, next) =>
{
    var headers = context.Response.Headers;
    headers["X-Content-Type-Options"] = "nosniff";
    headers["X-Frame-Options"] = "DENY";
    headers["Referrer-Policy"] = "strict-origin-when-cross-origin";

    // Verified against Swashbuckle's actual SwaggerUI output (GET /swagger/index.html): it
    // loads swagger-ui-bundle.js/swagger-ui-standalone-preset.js/index.js as external,
    // same-origin <script src> files — no inline <script>/<style> anywhere — so 'self'
    // covers it too, same as the admin dashboard and default MVC views. Applied in every
    // environment; no need to loosen this for dev-only tooling.
    headers["Content-Security-Policy"] =
        "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; " +
        "object-src 'none'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'";

    await next();
});

app.UseStaticFiles();
app.UseRouting();
app.UseCors();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

app.MapHealthChecks("/health");

app.Run();

// Testability marker only (standard ASP.NET Core pattern for top-level-statement Program.cs):
// lets a WebApplicationFactory<Program> in the test project boot this host. Adds no behavior.
public partial class Program { }
