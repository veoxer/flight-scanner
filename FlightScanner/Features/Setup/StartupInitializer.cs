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
    private const string GoogleFlightsProvider = "SerpApiGoogleFlights";
    private const string FreebaseIdentifierType = "FreebaseId";

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
            .Select(location => new { location.Type, location.Code, location.Name, location.CountryCode })
            .ToListAsync(cancellationToken);
        var existingLocationKeys = existingLocations
            .Select(location => $"{location.Type}:{location.Code}".ToUpperInvariant())
            .ToHashSet();
        var existingLocationSemanticKeys = existingLocations
            .Select(location => BuildLocationSemanticKey(location.Type, location.Name, location.CountryCode))
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missingLocations = SeedLocations()
            .Where(location => ShouldAddLocation(location, existingLocationKeys, existingLocationSemanticKeys))
            .ToList();
        db.FlightLocations.AddRange(missingLocations);

        var importedLocations = await TryImportWorldLocationsAsync(db, existingLocationKeys, existingLocationSemanticKeys, cancellationToken);
        db.FlightLocations.AddRange(importedLocations);
        await db.SaveChangesAsync(cancellationToken);

        await SeedLocationIdentifiersAsync(db, cancellationToken);
        await TryImportWikidataLocationIdentifiersAsync(db, cancellationToken);

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

            ALTER TABLE "PriceAlerts"
            ADD COLUMN IF NOT EXISTS "FlexibleDates" boolean NOT NULL DEFAULT false;

            ALTER TABLE "PriceAlerts"
            ADD COLUMN IF NOT EXISTS "FlexibleYear" integer;

            ALTER TABLE "PriceAlerts"
            ADD COLUMN IF NOT EXISTS "FlexibleMonth" integer;

            ALTER TABLE "PriceAlerts"
            ADD COLUMN IF NOT EXISTS "FlexibleDepartureDay" integer;

            ALTER TABLE "PriceAlerts"
            ADD COLUMN IF NOT EXISTS "FlexibleStayDays" integer;

            ALTER TABLE "PriceAlerts"
            ADD COLUMN IF NOT EXISTS "OutboundTimeFromHour" integer;

            ALTER TABLE "PriceAlerts"
            ADD COLUMN IF NOT EXISTS "OutboundTimeToHour" integer;

            ALTER TABLE "PriceAlerts"
            ADD COLUMN IF NOT EXISTS "ReturnTimeFromHour" integer;

            ALTER TABLE "PriceAlerts"
            ADD COLUMN IF NOT EXISTS "ReturnTimeToHour" integer;

            ALTER TABLE "AspNetUsers"
            ADD COLUMN IF NOT EXISTS "PreferredCulture" text NOT NULL DEFAULT 'en';

            CREATE TABLE IF NOT EXISTS "FlightLocationIdentifiers" (
                "Id" integer GENERATED BY DEFAULT AS IDENTITY,
                "LocationType" integer NOT NULL,
                "LocationCode" character varying(16) NOT NULL,
                "Provider" character varying(40) NOT NULL,
                "IdentifierType" character varying(32) NOT NULL,
                "Identifier" character varying(128) NOT NULL,
                "Source" character varying(80) NOT NULL,
                "UpdatedAt" timestamp with time zone NOT NULL,
                CONSTRAINT "PK_FlightLocationIdentifiers" PRIMARY KEY ("Id")
            );

            ALTER TABLE "FlightLocationIdentifiers"
            ADD COLUMN IF NOT EXISTS "IdentifierType" character varying(32) NOT NULL DEFAULT 'FreebaseId';

            UPDATE "FlightLocationIdentifiers"
            SET "LocationCode" = UPPER(TRIM("LocationCode")),
                "Provider" = CASE WHEN COALESCE(TRIM("Provider"), '') = '' THEN 'SerpApiGoogleFlights' ELSE TRIM("Provider") END,
                "IdentifierType" = CASE WHEN COALESCE(TRIM("IdentifierType"), '') = '' THEN 'FreebaseId' ELSE TRIM("IdentifierType") END,
                "Identifier" = TRIM("Identifier"),
                "Source" = CASE WHEN COALESCE(TRIM("Source"), '') = '' THEN 'Wikidata' ELSE TRIM("Source") END;

            UPDATE "FlightLocations"
            SET "Code" = UPPER(TRIM("Code")),
                "Name" = TRIM("Name"),
                "CountryCode" = NULLIF(UPPER(TRIM(COALESCE("CountryCode", ''))), ''),
                "CountryName" = NULLIF(TRIM(COALESCE("CountryName", '')), ''),
                "Continent" = TRIM("Continent");

            DELETE FROM "FlightLocationIdentifiers" duplicate
            USING "FlightLocationIdentifiers" keeper
            WHERE duplicate."Id" > keeper."Id"
                AND duplicate."LocationType" = keeper."LocationType"
                AND duplicate."LocationCode" = keeper."LocationCode"
                AND duplicate."Provider" = keeper."Provider"
                AND duplicate."IdentifierType" = keeper."IdentifierType";

            DELETE FROM "FlightLocations" duplicate
            USING "FlightLocations" keeper
            WHERE duplicate."Id" > keeper."Id"
                AND duplicate."Type" = keeper."Type"
                AND duplicate."Code" = keeper."Code";

            DELETE FROM "FlightLocations" duplicate
            USING "FlightLocations" keeper
            WHERE duplicate."Id" > keeper."Id"
                AND duplicate."Type" = keeper."Type"
                AND duplicate."Type" IN (1, 2, 3)
                AND UPPER(duplicate."Name") = UPPER(keeper."Name")
                AND COALESCE(UPPER(duplicate."CountryCode"), '') = COALESCE(UPPER(keeper."CountryCode"), '');

            DELETE FROM "AppSettings" duplicate
            USING "AppSettings" keeper
            WHERE duplicate."Id" > keeper."Id"
                AND duplicate."Key" = keeper."Key";

            DELETE FROM "IntegrationSettings" duplicate
            USING "IntegrationSettings" keeper
            WHERE duplicate."Id" > keeper."Id"
                AND duplicate."Kind" = keeper."Kind";

            DROP INDEX IF EXISTS "IX_FlightLocationIdentifiers_LocationType_LocationCode_Provider";

            CREATE UNIQUE INDEX IF NOT EXISTS "IX_FlightLocations_Type_Code"
            ON "FlightLocations" ("Type", "Code");

            CREATE UNIQUE INDEX IF NOT EXISTS "IX_FlightLocations_City_Name_Country"
            ON "FlightLocations" ("Type", UPPER("Name"), COALESCE(UPPER("CountryCode"), ''))
            WHERE "Type" = 1;

            CREATE UNIQUE INDEX IF NOT EXISTS "IX_FlightLocations_Region_Name"
            ON "FlightLocations" ("Type", UPPER("Name"))
            WHERE "Type" IN (2, 3);

            CREATE UNIQUE INDEX IF NOT EXISTS "IX_FlightLocationIdentifiers_LocationType_LocationCode_Provider_IdentifierType"
            ON "FlightLocationIdentifiers" ("LocationType", "LocationCode", "Provider", "IdentifierType");

            CREATE UNIQUE INDEX IF NOT EXISTS "IX_AppSettings_Key"
            ON "AppSettings" ("Key");

            CREATE UNIQUE INDEX IF NOT EXISTS "IX_IntegrationSettings_Kind"
            ON "IntegrationSettings" ("Kind");

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
        HashSet<string> existingLocationSemanticKeys,
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
                .Where(location => ShouldAddLocation(location, existingLocationKeys, existingLocationSemanticKeys))
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

    private async Task SeedLocationIdentifiersAsync(ApplicationDbContext db, CancellationToken cancellationToken)
    {
        var casablancaCodes = await db.FlightLocations.AsNoTracking()
            .Where(location => location.Type == LocationType.City && location.Name == "Casablanca")
            .Select(location => location.Code)
            .ToListAsync(cancellationToken);
        foreach (var code in casablancaCodes.Append("CAS").Distinct(StringComparer.OrdinalIgnoreCase))
        {
            await UpsertLocationIdentifierAsync(
                db,
                LocationType.City,
                code,
                "/m/022b_",
                "Seed",
                cancellationToken);
        }
    }

    private async Task TryImportWikidataLocationIdentifiersAsync(ApplicationDbContext db, CancellationToken cancellationToken)
    {
        var importEnabled = !bool.TryParse(configuration["LOCATION_IDENTIFIER_IMPORT_ENABLED"], out var enabled) || enabled;
        if (!importEnabled)
        {
            return;
        }

        await TryImportWikidataCountryIdentifiersAsync(db, cancellationToken);
        await TryImportWikidataContinentIdentifiersAsync(db, cancellationToken);
        await TryImportWikidataCityIdentifiersAsync(db, cancellationToken);
    }

    private async Task TryImportWikidataCountryIdentifiersAsync(ApplicationDbContext db, CancellationToken cancellationToken)
    {
        var importSetting = await db.AppSettings
            .FirstOrDefaultAsync(setting => setting.Key == "WikidataCountryIdentifiersImported", cancellationToken);
        var refresh = bool.TryParse(configuration["LOCATION_IDENTIFIER_IMPORT_REFRESH"], out var refreshEnabled) && refreshEnabled;
        if (!refresh && importSetting?.Value == "true")
        {
            return;
        }

        try
        {
            const string query = """
                SELECT ?iso ?freebase WHERE {
                  ?item wdt:P31 wd:Q6256;
                        wdt:P297 ?iso;
                        wdt:P646 ?freebase.
                }
                """;
            using var document = await ExecuteWikidataSparqlAsync(query, cancellationToken);
            var bindings = ReadSparqlBindings(document.RootElement);
            var countryCodes = await db.FlightLocations.AsNoTracking()
                .Where(location => location.Type == LocationType.Country)
                .Select(location => location.Code)
                .ToHashSetAsync(StringComparer.OrdinalIgnoreCase, cancellationToken);

            var imported = 0;
            foreach (var binding in bindings)
            {
                var iso = ReadSparqlValue(binding, "iso");
                var freebase = ReadSparqlValue(binding, "freebase");
                if (string.IsNullOrWhiteSpace(iso) || string.IsNullOrWhiteSpace(freebase))
                {
                    continue;
                }

                if (!countryCodes.Contains(iso))
                {
                    continue;
                }

                await UpsertLocationIdentifierAsync(
                    db,
                    LocationType.Country,
                    iso.ToUpperInvariant(),
                    freebase,
                    "Wikidata",
                    cancellationToken);
                imported++;
            }

            importSetting ??= new AppSetting { Key = "WikidataCountryIdentifiersImported" };
            importSetting.Value = "true";
            if (db.Entry(importSetting).State == EntityState.Detached)
            {
                db.AppSettings.Add(importSetting);
            }

            await db.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Imported {IdentifierCount} Wikidata Freebase country identifiers.", imported);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Wikidata Freebase country identifier import failed. Country searches will fall back where cached identifiers are missing.");
        }
    }

    private async Task TryImportWikidataContinentIdentifiersAsync(ApplicationDbContext db, CancellationToken cancellationToken)
    {
        var importSetting = await db.AppSettings
            .FirstOrDefaultAsync(setting => setting.Key == "WikidataContinentIdentifiersImported", cancellationToken);
        var refresh = bool.TryParse(configuration["LOCATION_IDENTIFIER_IMPORT_REFRESH"], out var refreshEnabled) && refreshEnabled;
        if (!refresh && importSetting?.Value == "true")
        {
            return;
        }

        try
        {
            const string query = """
                SELECT ?name ?freebase WHERE {
                  ?item wdt:P31 wd:Q5107;
                        wdt:P646 ?freebase;
                        rdfs:label ?name.
                  FILTER(LANG(?name) = "en")
                }
                """;
            using var document = await ExecuteWikidataSparqlAsync(query, cancellationToken);
            var localContinents = await db.FlightLocations.AsNoTracking()
                .Where(location => location.Type == LocationType.Continent)
                .Select(location => new { location.Code, location.Name })
                .ToDictionaryAsync(
                    location => location.Name,
                    location => location.Code,
                    StringComparer.OrdinalIgnoreCase,
                    cancellationToken);
            var imported = 0;

            foreach (var binding in ReadSparqlBindings(document.RootElement))
            {
                var name = ReadSparqlValue(binding, "name");
                var freebase = ReadSparqlValue(binding, "freebase");
                if (string.IsNullOrWhiteSpace(name) ||
                    string.IsNullOrWhiteSpace(freebase) ||
                    !localContinents.TryGetValue(name, out var code))
                {
                    continue;
                }

                await UpsertLocationIdentifierAsync(
                    db,
                    LocationType.Continent,
                    code,
                    freebase,
                    "Wikidata",
                    cancellationToken);
                imported++;
            }

            importSetting ??= new AppSetting { Key = "WikidataContinentIdentifiersImported" };
            importSetting.Value = "true";
            if (db.Entry(importSetting).State == EntityState.Detached)
            {
                db.AppSettings.Add(importSetting);
            }

            await db.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Imported {IdentifierCount} Wikidata Freebase continent identifiers.", imported);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Wikidata Freebase continent identifier import failed. Continent searches will return live fares only where cached identifiers already exist.");
        }
    }

    private async Task TryImportWikidataCityIdentifiersAsync(ApplicationDbContext db, CancellationToken cancellationToken)
    {
        var importSetting = await db.AppSettings
            .FirstOrDefaultAsync(setting => setting.Key == "WikidataCityIdentifiersImported", cancellationToken);
        var refresh = bool.TryParse(configuration["LOCATION_IDENTIFIER_IMPORT_REFRESH"], out var refreshEnabled) && refreshEnabled;
        if (!refresh && importSetting?.Value == "true")
        {
            return;
        }

        try
        {
            var localCities = await db.FlightLocations.AsNoTracking()
                .Where(location => location.Type == LocationType.City && location.CountryCode != null)
                .Select(location => new { location.Code, location.Name, location.CountryCode })
                .ToListAsync(cancellationToken);
            var localCityLookup = localCities
                .GroupBy(city => BuildCityIdentifierKey(city.Name, city.CountryCode!))
                .ToDictionary(
                    group => group.Key,
                    group => group.Select(city => city.Code).Distinct(StringComparer.OrdinalIgnoreCase).ToList());
            var usedCityCodes = localCities
                .Select(city => city.Code)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var countries = await db.FlightLocations.AsNoTracking()
                .Where(location => location.Type == LocationType.Country)
                .Select(location => new { location.Code, location.Name, location.Continent })
                .ToDictionaryAsync(
                    country => country.Code,
                    country => new CountryInfo(country.Name, country.Continent),
                    StringComparer.OrdinalIgnoreCase,
                    cancellationToken);

            var batchSize = int.TryParse(configuration["LOCATION_IDENTIFIER_IMPORT_CITY_BATCH_SIZE"], out var configuredBatchSize)
                ? Math.Clamp(configuredBatchSize, 100, 1000)
                : 100;
            var maxPages = int.TryParse(configuration["LOCATION_IDENTIFIER_IMPORT_MAX_CITY_PAGES"], out var configuredMaxPages)
                ? Math.Max(0, configuredMaxPages)
                : 400;
            var bestCandidates = new Dictionary<string, CityIdentifierCandidate>(StringComparer.OrdinalIgnoreCase);
            var importedBindings = 0;

            for (var page = 0; maxPages == 0 || page < maxPages; page++)
            {
                var offset = page * batchSize;
                //var query = $$"""
                //    SELECT DISTINCT ?name ?iso ?freebase ?population WHERE {
                //      ?item wdt:P646 ?freebase;
                //            wdt:P17 ?country;
                //            rdfs:label ?name.
                //      ?country wdt:P297 ?iso.
                //      VALUES ?class { wd:Q515 wd:Q3957 wd:Q486972 }
                //      ?item wdt:P31/wdt:P279* ?class.
                //      FILTER(LANG(?name) = "en")
                //      OPTIONAL { ?item wdt:P1082 ?population. }
                //    }
                //    LIMIT {{batchSize}}
                //    OFFSET {{offset}}
                //    """;

                var query = $$"""
                    SELECT DISTINCT ?name ?iso ?freebase ?population WHERE {
                      ?item wdt:P646 ?freebase;
                            wdt:P17 ?country;
                            rdfs:label ?name.
                      ?country wdt:P297 ?iso.
                      VALUES ?class { wd:Q515 wd:Q3957 wd:Q486972 }
                      ?item wdt:P31/wdt:P279* ?class.
                      FILTER(LANG(?name) = "en")
                      OPTIONAL { ?item wdt:P1082 ?population. }
                    }
                    LIMIT {{batchSize}}
                    OFFSET {{offset}}
                    """;

                using var document = await ExecuteWikidataSparqlAsync(query, cancellationToken);
                var bindings = ReadSparqlBindings(document.RootElement).ToList();
                if (bindings.Count == 0)
                {
                    break;
                }

                foreach (var binding in bindings)
                {
                    var name = ReadSparqlValue(binding, "name");
                    var iso = ReadSparqlValue(binding, "iso");
                    var freebase = ReadSparqlValue(binding, "freebase");
                    if (string.IsNullOrWhiteSpace(name) ||
                        string.IsNullOrWhiteSpace(iso) ||
                        string.IsNullOrWhiteSpace(freebase))
                    {
                        continue;
                    }

                    var key = BuildCityIdentifierKey(name, iso);
                    var population = ReadSparqlLong(binding, "population");
                    if (!bestCandidates.TryGetValue(key, out var existing) || population > existing.Population)
                    {
                        bestCandidates[key] = new CityIdentifierCandidate(name, iso.ToUpperInvariant(), freebase, population);
                    }

                    importedBindings++;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(150), cancellationToken);
            }

            var imported = 0;
            foreach (var (key, candidate) in bestCandidates)
            {
                if (!localCityLookup.TryGetValue(key, out var cityCodes))
                {
                    if (!countries.TryGetValue(candidate.CountryCode, out var country))
                    {
                        continue;
                    }

                    var cityCode = BuildUniqueCityCode(candidate.CountryCode, candidate.Name, candidate.FreebaseId, usedCityCodes);
                    db.FlightLocations.Add(new FlightLocation
                    {
                        Type = LocationType.City,
                        Code = cityCode,
                        Name = Truncate(candidate.Name, 120),
                        CountryCode = Truncate(candidate.CountryCode, 2),
                        CountryName = Truncate(country.Name, 80),
                        Continent = Truncate(country.Continent, 32)
                    });
                    cityCodes = [cityCode];
                    localCityLookup[key] = cityCodes;
                }

                foreach (var cityCode in localCityLookup[key])
                {
                    await UpsertLocationIdentifierAsync(
                        db,
                        LocationType.City,
                        cityCode,
                        candidate.FreebaseId,
                        "Wikidata",
                        cancellationToken);
                    imported++;
                }
            }

            importSetting ??= new AppSetting { Key = "WikidataCityIdentifiersImported" };
            importSetting.Value = "true";
            if (db.Entry(importSetting).State == EntityState.Detached)
            {
                db.AppSettings.Add(importSetting);
            }

            await db.SaveChangesAsync(cancellationToken);
            logger.LogInformation(
                "Imported {IdentifierCount} Wikidata Freebase city identifiers from {BindingCount} bindings.",
                imported,
                importedBindings);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Wikidata Freebase city identifier import failed. City searches will return live fares only where cached identifiers already exist.");
        }
    }

    private async Task<JsonDocument> ExecuteWikidataSparqlAsync(string query, CancellationToken cancellationToken)
    {
        var requestUrl = $"https://query.wikidata.org/sparql?format=json&query={Uri.EscapeDataString(query)}";
        var client = httpClientFactory.CreateClient("location-data");
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUrl);
        request.Headers.UserAgent.ParseAdd("FlightScanner/1.0 (self-hosted app)");
        using var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
    }

    private static IEnumerable<JsonElement> ReadSparqlBindings(JsonElement root)
    {
        if (!root.TryGetProperty("results", out var results) ||
            !results.TryGetProperty("bindings", out var bindings) ||
            bindings.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var binding in bindings.EnumerateArray())
        {
            yield return binding;
        }
    }

    private static string? ReadSparqlValue(JsonElement binding, string propertyName)
    {
        return binding.ValueKind == JsonValueKind.Object &&
            binding.TryGetProperty(propertyName, out var property) &&
            property.ValueKind == JsonValueKind.Object &&
            property.TryGetProperty("value", out var value) &&
            value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
    }

    private static long ReadSparqlLong(JsonElement binding, string propertyName)
    {
        return ReadSparqlValue(binding, propertyName) is { } value &&
            long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : 0;
    }

    private static string BuildCityIdentifierKey(string name, string countryCode)
    {
        var normalizedName = name
            .Normalize(NormalizationForm.FormD)
            .Where(character => CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
            .Select(character => char.IsLetterOrDigit(character) ? char.ToUpperInvariant(character) : ' ')
            .ToArray();
        var compactName = string.Join(' ', new string(normalizedName).Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return $"{countryCode.Trim().ToUpperInvariant()}:{compactName}";
    }

    private static bool ShouldAddLocation(
        FlightLocation location,
        HashSet<string> existingLocationKeys,
        HashSet<string> existingLocationSemanticKeys)
    {
        var codeKey = $"{location.Type}:{location.Code}".ToUpperInvariant();
        if (!existingLocationKeys.Add(codeKey))
        {
            return false;
        }

        var semanticKey = BuildLocationSemanticKey(location.Type, location.Name, location.CountryCode);
        if (string.IsNullOrWhiteSpace(semanticKey))
        {
            return true;
        }

        if (!existingLocationSemanticKeys.Add(semanticKey))
        {
            existingLocationKeys.Remove(codeKey);
            return false;
        }

        return true;
    }

    private static string BuildLocationSemanticKey(LocationType type, string name, string? countryCode)
    {
        if (type is LocationType.Airport || string.IsNullOrWhiteSpace(name))
        {
            return "";
        }

        var countryPart = type is LocationType.City
            ? countryCode?.Trim().ToUpperInvariant() ?? ""
            : "";
        return $"{type}:{countryPart}:{NormalizeLocationName(name)}";
    }

    private static string NormalizeLocationName(string name)
    {
        var normalized = name
            .Normalize(NormalizationForm.FormD)
            .Where(character => CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
            .Select(character => char.IsLetterOrDigit(character) ? char.ToUpperInvariant(character) : ' ')
            .ToArray();

        return string.Join(' ', new string(normalized).Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static async Task UpsertLocationIdentifierAsync(
        ApplicationDbContext db,
        LocationType locationType,
        string locationCode,
        string identifier,
        string source,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(locationCode) || string.IsNullOrWhiteSpace(identifier))
        {
            return;
        }

        var normalizedCode = locationCode.Trim().ToUpperInvariant();
        var existing = db.FlightLocationIdentifiers.Local.FirstOrDefault(item =>
            item.LocationType == locationType &&
            item.LocationCode.Equals(normalizedCode, StringComparison.OrdinalIgnoreCase) &&
            item.Provider.Equals(GoogleFlightsProvider, StringComparison.OrdinalIgnoreCase) &&
            item.IdentifierType.Equals(FreebaseIdentifierType, StringComparison.OrdinalIgnoreCase));

        existing ??= await db.FlightLocationIdentifiers.FirstOrDefaultAsync(item =>
            item.LocationType == locationType &&
            item.LocationCode == normalizedCode &&
            item.Provider == GoogleFlightsProvider,
            cancellationToken);
        if (existing is null)
        {
            db.FlightLocationIdentifiers.Add(new FlightLocationIdentifier
            {
                LocationType = locationType,
                LocationCode = normalizedCode,
                Provider = GoogleFlightsProvider,
                IdentifierType = FreebaseIdentifierType,
                Identifier = identifier.Trim(),
                Source = source,
                UpdatedAt = DateTimeOffset.UtcNow
            });
            return;
        }

        existing.Identifier = identifier.Trim();
        existing.Provider = GoogleFlightsProvider;
        existing.IdentifierType = FreebaseIdentifierType;
        existing.Source = source;
        existing.UpdatedAt = DateTimeOffset.UtcNow;
    }

    private static string BuildUniqueCityCode(
        string countryCode,
        string cityName,
        string freebaseId,
        HashSet<string> usedCodes)
    {
        var baseCode = BuildCityCode(countryCode, cityName);
        if (usedCodes.Add(baseCode))
        {
            return baseCode;
        }

        var suffix = new string(freebaseId.Where(char.IsLetterOrDigit).TakeLast(4).ToArray()).ToUpperInvariant();
        suffix = string.IsNullOrWhiteSpace(suffix) ? "CITY" : suffix;
        for (var counter = 1; counter < 1000; counter++)
        {
            var counterText = counter == 1 ? suffix : $"{suffix}{counter}";
            var prefixLength = Math.Max(1, 15 - counterText.Length);
            var prefix = baseCode.Length <= prefixLength ? baseCode : baseCode[..prefixLength].TrimEnd('-');
            var candidate = Truncate($"{prefix}-{counterText}", 16);
            if (usedCodes.Add(candidate))
            {
                return candidate;
            }
        }

        return Truncate($"{countryCode.ToUpperInvariant()}-{Guid.NewGuid():N}", 16);
    }

    private readonly record struct CityIdentifierCandidate(string Name, string CountryCode, string FreebaseId, long Population);

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
                ProviderType = configuration["FLIGHT_PROVIDER_TYPE"] ?? "SerpApi",
                SerpApiApiKey = configuration["SERPAPI_API_KEY"] ?? "",
                SerpApiApiKeys = configuration["SERPAPI_API_KEYS"] ?? configuration["SERPAPI_API_KEY"] ?? "",
                EndpointUrl = configuration["FLIGHT_PROVIDER_URL"] ?? "",
                HttpMethod = configuration["FLIGHT_PROVIDER_HTTP_METHOD"] ?? "POST",
                HeadersJson = configuration["FLIGHT_PROVIDER_HEADERS_JSON"] ?? "{}",
                BodyTemplate = configuration["FLIGHT_PROVIDER_BODY_TEMPLATE"] ?? "",
                PriceJsonPath = configuration["FLIGHT_PROVIDER_PRICE_JSON_PATH"] ?? "$.offers[0].price",
                CurrencyJsonPath = configuration["FLIGHT_PROVIDER_CURRENCY_JSON_PATH"] ?? "$.offers[0].currency",
                UrlJsonPath = configuration["FLIGHT_PROVIDER_URL_JSON_PATH"] ?? "$.offers[0].url",
                AlertScanIntervalMinutes = AlertScanOptions.DefaultIntervalMinutes
            }),
            IntegrationKind.Email => Serialize(EmailOptionsResolver.FromConfiguration(configuration)),
            IntegrationKind.WhatsApp => Serialize(new WhatsAppOptions
            {
                EndpointUrl = configuration["WHATSAPP_API_URL"] ?? "",
                HttpMethod = configuration["WHATSAPP_HTTP_METHOD"] ?? "POST",
                HeadersJson = configuration["WHATSAPP_HEADERS_JSON"] ?? "{}",
                To = configuration["WHATSAPP_TO"] ?? "",
                BodyTemplate = configuration["WHATSAPP_BODY_TEMPLATE"] ?? "{\"to\":\"{{to}}\",\"message\":\"{{message}}\"}"
            }),
            IntegrationKind.WebPush => Serialize(new WebPushOptions
            {
                PublicKey = configuration["VAPID_PUBLIC_KEY"] ?? "",
                PrivateKey = configuration["VAPID_PRIVATE_KEY"] ?? "",
                Subject = configuration["VAPID_SUBJECT"] ?? "mailto:admin@example.com"
            }),
            IntegrationKind.AlertPolicy => Serialize(new AlertPolicyOptions()),
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
            ("AU", "Australian continent", "Australian continent"),
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
        ("AU", "Australian continent"),
        ("AS", "Asia"),
        ("EU", "Europe"),
        ("NA", "North America"),
        ("OC", "Oceania"),
        ("SA", "South America")
    ];

    private readonly record struct CountryInfo(string Name, string Continent);
}
