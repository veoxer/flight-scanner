using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Antiforgery;
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
using FlightScanner.Features.Localization;
using FlightScanner.Features.Setup;
using FlightScanner.Data;
using Microsoft.AspNetCore.Localization;
using Microsoft.Extensions.Caching.Memory;
using Npgsql;
using System.Globalization;
using System.Security.Claims;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    //.AddInteractiveWebAssemblyComponents()
    .AddInteractiveServerComponents();
builder.Services.AddLocalization();
builder.Services.AddProblemDetails();
builder.Services.AddHealthChecks();
builder.Services.AddMemoryCache();
builder.Services.AddHttpClient("flight-provider", client => client.Timeout = TimeSpan.FromSeconds(30));
builder.Services.AddHttpClient("serpapi", client => client.Timeout = TimeSpan.FromSeconds(45));
builder.Services.AddHttpClient("whatsapp", client => client.Timeout = TimeSpan.FromSeconds(20));
builder.Services.AddHttpClient("location-data", client => client.Timeout = TimeSpan.FromSeconds(90));
builder.Services.AddHttpClient("wikidata-on-demand", client => client.Timeout = TimeSpan.FromSeconds(20));

builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<IdentityRedirectManager>();
builder.Services.AddScoped<AuthenticationStateProvider, IdentityRevalidatingAuthenticationStateProvider>();
builder.Services.AddScoped<IdentityErrorDescriber, LocalizedIdentityErrorDescriber>();
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
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.User.FindFirstValue(ClaimTypes.NameIdentifier) ??
            context.User.Identity?.Name ??
            context.Connection.RemoteIpAddress?.ToString() ??
            "anonymous",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = builder.Environment.IsDevelopment() ? 600 : 240,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 20,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst
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

builder.Services.AddScoped<IEmailSender<ApplicationUser>, IdentitySmtpEmailSender>();
builder.Services.AddScoped<IUserClaimsPrincipalFactory<ApplicationUser>, ApplicationUserClaimsPrincipalFactory>();

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
var supportedCultures = new[] { "en", "fr", "ar" }.Select(culture => new CultureInfo(culture)).ToArray();
app.UseRequestLocalization(new RequestLocalizationOptions
{
    DefaultRequestCulture = new RequestCulture("en"),
    SupportedCultures = supportedCultures,
    SupportedUICultures = supportedCultures
});

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
if (!app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
}
app.Use(async (context, next) =>
{
    context.Response.Headers.TryAdd("X-Content-Type-Options", "nosniff");
    context.Response.Headers.TryAdd("Referrer-Policy", "strict-origin-when-cross-origin");
    context.Response.Headers.TryAdd("Permissions-Policy", "geolocation=(), camera=(), microphone=()");
    await next();
});

app.UseAntiforgery();
app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    //.AddInteractiveWebAssemblyRenderMode()
    .AddInteractiveServerRenderMode();
app.MapHealthChecks("/health");

app.MapGet("/culture/set", async (
    string culture,
    string? returnUrl,
    HttpContext context,
    ClaimsPrincipal user,
    IDbContextFactory<ApplicationDbContext> dbFactory) =>
{
    var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "en", "fr", "ar" };
    if (!allowed.Contains(culture))
    {
        culture = "en";
    }

    var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
    if (!string.IsNullOrWhiteSpace(userId))
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var appUser = await db.Users.FirstOrDefaultAsync(item => item.Id == userId);
        if (appUser is not null)
        {
            appUser.PreferredCulture = culture;
            await db.SaveChangesAsync();
        }
    }

    context.Response.Cookies.Append(
        CookieRequestCultureProvider.DefaultCookieName,
        CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(culture)),
        new CookieOptions
        {
            HttpOnly = true,
            IsEssential = true,
            SameSite = SameSiteMode.Lax,
            Secure = context.Request.IsHttps,
            Expires = DateTimeOffset.UtcNow.AddYears(1)
        });

    var target = string.IsNullOrWhiteSpace(returnUrl) || !returnUrl.StartsWith('/')
        ? "/"
        : returnUrl;
    return Results.LocalRedirect(target);
}).AllowAnonymous();

app.MapGet("/api/locations/suggest", async (
    string q,
    IDbContextFactory<ApplicationDbContext> dbFactory,
    IMemoryCache cache,
    CancellationToken cancellationToken) =>
{
    q = q.Trim();
    if (q.Length < 2)
    {
        return Results.Ok(Array.Empty<object>());
    }

    var lowered = q.ToLowerInvariant();
    var culture = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
    var cacheKey = $"locations:suggest:{culture}:{lowered}";
    if (cache.TryGetValue<object[]>(cacheKey, out var cachedSuggestions))
    {
        return Results.Ok(cachedSuggestions);
    }

    await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
    var prefixPattern = $"{EscapeLikePattern(lowered)}%";
    var locations = await QueryLocalizedLocationSuggestionsAsync(db, lowered, prefixPattern, culture, cancellationToken);
    if (culture != "en" && locations.Count < 120)
    {
        var englishLocations = await QueryLocalizedLocationSuggestionsAsync(db, lowered, prefixPattern, "en", cancellationToken);
        locations = locations
            .Concat(englishLocations)
            .GroupBy(location => new { location.Type, location.Code })
            .Select(group => group.First())
            .ToList();
    }

    locations = locations
        .GroupBy(location => new { location.Type, location.Code })
        .Select(group => group.First())
        .ToList();

    var suggestions = locations
        .OrderByDescending(location => location.Code.Equals(q, StringComparison.OrdinalIgnoreCase))
        .ThenByDescending(location => location.Type == LocationType.Country &&
            LocalizedLocationName(location, culture).Equals(q, StringComparison.OrdinalIgnoreCase))
        .ThenByDescending(location => location.Type == LocationType.Country &&
            LocalizedLocationName(location, culture).StartsWith(q, StringComparison.OrdinalIgnoreCase))
        .ThenByDescending(location => location.Type == LocationType.Continent &&
            LocalizedLocationName(location, culture).Equals(q, StringComparison.OrdinalIgnoreCase))
        .ThenByDescending(location => location.Type == LocationType.Continent &&
            LocalizedLocationName(location, culture).StartsWith(q, StringComparison.OrdinalIgnoreCase))
        .ThenByDescending(location => LocalizedLocationName(location, culture).StartsWith(q, StringComparison.OrdinalIgnoreCase))
        .ThenBy(location => location.Type == LocationType.Country ? 0 :
            location.Type == LocationType.City ? 1 :
            location.Type == LocationType.Airport ? 2 : 3)
        .ThenBy(location => LocalizedLocationName(location, culture))
        .Take(12)
        .Select(location => BuildLocationSuggestion(location, culture))
        .ToArray();

    cache.Set(cacheKey, suggestions, TimeSpan.FromMinutes(30));

    return Results.Ok(suggestions);
}).RequireAuthorization();

app.MapGet("/api/locations/resolve", async (
    string q,
    IDbContextFactory<ApplicationDbContext> dbFactory,
    CancellationToken cancellationToken) =>
{
    q = q.Trim();
    if (q.Length < 2)
    {
        return Results.Ok<object?>(null);
    }

    await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
    var lowered = q.ToLowerInvariant();
    var culture = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
    var matches = await db.FlightLocations
        .FromSqlInterpolated($"""
            SELECT *
            FROM "FlightLocations"
            WHERE lower("Code") = {lowered}
                OR lower("Name") = {lowered}
                OR lower(coalesce("NameFr", '')) = {lowered}
                OR lower(coalesce("NameAr", '')) = {lowered}
            ORDER BY
                CASE
                    WHEN lower("Code") = {lowered} THEN 0
                    WHEN lower("Name") = {lowered}
                        OR lower(coalesce("NameFr", '')) = {lowered}
                        OR lower(coalesce("NameAr", '')) = {lowered} THEN 1
                    ELSE 2
                END,
                CASE "Type" WHEN 2 THEN 0 WHEN 1 THEN 1 WHEN 0 THEN 2 WHEN 3 THEN 3 ELSE 4 END,
                "Name"
            LIMIT 40
            """)
        .AsNoTracking()
        .ToListAsync(cancellationToken);

    var match = matches
        .OrderByDescending(location => location.Code.Equals(q, StringComparison.OrdinalIgnoreCase))
        .ThenByDescending(location => LocalizedLocationName(location, culture).Equals(q, StringComparison.OrdinalIgnoreCase))
        .ThenBy(location => location.Type == LocationType.Country ? 0 :
            location.Type == LocationType.City ? 1 :
            location.Type == LocationType.Airport ? 2 : 3)
        .ThenBy(location => LocalizedLocationName(location, culture))
        .FirstOrDefault();

    return Results.Ok(match is null ? null : BuildLocationSuggestion(match, culture));
}).RequireAuthorization();

app.MapGet("/api/push/public-key", async (IDbContextFactory<ApplicationDbContext> dbFactory) =>
{
    await using var db = await dbFactory.CreateDbContextAsync();
    var setting = await db.IntegrationSettings.AsNoTracking().FirstAsync(item => item.Kind == IntegrationKind.WebPush);
    var options = System.Text.Json.JsonSerializer.Deserialize<WebPushOptions>(setting.SettingsJson, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)) ?? new();
    return Results.Ok(new { publicKey = options.PublicKey });
}).RequireAuthorization();

app.MapGet("/api/flights/return-details", async (
    string departureToken,
    string? currency,
    string? departureId,
    string? arrivalId,
    DateOnly? outboundDate,
    DateOnly? returnDate,
    IFlightSearchService flightSearch,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(departureToken) || departureToken.Length > 4096)
    {
        return Results.BadRequest();
    }

    var offer = await flightSearch.GetReturnFlightAsync(departureToken, currency ?? "MAD", departureId, arrivalId, outboundDate, returnDate, cancellationToken);
    return offer is null ? Results.NotFound() : Results.Ok(offer);
}).RequireAuthorization();

app.MapGet("/api/flights/return-options", async (
    string departureToken,
    string? currency,
    string? departureId,
    string? arrivalId,
    DateOnly? outboundDate,
    DateOnly? returnDate,
    IFlightSearchService flightSearch,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(departureToken) || departureToken.Length > 4096)
    {
        return Results.BadRequest();
    }

    var offers = await flightSearch.GetReturnFlightsAsync(departureToken, currency ?? "MAD", departureId, arrivalId, outboundDate, returnDate, cancellationToken);
    return Results.Ok(offers);
}).RequireAuthorization();

app.MapPost("/admin/integrations/save", async (
    HttpContext context,
    IDbContextFactory<ApplicationDbContext> dbFactory) =>
{
    var form = await context.Request.ReadFormAsync();
    var enabled = form.TryGetValue("enabled", out var enabledValues) &&
        enabledValues.Any(value => string.Equals(value, "true", StringComparison.OrdinalIgnoreCase));
    var apiKeys = NormalizeApiKeys(ReadFormValue(form, "apiKeys"));
    var scanIntervalMinutes = form.TryGetValue("alertScanIntervalMinutes", out var intervalValues) &&
        int.TryParse(intervalValues.ToString(), out var parsedInterval)
        ? AlertScanOptions.NormalizeMinutes(parsedInterval)
        : AlertScanOptions.DefaultIntervalMinutes;

    await using var db = await dbFactory.CreateDbContextAsync();
    var setting = await db.IntegrationSettings.FirstOrDefaultAsync(item => item.Kind == IntegrationKind.FlightProvider);
    if (setting is null)
    {
        setting = new IntegrationSetting { Kind = IntegrationKind.FlightProvider };
        db.IntegrationSettings.Add(setting);
    }

    var options = System.Text.Json.JsonSerializer.Deserialize<FlightProviderOptions>(
        setting.SettingsJson,
        new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)) ?? new();
    options.ProviderType = "SerpApi";
    options.SerpApiApiKeys = apiKeys;
    options.SerpApiApiKey = apiKeys.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "";
    options.AlertScanIntervalMinutes = scanIntervalMinutes;

    setting.Enabled = enabled;
    setting.SettingsJson = System.Text.Json.JsonSerializer.Serialize(
        options,
        new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
    setting.UpdatedAt = DateTimeOffset.UtcNow;
    await db.SaveChangesAsync();

    return Results.LocalRedirect("/admin/integrations?saved=true");
}).RequireAuthorization(policy => policy.RequireRole("Admin"))
  .DisableAntiforgery();

app.MapPost("/admin/reminders/save", async (
    HttpContext context,
    IDbContextFactory<ApplicationDbContext> dbFactory,
    IConfiguration configuration) =>
{
    var form = await context.Request.ReadFormAsync();
    var jsonOptions = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    var whatsApp = new WhatsAppOptions
    {
        EndpointUrl = ReadFormValue(form, "whatsAppEndpointUrl"),
        HttpMethod = ReadFormValue(form, "whatsAppHttpMethod", "POST"),
        HeadersJson = ReadFormValue(form, "whatsAppHeadersJson", "{}"),
        To = ReadFormValue(form, "whatsAppTo"),
        BodyTemplate = ReadFormValue(form, "whatsAppBodyTemplate", "{\"to\":\"{{to}}\",\"message\":\"{{message}}\"}")
    };
    var webPush = new WebPushOptions
    {
        PublicKey = ReadFormValue(form, "vapidPublicKey"),
        PrivateKey = ReadFormValue(form, "vapidPrivateKey"),
        Subject = ReadFormValue(form, "vapidSubject", "mailto:admin@example.com")
    };

    await using var db = await dbFactory.CreateDbContextAsync();
    var email = await LoadMergedEmailOptionsAsync(db, configuration);
    await SaveIntegrationSettingAsync(db, IntegrationKind.Email, HasCheckedValue(form, "emailEnabled"), email, jsonOptions);
    await SaveIntegrationSettingAsync(db, IntegrationKind.WhatsApp, HasCheckedValue(form, "whatsAppEnabled"), whatsApp, jsonOptions);
    await SaveIntegrationSettingAsync(db, IntegrationKind.WebPush, HasCheckedValue(form, "webPushEnabled"), webPush, jsonOptions);
    await db.SaveChangesAsync();

    return Results.LocalRedirect("/admin/reminders?saved=true");
}).RequireAuthorization(policy => policy.RequireRole("Admin"))
  .DisableAntiforgery();

app.MapPost("/alerts/{id:int}/toggle", async (
    int id,
    ClaimsPrincipal user,
    IDbContextFactory<ApplicationDbContext> dbFactory) =>
{
    var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
    if (string.IsNullOrWhiteSpace(userId))
    {
        return Results.Unauthorized();
    }

    await using var db = await dbFactory.CreateDbContextAsync();
    var alert = await db.PriceAlerts.FirstOrDefaultAsync(item => item.Id == id && item.UserId == userId);
    if (alert is null)
    {
        return Results.NotFound();
    }

    if (!alert.IsActive)
    {
        var policyResult = await ValidateActiveAlertPolicyAsync(db, userId, alert.FlexibleDates);
        if (policyResult.ErrorKey is not null)
        {
            return Results.LocalRedirect($"/alerts?error={Uri.EscapeDataString(policyResult.ErrorKey)}&limit={policyResult.Limit}");
        }
    }

    alert.IsActive = !alert.IsActive;
    await db.SaveChangesAsync();
    return Results.LocalRedirect("/alerts");
}).RequireAuthorization();

app.MapPost("/alerts/{id:int}/delete", async (
    int id,
    ClaimsPrincipal user,
    IDbContextFactory<ApplicationDbContext> dbFactory) =>
{
    var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
    if (string.IsNullOrWhiteSpace(userId))
    {
        return Results.Unauthorized();
    }

    await using var db = await dbFactory.CreateDbContextAsync();
    var alert = await db.PriceAlerts.FirstOrDefaultAsync(item => item.Id == id && item.UserId == userId);
    if (alert is null)
    {
        return Results.NotFound();
    }

    db.PriceAlerts.Remove(alert);
    await db.SaveChangesAsync();
    return Results.LocalRedirect("/alerts");
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

static string ReadFormValue(IFormCollection form, string key, string fallback = "")
{
    return form.TryGetValue(key, out var value) ? value.ToString().Trim() : fallback;
}

static bool HasCheckedValue(IFormCollection form, string key)
{
    return form.TryGetValue(key, out var values) &&
        values.Any(value => string.Equals(value, "true", StringComparison.OrdinalIgnoreCase));
}

static string NormalizeApiKeys(string value)
{
    return string.Join(
        "\n",
        value.Split(['\r', '\n', ',', ';', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal));
}

static async Task<EmailOptions> LoadMergedEmailOptionsAsync(ApplicationDbContext db, IConfiguration configuration)
{
    var setting = await db.IntegrationSettings
        .AsNoTracking()
        .FirstOrDefaultAsync(item => item.Kind == IntegrationKind.Email);
    return EmailOptionsResolver.MergeWithConfigurationFallback(setting?.SettingsJson, configuration);
}

static async Task SaveIntegrationSettingAsync<T>(
    ApplicationDbContext db,
    IntegrationKind kind,
    bool enabled,
    T options,
    System.Text.Json.JsonSerializerOptions jsonOptions)
{
    var setting = await db.IntegrationSettings.FirstOrDefaultAsync(item => item.Kind == kind);
    if (setting is null)
    {
        setting = new IntegrationSetting { Kind = kind };
        db.IntegrationSettings.Add(setting);
    }

    setting.Enabled = enabled;
    setting.SettingsJson = System.Text.Json.JsonSerializer.Serialize(options, jsonOptions);
    setting.UpdatedAt = DateTimeOffset.UtcNow;
}

static async Task<(string? ErrorKey, int Limit)> ValidateActiveAlertPolicyAsync(
    ApplicationDbContext db,
    string userId,
    bool flexibleDates)
{
    var setting = await db.IntegrationSettings
        .AsNoTracking()
        .FirstOrDefaultAsync(item => item.Kind == IntegrationKind.AlertPolicy);
    var policy = setting is null
        ? new AlertPolicyOptions()
        : System.Text.Json.JsonSerializer.Deserialize<AlertPolicyOptions>(
            setting.SettingsJson,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)) ?? new();

    if (policy.MaxAlertsPerUser <= 0 &&
        policy.MaxFlexibleAlertsPerUser <= 0 &&
        policy.MaxSpecificAlertsPerUser <= 0)
    {
        return (null, 0);
    }

    var activeAlerts = await db.PriceAlerts
        .AsNoTracking()
        .Where(alert => alert.UserId == userId && alert.IsActive)
        .Select(alert => alert.FlexibleDates)
        .ToListAsync();

    if (policy.MaxAlertsPerUser > 0 && activeAlerts.Count >= policy.MaxAlertsPerUser)
    {
        return ("ActiveAlertLimitReached", policy.MaxAlertsPerUser);
    }

    if (flexibleDates &&
        policy.MaxFlexibleAlertsPerUser > 0 &&
        activeAlerts.Count(alert => alert) >= policy.MaxFlexibleAlertsPerUser)
    {
        return ("ActiveFlexibleAlertLimitReached", policy.MaxFlexibleAlertsPerUser);
    }

    if (!flexibleDates &&
        policy.MaxSpecificAlertsPerUser > 0 &&
        activeAlerts.Count(alert => !alert) >= policy.MaxSpecificAlertsPerUser)
    {
        return ("ActiveSpecificAlertLimitReached", policy.MaxSpecificAlertsPerUser);
    }

    return (null, 0);
}

static string LocalizedLocationName(FlightLocation location, string culture)
{
    return culture switch
    {
        "fr" when !string.IsNullOrWhiteSpace(location.NameFr) => location.NameFr,
        "ar" when !string.IsNullOrWhiteSpace(location.NameAr) => location.NameAr,
        _ => location.Name
    };
}

static string LocalizedCountryName(FlightLocation location, string culture)
{
    return culture switch
    {
        "fr" when !string.IsNullOrWhiteSpace(location.CountryNameFr) => location.CountryNameFr,
        "ar" when !string.IsNullOrWhiteSpace(location.CountryNameAr) => location.CountryNameAr,
        _ => location.CountryName ?? ""
    };
}

static string LocalizedContinent(FlightLocation location, string culture)
{
    return culture switch
    {
        "fr" when !string.IsNullOrWhiteSpace(location.ContinentFr) => location.ContinentFr,
        "ar" when !string.IsNullOrWhiteSpace(location.ContinentAr) => location.ContinentAr,
        _ => location.Continent
    };
}

static string LocalizedLocationType(LocationType type)
{
    return type switch
    {
        LocationType.Airport => UiText.T("Airport"),
        LocationType.City => UiText.T("City"),
        LocationType.Country => UiText.T("Country"),
        LocationType.Continent => UiText.T("Continent"),
        _ => type.ToString()
    };
}

static object BuildLocationSuggestion(FlightLocation location, string culture)
{
    return new
    {
        value = location.Type == LocationType.Airport ? location.Code : LocalizedLocationName(location, culture),
        type = location.Type.ToString(),
        typeLabel = LocalizedLocationType(location.Type),
        code = location.Code,
        primary = location.Type == LocationType.Airport
            ? $"{location.Code} · {LocalizedLocationName(location, culture)}"
            : LocalizedLocationName(location, culture),
        secondary = location.Type switch
        {
            LocationType.Continent => UiText.T("Continent"),
            LocationType.Country => $"{LocalizedContinent(location, culture)} · {UiText.T("Country")}",
            _ => $"{LocalizedCountryName(location, culture)} · {LocalizedContinent(location, culture)}"
        }
    };
}

static Task<List<FlightLocation>> QueryLocalizedLocationSuggestionsAsync(
    ApplicationDbContext db,
    string lowered,
    string prefixPattern,
    string culture,
    CancellationToken cancellationToken)
{
    return culture switch
    {
        "fr" => db.FlightLocations
            .FromSqlInterpolated($"""
                SELECT *
                FROM "FlightLocations"
                WHERE lower("Code") LIKE {prefixPattern} ESCAPE '\'
                    OR lower(coalesce("CountryCode", '')) LIKE {prefixPattern} ESCAPE '\'
                    OR lower(coalesce("NameFr", "Name")) LIKE {prefixPattern} ESCAPE '\'
                    OR lower(coalesce("CountryNameFr", "CountryName", '')) LIKE {prefixPattern} ESCAPE '\'
                    OR lower(coalesce("ContinentFr", "Continent")) LIKE {prefixPattern} ESCAPE '\'
                ORDER BY
                    CASE
                        WHEN lower("Code") = {lowered} THEN 0
                        WHEN lower(coalesce("NameFr", "Name")) = {lowered} THEN 1
                        WHEN lower("Code") LIKE {prefixPattern} ESCAPE '\' THEN 2
                        WHEN lower(coalesce("NameFr", "Name")) LIKE {prefixPattern} ESCAPE '\' THEN 3
                        WHEN lower(coalesce("CountryNameFr", "CountryName", '')) LIKE {prefixPattern} ESCAPE '\' THEN 4
                        WHEN lower(coalesce("ContinentFr", "Continent")) LIKE {prefixPattern} ESCAPE '\' THEN 5
                        ELSE 6
                    END,
                    CASE "Type" WHEN 2 THEN 0 WHEN 1 THEN 1 WHEN 0 THEN 2 WHEN 3 THEN 3 ELSE 4 END,
                    coalesce("NameFr", "Name"),
                    "Code"
                LIMIT 300
                """)
            .AsNoTracking()
            .ToListAsync(cancellationToken),
        "ar" => db.FlightLocations
            .FromSqlInterpolated($"""
                SELECT *
                FROM "FlightLocations"
                WHERE lower("Code") LIKE {prefixPattern} ESCAPE '\'
                    OR lower(coalesce("CountryCode", '')) LIKE {prefixPattern} ESCAPE '\'
                    OR lower(coalesce("NameAr", "Name")) LIKE {prefixPattern} ESCAPE '\'
                    OR lower(coalesce("CountryNameAr", "CountryName", '')) LIKE {prefixPattern} ESCAPE '\'
                    OR lower(coalesce("ContinentAr", "Continent")) LIKE {prefixPattern} ESCAPE '\'
                ORDER BY
                    CASE
                        WHEN lower("Code") = {lowered} THEN 0
                        WHEN lower(coalesce("NameAr", "Name")) = {lowered} THEN 1
                        WHEN lower("Code") LIKE {prefixPattern} ESCAPE '\' THEN 2
                        WHEN lower(coalesce("NameAr", "Name")) LIKE {prefixPattern} ESCAPE '\' THEN 3
                        WHEN lower(coalesce("CountryNameAr", "CountryName", '')) LIKE {prefixPattern} ESCAPE '\' THEN 4
                        WHEN lower(coalesce("ContinentAr", "Continent")) LIKE {prefixPattern} ESCAPE '\' THEN 5
                        ELSE 6
                    END,
                    CASE "Type" WHEN 2 THEN 0 WHEN 1 THEN 1 WHEN 0 THEN 2 WHEN 3 THEN 3 ELSE 4 END,
                    coalesce("NameAr", "Name"),
                    "Code"
                LIMIT 300
                """)
            .AsNoTracking()
            .ToListAsync(cancellationToken),
        _ => db.FlightLocations
            .FromSqlInterpolated($"""
                SELECT *
                FROM "FlightLocations"
                WHERE lower("Code") LIKE {prefixPattern} ESCAPE '\'
                    OR lower(coalesce("CountryCode", '')) LIKE {prefixPattern} ESCAPE '\'
                    OR lower("Name") LIKE {prefixPattern} ESCAPE '\'
                    OR lower(coalesce("CountryName", '')) LIKE {prefixPattern} ESCAPE '\'
                    OR lower("Continent") LIKE {prefixPattern} ESCAPE '\'
                ORDER BY
                    CASE
                        WHEN lower("Code") = {lowered} THEN 0
                        WHEN lower("Name") = {lowered} THEN 1
                        WHEN lower("Code") LIKE {prefixPattern} ESCAPE '\' THEN 2
                        WHEN lower("Name") LIKE {prefixPattern} ESCAPE '\' THEN 3
                        WHEN lower(coalesce("CountryName", '')) LIKE {prefixPattern} ESCAPE '\' THEN 4
                        WHEN lower("Continent") LIKE {prefixPattern} ESCAPE '\' THEN 5
                        ELSE 6
                    END,
                    CASE "Type" WHEN 2 THEN 0 WHEN 1 THEN 1 WHEN 0 THEN 2 WHEN 3 THEN 3 ELSE 4 END,
                    "Name",
                    "Code"
                LIMIT 300
                """)
            .AsNoTracking()
            .ToListAsync(cancellationToken)
    };
}

static string EscapeLikePattern(string value)
{
    return value
        .Replace("\\", "\\\\")
        .Replace("%", "\\%")
        .Replace("_", "\\_");
}

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
