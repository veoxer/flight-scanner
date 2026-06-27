using FlightScanner.Data;
using FlightScanner.Features.Alerts;
using FlightScanner.Features.Flights;
using FlightScanner.Features.Integrations;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace FlightScanner.Features.Setup;

public sealed class StartupInitializer(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    IHttpClientFactory httpClientFactory,
    ILogger<StartupInitializer> logger)
{
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();

        await db.Database.EnsureCreatedAsync(cancellationToken);
        await EnsureSchemaAsync(db, cancellationToken);

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

        var existingLocations = await db.FlightLocations
            .Select(location => new { location.Type, location.Code })
            .ToListAsync(cancellationToken);
        var existingLocationKeys = existingLocations
            .Select(location => $"{location.Type}:{location.Code}".ToUpperInvariant())
            .ToHashSet();
        var missingLocations = SeedLocations()
            .Where(location => existingLocationKeys.Add($"{location.Type}:{location.Code}".ToUpperInvariant()))
            .ToList();
        db.FlightLocations.AddRange(missingLocations);

        var importedLocations = await TryImportWorldLocationsAsync(db, existingLocationKeys, cancellationToken);
        db.FlightLocations.AddRange(importedLocations);

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

    private static async Task EnsureSchemaAsync(ApplicationDbContext db, CancellationToken cancellationToken)
    {
        await db.Database.ExecuteSqlRawAsync(
            """
            ALTER TABLE "PriceAlerts"
            ADD COLUMN IF NOT EXISTS "TargetMode" character varying(8) NOT NULL DEFAULT 'Max';
            """,
            cancellationToken);
    }

    private async Task<IReadOnlyList<FlightLocation>> TryImportWorldLocationsAsync(
        ApplicationDbContext db,
        HashSet<string> existingLocationKeys,
        CancellationToken cancellationToken)
    {
        var importEnabled = !bool.TryParse(configuration["LOCATION_DATA_IMPORT_ENABLED"], out var enabled) || enabled;
        if (!importEnabled)
        {
            return [];
        }

        var refresh = bool.TryParse(configuration["LOCATION_DATA_IMPORT_REFRESH"], out var refreshEnabled) && refreshEnabled;
        var importSetting = await db.AppSettings
            .FirstOrDefaultAsync(setting => setting.Key == "LocationCatalogImportCompleted", cancellationToken);
        if (!refresh && importSetting?.Value == "true")
        {
            return [];
        }

        try
        {
            var importedLocations = await DownloadWorldLocationsAsync(cancellationToken);
            var missingLocations = importedLocations
                .Where(location => existingLocationKeys.Add($"{location.Type}:{location.Code}".ToUpperInvariant()))
                .ToList();

            if (missingLocations.Count > 0)
            {
                importSetting ??= new AppSetting { Key = "LocationCatalogImportCompleted" };
                importSetting.Value = "true";
                if (db.Entry(importSetting).State == EntityState.Detached)
                {
                    db.AppSettings.Add(importSetting);
                }

                logger.LogInformation("Imported {LocationCount} world flight locations from public catalog.", missingLocations.Count);
            }

            return missingLocations;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "World flight location import failed. The app will use the built-in fallback catalog.");
            return [];
        }
    }

    private async Task<IReadOnlyList<FlightLocation>> DownloadWorldLocationsAsync(CancellationToken cancellationToken)
    {
        const string airportsUrl = "https://davidmegginson.github.io/ourairports-data/airports.csv";
        const string countriesUrl = "https://davidmegginson.github.io/ourairports-data/countries.csv";

        var client = httpClientFactory.CreateClient("location-data");
        var countriesCsv = await client.GetStringAsync(countriesUrl, cancellationToken);
        var airportsCsv = await client.GetStringAsync(airportsUrl, cancellationToken);
        var countryRows = ReadCsv(countriesCsv);
        var airportRows = ReadCsv(airportsCsv);

        var countries = countryRows
            .Where(row => !string.IsNullOrWhiteSpace(Value(row, "code")))
            .ToDictionary(
                row => Value(row, "code"),
                row => new CountryInfo(
                    Value(row, "name"),
                    ContinentName(Value(row, "continent"))),
                StringComparer.OrdinalIgnoreCase);

        var locations = new List<FlightLocation>();

        foreach (var (code, name) in ContinentNames())
        {
            locations.Add(new FlightLocation
            {
                Type = LocationType.Continent,
                Code = code,
                Name = name,
                Continent = name
            });
        }

        foreach (var (code, country) in countries.OrderBy(country => country.Value.Name))
        {
            locations.Add(new FlightLocation
            {
                Type = LocationType.Country,
                Code = code,
                Name = country.Name,
                CountryCode = code,
                CountryName = country.Name,
                Continent = country.Continent
            });
        }

        var airportLocations = airportRows
            .Where(row => !Value(row, "type").Equals("closed", StringComparison.OrdinalIgnoreCase))
            .Select(row => BuildAirportLocation(row, countries))
            .Where(location => location is not null)
            .Cast<FlightLocation>()
            .GroupBy(location => location.Code, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
        locations.AddRange(airportLocations);

        var cityLocations = airportRows
            .Where(row => !Value(row, "type").Equals("closed", StringComparison.OrdinalIgnoreCase))
            .Where(row => !string.IsNullOrWhiteSpace(Value(row, "municipality")))
            .GroupBy(row => $"{Value(row, "iso_country")}:{Value(row, "municipality")}", StringComparer.OrdinalIgnoreCase)
            .Select(group => BuildCityLocation(group, countries))
            .Where(location => location is not null)
            .Cast<FlightLocation>()
            .GroupBy(location => location.Code, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
        locations.AddRange(cityLocations);

        return locations;
    }

    private static FlightLocation? BuildAirportLocation(
        Dictionary<string, string> row,
        Dictionary<string, CountryInfo> countries)
    {
        var code = FirstNonEmpty(Value(row, "iata_code"), Value(row, "ident"));
        if (string.IsNullOrWhiteSpace(code))
        {
            return null;
        }

        code = Truncate(code.ToUpperInvariant(), 16);
        var countryCode = Value(row, "iso_country");
        countries.TryGetValue(countryCode, out var country);
        var continent = !string.IsNullOrWhiteSpace(country.Continent)
            ? country.Continent
            : ContinentName(Value(row, "continent"));

        return new FlightLocation
        {
            Type = LocationType.Airport,
            Code = code,
            Name = Truncate(Value(row, "name"), 120),
            CountryCode = Truncate(countryCode, 2),
            CountryName = Truncate(country.Name, 80),
            Continent = Truncate(continent, 32)
        };
    }

    private static FlightLocation? BuildCityLocation(
        IGrouping<string, Dictionary<string, string>> group,
        Dictionary<string, CountryInfo> countries)
    {
        var first = group.First();
        var cityName = Value(first, "municipality");
        var countryCode = Value(first, "iso_country");
        if (string.IsNullOrWhiteSpace(cityName) || string.IsNullOrWhiteSpace(countryCode))
        {
            return null;
        }

        countries.TryGetValue(countryCode, out var country);
        var primaryAirportCode = group
            .Select(row => Value(row, "iata_code"))
            .FirstOrDefault(code => code.Length == 3);
        var code = string.IsNullOrWhiteSpace(primaryAirportCode)
            ? BuildCityCode(countryCode, cityName)
            : primaryAirportCode.ToUpperInvariant();

        return new FlightLocation
        {
            Type = LocationType.City,
            Code = Truncate(code, 16),
            Name = Truncate(cityName, 120),
            CountryCode = Truncate(countryCode, 2),
            CountryName = Truncate(country.Name, 80),
            Continent = Truncate(country.Continent, 32)
        };
    }

    private string BuildDefaultSettingsJson(IntegrationKind kind)
    {
        return kind switch
        {
            IntegrationKind.FlightProvider => Serialize(new FlightProviderOptions
            {
                ProviderType = configuration["FLIGHT_PROVIDER_TYPE"] ?? "Amadeus",
                AmadeusEnvironment = configuration["AMADEUS_ENVIRONMENT"] ?? "Test",
                AmadeusClientId = configuration["AMADEUS_CLIENT_ID"] ?? "",
                AmadeusClientSecret = configuration["AMADEUS_CLIENT_SECRET"] ?? "",
                AmadeusMaxOffers = int.TryParse(configuration["AMADEUS_MAX_OFFERS"], out var maxOffers) ? maxOffers : 20,
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
            ("AN", "Antarctica", "Antarctica"),
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

        var cities = new[]
        {
            ("NYC", "New York", "US", "United States", "North America"),
            ("LON", "London", "GB", "United Kingdom", "Europe"),
            ("PAR", "Paris", "FR", "France", "Europe"),
            ("AMS", "Amsterdam", "NL", "Netherlands", "Europe"),
            ("CAS", "Casablanca", "MA", "Morocco", "Africa"),
            ("CAI", "Cairo", "EG", "Egypt", "Africa"),
            ("JNB", "Johannesburg", "ZA", "South Africa", "Africa"),
            ("DXB", "Dubai", "AE", "United Arab Emirates", "Asia"),
            ("SIN", "Singapore", "SG", "Singapore", "Asia"),
            ("TYO", "Tokyo", "JP", "Japan", "Asia"),
            ("SYD", "Sydney", "AU", "Australia", "Oceania"),
            ("AKL", "Auckland", "NZ", "New Zealand", "Oceania"),
            ("SAO", "Sao Paulo", "BR", "Brazil", "South America"),
            ("BUE", "Buenos Aires", "AR", "Argentina", "South America")
        };

        foreach (var (code, name, countryCode, countryName, continent) in cities)
        {
            yield return new FlightLocation
            {
                Type = LocationType.City,
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

    private static IReadOnlyList<Dictionary<string, string>> ReadCsv(string csv)
    {
        using var reader = new StringReader(csv);
        var headers = ParseCsvLine(reader.ReadLine() ?? "");
        var rows = new List<Dictionary<string, string>>();

        while (reader.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var values = ParseCsvLine(line);
            var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < headers.Count; index++)
            {
                row[headers[index]] = index < values.Count ? values[index] : "";
            }

            rows.Add(row);
        }

        return rows;
    }

    private static IReadOnlyList<string> ParseCsvLine(string line)
    {
        var values = new List<string>();
        var current = new List<char>();
        var inQuotes = false;

        for (var index = 0; index < line.Length; index++)
        {
            var character = line[index];
            if (character == '"')
            {
                if (inQuotes && index + 1 < line.Length && line[index + 1] == '"')
                {
                    current.Add('"');
                    index++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }

                continue;
            }

            if (character == ',' && !inQuotes)
            {
                values.Add(new string(current.ToArray()).Trim());
                current.Clear();
                continue;
            }

            current.Add(character);
        }

        values.Add(new string(current.ToArray()).Trim());
        return values;
    }

    private static string Value(Dictionary<string, string> row, string key)
    {
        return row.TryGetValue(key, out var value) ? value.Trim() : "";
    }

    private static string FirstNonEmpty(params string[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "";
    }

    private static string BuildCityCode(string countryCode, string cityName)
    {
        var slug = new string(cityName
            .Normalize(NormalizationForm.FormD)
            .Where(character => CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
            .Select(character => char.IsLetterOrDigit(character) ? char.ToUpperInvariant(character) : '-')
            .ToArray());
        slug = string.Join("-", slug.Split('-', StringSplitOptions.RemoveEmptyEntries));
        return Truncate($"{countryCode.ToUpperInvariant()}-{slug}", 16);
    }

    private static string Truncate(string value, int maxLength)
    {
        return value.Length <= maxLength ? value : value[..maxLength];
    }

    private static string ContinentName(string code)
    {
        return ContinentNames().FirstOrDefault(continent => continent.Code.Equals(code, StringComparison.OrdinalIgnoreCase)).Name ?? code;
    }

    private static IReadOnlyList<(string Code, string Name)> ContinentNames() =>
    [
        ("AF", "Africa"),
        ("AN", "Antarctica"),
        ("AS", "Asia"),
        ("EU", "Europe"),
        ("NA", "North America"),
        ("OC", "Oceania"),
        ("SA", "South America")
    ];

    private readonly record struct CountryInfo(string Name, string Continent);
}
