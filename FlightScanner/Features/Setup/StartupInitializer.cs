using FlightScanner.Data;
using FlightScanner.Features.Alerts;
using FlightScanner.Features.Flights;
using FlightScanner.Features.Integrations;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace FlightScanner.Features.Setup;

public sealed class StartupInitializer(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<StartupInitializer> logger)
{
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();

        await db.Database.EnsureCreatedAsync(cancellationToken);

        foreach (var role in new[] { "Admin", "User" })
        {
            if (!await roleManager.RoleExistsAsync(role))
            {
                await roleManager.CreateAsync(new IdentityRole(role));
            }
        }

        if (!await db.AppSettings.AnyAsync(setting => setting.Key == "SetupComplete", cancellationToken))
        {
            db.AppSettings.Add(new AppSetting { Key = "SetupComplete", Value = "false" });
        }

        if (!await db.FlightLocations.AnyAsync(cancellationToken))
        {
            db.FlightLocations.AddRange(SeedLocations());
        }

        foreach (var kind in Enum.GetValues<IntegrationKind>())
        {
            if (!await db.IntegrationSettings.AnyAsync(setting => setting.Kind == kind, cancellationToken))
            {
                db.IntegrationSettings.Add(new IntegrationSetting
                {
                    Kind = kind,
                    Enabled = kind == IntegrationKind.FlightProvider,
                    SettingsJson = BuildDefaultSettingsJson(kind)
                });
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Application startup initialization completed.");
    }

    private string BuildDefaultSettingsJson(IntegrationKind kind)
    {
        return kind switch
        {
            IntegrationKind.FlightProvider => Serialize(new FlightProviderOptions
            {
                EndpointUrl = configuration["FLIGHT_PROVIDER_URL"] ?? "",
                HttpMethod = configuration["FLIGHT_PROVIDER_HTTP_METHOD"] ?? "POST",
                HeadersJson = configuration["FLIGHT_PROVIDER_HEADERS_JSON"] ?? "{}",
                BodyTemplate = configuration["FLIGHT_PROVIDER_BODY_TEMPLATE"] ?? "",
                PriceJsonPath = configuration["FLIGHT_PROVIDER_PRICE_JSON_PATH"] ?? "$.offers[0].price",
                CurrencyJsonPath = configuration["FLIGHT_PROVIDER_CURRENCY_JSON_PATH"] ?? "$.offers[0].currency",
                UrlJsonPath = configuration["FLIGHT_PROVIDER_URL_JSON_PATH"] ?? "$.offers[0].url"
            }),
            IntegrationKind.Email => Serialize(new EmailOptions
            {
                SmtpHost = configuration["SMTP_HOST"] ?? "smtp.gmail.com",
                SmtpPort = int.TryParse(configuration["SMTP_PORT"], out var smtpPort) ? smtpPort : 587,
                UseStartTls = !bool.TryParse(configuration["SMTP_USE_STARTTLS"], out var useStartTls) || useStartTls,
                FromAddress = configuration["SMTP_FROM"] ?? "",
                UserName = configuration["SMTP_USER"] ?? "",
                Password = configuration["SMTP_PASSWORD"] ?? ""
            }),
            IntegrationKind.WhatsApp => Serialize(new WhatsAppOptions
            {
                EndpointUrl = configuration["WHATSAPP_API_URL"] ?? "",
                HttpMethod = configuration["WHATSAPP_HTTP_METHOD"] ?? "POST",
                HeadersJson = configuration["WHATSAPP_HEADERS_JSON"] ?? "{}",
                BodyTemplate = configuration["WHATSAPP_BODY_TEMPLATE"] ?? "{\"to\":\"{{to}}\",\"message\":\"{{message}}\"}"
            }),
            IntegrationKind.WebPush => Serialize(new WebPushOptions
            {
                PublicKey = configuration["VAPID_PUBLIC_KEY"] ?? "",
                PrivateKey = configuration["VAPID_PRIVATE_KEY"] ?? "",
                Subject = configuration["VAPID_SUBJECT"] ?? "mailto:admin@example.com"
            }),
            _ => "{}"
        };
    }

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    private static IEnumerable<FlightLocation> SeedLocations()
    {
        var continents = new[]
        {
            ("AF", "Africa", "Africa"),
            ("AS", "Asia", "Asia"),
            ("EU", "Europe", "Europe"),
            ("NA", "North America", "North America"),
            ("SA", "South America", "South America"),
            ("OC", "Oceania", "Oceania")
        };

        foreach (var (code, name, continent) in continents)
        {
            yield return new FlightLocation { Type = LocationType.Continent, Code = code, Name = name, Continent = continent };
        }

        var airports = new[]
        {
            ("JFK", "New York JFK", "US", "United States", "North America"),
            ("LAX", "Los Angeles", "US", "United States", "North America"),
            ("YYZ", "Toronto Pearson", "CA", "Canada", "North America"),
            ("LHR", "London Heathrow", "GB", "United Kingdom", "Europe"),
            ("CDG", "Paris Charles de Gaulle", "FR", "France", "Europe"),
            ("AMS", "Amsterdam Schiphol", "NL", "Netherlands", "Europe"),
            ("CMN", "Casablanca Mohammed V", "MA", "Morocco", "Africa"),
            ("CAI", "Cairo", "EG", "Egypt", "Africa"),
            ("JNB", "Johannesburg OR Tambo", "ZA", "South Africa", "Africa"),
            ("DXB", "Dubai", "AE", "United Arab Emirates", "Asia"),
            ("SIN", "Singapore Changi", "SG", "Singapore", "Asia"),
            ("HND", "Tokyo Haneda", "JP", "Japan", "Asia"),
            ("SYD", "Sydney", "AU", "Australia", "Oceania"),
            ("AKL", "Auckland", "NZ", "New Zealand", "Oceania"),
            ("GRU", "Sao Paulo Guarulhos", "BR", "Brazil", "South America"),
            ("EZE", "Buenos Aires Ezeiza", "AR", "Argentina", "South America")
        };

        foreach (var (code, name, countryCode, countryName, continent) in airports)
        {
            yield return new FlightLocation
            {
                Type = LocationType.Airport,
                Code = code,
                Name = name,
                CountryCode = countryCode,
                CountryName = countryName,
                Continent = continent
            };
        }

        foreach (var country in airports.Select(a => (a.Item3, a.Item4, a.Item5)).Distinct())
        {
            yield return new FlightLocation
            {
                Type = LocationType.Country,
                Code = country.Item1,
                Name = country.Item2,
                CountryCode = country.Item1,
                CountryName = country.Item2,
                Continent = country.Item3
            };
        }
    }
}
