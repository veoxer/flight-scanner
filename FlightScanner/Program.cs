using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using FlightScanner.Components;
using FlightScanner.Components.Account;
using FlightScanner.Features.Alerts;
using FlightScanner.Features.Flights;
using FlightScanner.Features.Integrations;
using FlightScanner.Features.Setup;
using FlightScanner.Data;
using Npgsql;
using System.Security.Claims;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddProblemDetails();
builder.Services.AddHealthChecks();
builder.Services.AddHttpClient("flight-provider", client => client.Timeout = TimeSpan.FromSeconds(30));
builder.Services.AddHttpClient("whatsapp", client => client.Timeout = TimeSpan.FromSeconds(20));

builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<IdentityRedirectManager>();
builder.Services.AddScoped<AuthenticationStateProvider, IdentityRevalidatingAuthenticationStateProvider>();
builder.Services.AddScoped<SetupState>();
builder.Services.AddScoped<IFlightSearchService, FlightSearchService>();
builder.Services.AddScoped<INotificationDispatcher, NotificationDispatcher>();
builder.Services.AddSingleton<StartupInitializer>();
builder.Services.AddHostedService<AlertScannerService>();

builder.Services.AddAuthentication(options =>
    {
        options.DefaultScheme = IdentityConstants.ApplicationScheme;
        options.DefaultSignInScheme = IdentityConstants.ExternalScheme;
    })
    .AddIdentityCookies();
builder.Services.AddAuthorization();
builder.Services.AddRateLimiter(options =>
{
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.User.Identity?.Name ?? context.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 120,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            }));
});
builder.Services.ConfigureApplicationCookie(options =>
{
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    options.SlidingExpiration = true;
});

var connectionString = DatabaseConfiguration.GetPostgresConnectionString(builder.Configuration);
builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseNpgsql(connectionString));
builder.Services.AddDbContextFactory<ApplicationDbContext>(options =>
    options.UseNpgsql(connectionString), ServiceLifetime.Scoped);
builder.Services.AddDatabaseDeveloperPageExceptionFilter();

builder.Services.AddIdentityCore<ApplicationUser>(options =>
    {
        options.SignIn.RequireConfirmedAccount = false;
        options.Password.RequiredLength = 10;
        options.Password.RequireNonAlphanumeric = true;
        options.User.RequireUniqueEmail = true;
        options.Stores.SchemaVersion = IdentitySchemaVersions.Version3;
    })
    .AddRoles<IdentityRole>()
    .AddEntityFrameworkStores<ApplicationDbContext>()
    .AddSignInManager()
    .AddDefaultTokenProviders();

builder.Services.AddSingleton<IEmailSender<ApplicationUser>, IdentityNoOpEmailSender>();

var dataProtectionKeysPath = builder.Configuration["FLIGHTSCANNER_DATA_PROTECTION_KEYS_PATH"];
if (!string.IsNullOrWhiteSpace(dataProtectionKeysPath))
{
    builder.Services.AddDataProtection()
        .SetApplicationName("FlightScanner")
        .PersistKeysToFileSystem(new DirectoryInfo(dataProtectionKeysPath));
}

var app = builder.Build();
await app.Services.GetRequiredService<StartupInitializer>().InitializeAsync();

// Configure the HTTP request pipeline.
app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
});

if (app.Environment.IsDevelopment())
{
    app.UseMigrationsEndPoint();
}
else
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();
app.Use(async (context, next) =>
{
    context.Response.Headers.TryAdd("X-Content-Type-Options", "nosniff");
    context.Response.Headers.TryAdd("Referrer-Policy", "strict-origin-when-cross-origin");
    context.Response.Headers.TryAdd("Permissions-Policy", "geolocation=(), camera=(), microphone=()");
    await next();
});

app.UseAntiforgery();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();
app.MapHealthChecks("/health");

app.MapGet("/api/push/public-key", async (IDbContextFactory<ApplicationDbContext> dbFactory) =>
{
    await using var db = await dbFactory.CreateDbContextAsync();
    var setting = await db.IntegrationSettings.AsNoTracking().FirstAsync(item => item.Kind == IntegrationKind.WebPush);
    var options = System.Text.Json.JsonSerializer.Deserialize<WebPushOptions>(setting.SettingsJson, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)) ?? new();
    return Results.Ok(new { publicKey = options.PublicKey });
}).RequireAuthorization();

app.MapPost("/api/push/subscribe", async (
    PushSubscriptionInput input,
    ClaimsPrincipal user,
    IDbContextFactory<ApplicationDbContext> dbFactory) =>
{
    var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
    if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(input.Endpoint))
    {
        return Results.BadRequest();
    }

    await using var db = await dbFactory.CreateDbContextAsync();
    var subscription = await db.PushSubscriptions.FirstOrDefaultAsync(item => item.Endpoint == input.Endpoint);
    if (subscription is null)
    {
        db.PushSubscriptions.Add(new PushSubscriptionRecord
        {
            UserId = userId,
            Endpoint = input.Endpoint,
            P256dh = input.Keys.P256dh,
            Auth = input.Keys.Auth
        });
    }
    else
    {
        subscription.UserId = userId;
        subscription.P256dh = input.Keys.P256dh;
        subscription.Auth = input.Keys.Auth;
        subscription.LastSeenAt = DateTimeOffset.UtcNow;
    }

    await db.SaveChangesAsync();
    return Results.NoContent();
}).RequireAuthorization();

// Add additional endpoints required by the Identity /Account Razor components.
app.MapAdditionalIdentityEndpoints();

app.Run();

public sealed record PushSubscriptionInput(string Endpoint, PushSubscriptionKeys Keys);
public sealed record PushSubscriptionKeys(string P256dh, string Auth);

internal static class DatabaseConfiguration
{
    public static string GetPostgresConnectionString(IConfiguration configuration)
    {
        var explicitConnectionString = configuration.GetConnectionString("DefaultConnection");
        if (!string.IsNullOrWhiteSpace(explicitConnectionString))
        {
            return explicitConnectionString;
        }

        var databaseUrl = configuration["FLIGHTSCANNER_DATABASE_URL"]
            ?? configuration["DATABASE_URL"]
            ?? configuration["POSTGRES_URL"];

        if (!string.IsNullOrWhiteSpace(databaseUrl))
        {
            return ConvertDatabaseUrl(databaseUrl);
        }

        var host = Require(configuration, "POSTGRES_HOST");
        var database = Require(configuration, "POSTGRES_DB");
        var username = Require(configuration, "POSTGRES_USER");
        var password = Require(configuration, "POSTGRES_PASSWORD");

        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = host,
            Database = database,
            Username = username,
            Password = password,
            Port = int.TryParse(configuration["POSTGRES_PORT"], out var port) ? port : 5432,
            SslMode = Enum.TryParse<SslMode>(configuration["POSTGRES_SSL_MODE"], ignoreCase: true, out var sslMode)
                ? sslMode
                : SslMode.Disable,
            Pooling = true
        };

        return builder.ConnectionString;
    }

    private static string ConvertDatabaseUrl(string databaseUrl)
    {
        var uri = new Uri(databaseUrl);
        var userInfo = uri.UserInfo.Split(':', 2);

        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = uri.Host,
            Port = uri.Port > 0 ? uri.Port : 5432,
            Database = uri.AbsolutePath.TrimStart('/'),
            Username = Uri.UnescapeDataString(userInfo.ElementAtOrDefault(0) ?? ""),
            Password = Uri.UnescapeDataString(userInfo.ElementAtOrDefault(1) ?? ""),
            Pooling = true
        };

        var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(uri.Query);
        if (query.TryGetValue("sslmode", out var sslMode) &&
            Enum.TryParse<SslMode>(sslMode.ToString(), ignoreCase: true, out var parsedSslMode))
        {
            builder.SslMode = parsedSslMode;
        }

        return builder.ConnectionString;
    }

    private static string Require(IConfiguration configuration, string key)
    {
        var value = configuration[key];
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                $"Missing required PostgreSQL configuration. Set {key}, or set FLIGHTSCANNER_DATABASE_URL.");
        }

        return value;
    }
}
