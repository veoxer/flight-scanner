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
        await TryHydrateLocationCoordinatesAsync(db, cancellationToken);

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

            ALTER TABLE "PriceAlerts"
            ADD COLUMN IF NOT EXISTS "MaxTargetPrice" numeric(10,2);

            ALTER TABLE "PriceAlerts"
            ADD COLUMN IF NOT EXISTS "MinTargetPrice" numeric(10,2);

            ALTER TABLE "FlightLocations"
            ADD COLUMN IF NOT EXISTS "Latitude" double precision;

            ALTER TABLE "FlightLocations"
            ADD COLUMN IF NOT EXISTS "Longitude" double precision;

            UPDATE "PriceAlerts"
            SET "MaxTargetPrice" = "TargetPrice"
            WHERE "MaxTargetPrice" IS NULL
                AND COALESCE("TargetMode", 'Max') <> 'Min'
                AND "TargetPrice" > 0;

            UPDATE "PriceAlerts"
            SET "MinTargetPrice" = "TargetPrice"
            WHERE "MinTargetPrice" IS NULL
                AND "TargetMode" = 'Min'
                AND "TargetPrice" > 0;
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

    private async Task TryHydrateLocationCoordinatesAsync(
        ApplicationDbContext db,
        CancellationToken cancellationToken)
    {
        var importEnabled = !bool.TryParse(configuration["LOCATION_DATA_IMPORT_ENABLED"], out var enabled) || enabled;
        if (!importEnabled)
        {
            return;
        }

        var hydrationSetting = await db.AppSettings
            .FirstOrDefaultAsync(setting => setting.Key == "LocationCoordinatesHydrated", cancellationToken);
        if (hydrationSetting?.Value == "true")
        {
            return;
        }

        if (!await db.FlightLocations.AnyAsync(location => location.Latitude == null || location.Longitude == null, cancellationToken))
        {
            await MarkCoordinateHydrationCompleteAsync(db, hydrationSetting, cancellationToken);
            return;
        }

        try
        {
            var importedLocations = await DownloadWorldLocationsAsync(cancellationToken);
            var coordinates = importedLocations
                .Where(location => location.Latitude is not null && location.Longitude is not null)
                .GroupBy(location => $"{location.Type}:{location.Code}".ToUpperInvariant())
                .ToDictionary(
                    group => group.Key,
                    group => group.First(),
                    StringComparer.OrdinalIgnoreCase);
            var locations = await db.FlightLocations
                .Where(location => location.Latitude == null || location.Longitude == null)
                .ToListAsync(cancellationToken);
            var updated = 0;

            foreach (var location in locations)
            {
                if (coordinates.TryGetValue($"{location.Type}:{location.Code}".ToUpperInvariant(), out var imported))
                {
                    location.Latitude = imported.Latitude;
                    location.Longitude = imported.Longitude;
                    updated++;
                }
            }

            await MarkCoordinateHydrationCompleteAsync(db, hydrationSetting, cancellationToken);
            logger.LogInformation("Hydrated coordinates for {LocationCount} existing flight locations.", updated);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Flight location coordinate hydration failed. Route maps will use browser fallbacks where available.");
        }
    }

    private static async Task MarkCoordinateHydrationCompleteAsync(
        ApplicationDbContext db,
        AppSetting? hydrationSetting,
        CancellationToken cancellationToken)
    {
        hydrationSetting ??= new AppSetting { Key = "LocationCoordinatesHydrated" };
        hydrationSetting.Value = "true";
        if (db.Entry(hydrationSetting).State == EntityState.Detached)
        {
            db.AppSettings.Add(hydrationSetting);
        }

        await db.SaveChangesAsync(cancellationToken);
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

        var countryCenters = airportRows
            .Where(row => !Value(row, "type").Equals("closed", StringComparison.OrdinalIgnoreCase))
            .Where(row => TryReadCoordinate(row, out _, out _))
            .GroupBy(row => Value(row, "iso_country"), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => (
                    Latitude: group.Select(row => double.Parse(Value(row, "latitude_deg"), CultureInfo.InvariantCulture)).Average(),
                    Longitude: group.Select(row => double.Parse(Value(row, "longitude_deg"), CultureInfo.InvariantCulture)).Average()),
                StringComparer.OrdinalIgnoreCase);

        foreach (var (code, country) in countries.OrderBy(country => country.Value.Name))
        {
            countryCenters.TryGetValue(code, out var center);
            locations.Add(new FlightLocation
            {
                Type = LocationType.Country,
                Code = code,
                Name = country.Name,
                CountryCode = code,
                CountryName = country.Name,
                Continent = country.Continent,
                Latitude = center.Latitude == 0 && center.Longitude == 0 ? null : center.Latitude,
                Longitude = center.Latitude == 0 && center.Longitude == 0 ? null : center.Longitude
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
        TryReadCoordinate(row, out var latitude, out var longitude);
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
            Continent = Truncate(continent, 32),
            Latitude = latitude,
            Longitude = longitude
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
        var coordinates = group
            .Where(row => TryReadCoordinate(row, out _, out _))
            .Select(row => (
                Latitude: double.Parse(Value(row, "latitude_deg"), CultureInfo.InvariantCulture),
                Longitude: double.Parse(Value(row, "longitude_deg"), CultureInfo.InvariantCulture)))
            .ToList();
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
            Continent = Truncate(country.Continent, 32),
            Latitude = coordinates.Count == 0 ? null : coordinates.Average(coordinate => coordinate.Latitude),
            Longitude = coordinates.Count == 0 ? null : coordinates.Average(coordinate => coordinate.Longitude)
        };
    }

    private static bool TryReadCoordinate(Dictionary<string, string> row, out double latitude, out double longitude)
    {
        var hasLatitude = double.TryParse(Value(row, "latitude_deg"), NumberStyles.Float, CultureInfo.InvariantCulture, out latitude);
        var hasLongitude = double.TryParse(Value(row, "longitude_deg"), NumberStyles.Float, CultureInfo.InvariantCulture, out longitude);
        return hasLatitude && hasLongitude;
    }

    private string BuildDefaultSettingsJson(IntegrationKind kind)
    {
        return kind switch
        {
            IntegrationKind.FlightProvider => Serialize(new FlightProviderOptions
            {
                ProviderType = configuration["FLIGHT_PROVIDER_TYPE"] ?? "SerpApi",
                SerpApiApiKey = configuration["SERPAPI_API_KEY"] ?? "",
                EndpointUrl = configuration["FLIGHT_PROVIDER_URL"] ?? "",
                HttpMethod = configuration["FLIGHT_PROVIDER_HTTP_METHOD"] ?? "POST",
                HeadersJson = configuration["FLIGHT_PROVIDER_HEADERS_JSON"] ?? "{}",
                BodyTemplate = configuration["FLIGHT_PROVIDER_BODY_TEMPLATE"] ?? "",
                PriceJsonPath = configuration["FLIGHT_PROVIDER_PRICE_JSON_PATH"] ?? "$.offers[0].price",
                CurrencyJsonPath = configuration["FLIGHT_PROVIDER_CURRENCY_JSON_PATH"] ?? "$.offers[0].currency",
                UrlJsonPath = configuration["FLIGHT_PROVIDER_URL_JSON_PATH"] ?? "$.offers[0].url",
                AlertScanIntervalMinutes = AlertScanOptions.DefaultIntervalMinutes
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
            ("AF", "Africa", "Africa", 1.65, 17.8),
            ("AN", "Antarctica", "Antarctica", -82.8, 0.0),
            ("AS", "Asia", "Asia", 34.0, 100.0),
            ("EU", "Europe", "Europe", 54.5, 15.0),
            ("NA", "North America", "North America", 48.2, -100.0),
            ("SA", "South America", "South America", -14.6, -58.4),
            ("OC", "Oceania", "Oceania", -25.0, 134.0)
        };

        foreach (var (code, name, continent, latitude, longitude) in continents)
        {
            yield return new FlightLocation { Type = LocationType.Continent, Code = code, Name = name, Continent = continent, Latitude = latitude, Longitude = longitude };
        }

        var airports = new[]
        {
            ("JFK", "New York JFK", "US", "United States", "North America", 40.6413, -73.7781),
            ("LAX", "Los Angeles", "US", "United States", "North America", 33.9416, -118.4085),
            ("YYZ", "Toronto Pearson", "CA", "Canada", "North America", 43.6777, -79.6248),
            ("LHR", "London Heathrow", "GB", "United Kingdom", "Europe", 51.4700, -0.4543),
            ("CDG", "Paris Charles de Gaulle", "FR", "France", "Europe", 49.0097, 2.5479),
            ("AMS", "Amsterdam Schiphol", "NL", "Netherlands", "Europe", 52.3105, 4.7683),
            ("CMN", "Casablanca Mohammed V", "MA", "Morocco", "Africa", 33.3675, -7.5898),
            ("CAI", "Cairo", "EG", "Egypt", "Africa", 30.1120, 31.4000),
            ("JNB", "Johannesburg OR Tambo", "ZA", "South Africa", "Africa", -26.1337, 28.2420),
            ("DXB", "Dubai", "AE", "United Arab Emirates", "Asia", 25.2532, 55.3657),
            ("SIN", "Singapore Changi", "SG", "Singapore", "Asia", 1.3644, 103.9915),
            ("HND", "Tokyo Haneda", "JP", "Japan", "Asia", 35.5494, 139.7798),
            ("SYD", "Sydney", "AU", "Australia", "Oceania", -33.9399, 151.1753),
            ("AKL", "Auckland", "NZ", "New Zealand", "Oceania", -37.0082, 174.7850),
            ("GRU", "Sao Paulo Guarulhos", "BR", "Brazil", "South America", -23.4356, -46.4731),
            ("EZE", "Buenos Aires Ezeiza", "AR", "Argentina", "South America", -34.8222, -58.5358)
        };

        foreach (var (code, name, countryCode, countryName, continent, latitude, longitude) in airports)
        {
            yield return new FlightLocation
            {
                Type = LocationType.Airport,
                Code = code,
                Name = name,
                CountryCode = countryCode,
                CountryName = countryName,
                Continent = continent,
                Latitude = latitude,
                Longitude = longitude
            };
        }

        var cities = new[]
        {
            ("NYC", "New York", "US", "United States", "North America", 40.7128, -74.0060),
            ("LON", "London", "GB", "United Kingdom", "Europe", 51.5072, -0.1276),
            ("PAR", "Paris", "FR", "France", "Europe", 48.8566, 2.3522),
            ("AMS", "Amsterdam", "NL", "Netherlands", "Europe", 52.3676, 4.9041),
            ("CAS", "Casablanca", "MA", "Morocco", "Africa", 33.5731, -7.5898),
            ("CAI", "Cairo", "EG", "Egypt", "Africa", 30.0444, 31.2357),
            ("JNB", "Johannesburg", "ZA", "South Africa", "Africa", -26.2041, 28.0473),
            ("DXB", "Dubai", "AE", "United Arab Emirates", "Asia", 25.2048, 55.2708),
            ("SIN", "Singapore", "SG", "Singapore", "Asia", 1.3521, 103.8198),
            ("TYO", "Tokyo", "JP", "Japan", "Asia", 35.6762, 139.6503),
            ("SYD", "Sydney", "AU", "Australia", "Oceania", -33.8688, 151.2093),
            ("AKL", "Auckland", "NZ", "New Zealand", "Oceania", -36.8509, 174.7645),
            ("SAO", "Sao Paulo", "BR", "Brazil", "South America", -23.5558, -46.6396),
            ("BUE", "Buenos Aires", "AR", "Argentina", "South America", -34.6037, -58.3816)
        };

        foreach (var (code, name, countryCode, countryName, continent, latitude, longitude) in cities)
        {
            yield return new FlightLocation
            {
                Type = LocationType.City,
                Code = code,
                Name = name,
                CountryCode = countryCode,
                CountryName = countryName,
                Continent = continent,
                Latitude = latitude,
                Longitude = longitude
            };
        }

        foreach (var country in airports
            .GroupBy(airport => (Code: airport.Item3, Name: airport.Item4, Continent: airport.Item5))
            .Select(group => (
                group.Key.Code,
                group.Key.Name,
                group.Key.Continent,
                Latitude: group.Average(airport => airport.Item6),
                Longitude: group.Average(airport => airport.Item7))))
        {
            yield return new FlightLocation
            {
                Type = LocationType.Country,
                Code = country.Code,
                Name = country.Name,
                CountryCode = country.Code,
                CountryName = country.Name,
                Continent = country.Continent,
                Latitude = country.Latitude,
                Longitude = country.Longitude
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
