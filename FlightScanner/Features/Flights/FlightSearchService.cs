using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Xml;
using FlightScanner.Data;
using FlightScanner.Features.Integrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace FlightScanner.Features.Flights;

public interface IFlightSearchService
{
    Task<IReadOnlyList<FlightOffer>> SearchAsync(FlightSearchQuery query, CancellationToken cancellationToken = default);
    IReadOnlyList<AirlineSearchLink> GetAirlineSearchLinks(FlightSearchQuery query);
}

public sealed class FlightSearchService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    IHttpClientFactory httpClientFactory,
    IMemoryCache memoryCache,
    ILogger<FlightSearchService> logger) : IFlightSearchService
{
    private static readonly IReadOnlyList<AirlineDirectoryEntry> AirlineDirectory =
    [
        new("American Airlines", "Full service", "North America", "https://www.aa.com/booking/find-flights"),
        new("Delta Air Lines", "Full service", "North America", "https://www.delta.com/flight-search/book-a-flight"),
        new("United Airlines", "Full service", "North America", "https://www.united.com/en/us/fsr/choose-flights"),
        new("Southwest", "Low cost", "North America", "https://www.southwest.com/air/booking/"),
        new("JetBlue", "Low cost", "North America", "https://www.jetblue.com/booking/flights"),
        new("Air Canada", "Full service", "North America", "https://www.aircanada.com/ca/en/aco/home/book.html"),
        new("WestJet", "Low cost", "North America", "https://www.westjet.com/en-ca/flights"),
        new("British Airways", "Full service", "Europe", "https://www.britishairways.com/travel/book/public/en_us/flightList"),
        new("Lufthansa", "Full service", "Europe", "https://www.lufthansa.com/us/en/flight-search"),
        new("Air France", "Full service", "Europe", "https://wwws.airfrance.us/search/open-dates"),
        new("KLM", "Full service", "Europe", "https://www.klm.com/search/open-dates"),
        new("Iberia", "Full service", "Europe", "https://www.iberia.com/us/flight-search/"),
        new("TAP Air Portugal", "Full service", "Europe", "https://www.flytap.com/en-us/booking-information/book-flight"),
        new("Turkish Airlines", "Full service", "Europe", "https://www.turkishairlines.com/en-int/flights/booking/"),
        new("Ryanair", "Low cost", "Europe", "https://www.ryanair.com/gb/en"),
        new("easyJet", "Low cost", "Europe", "https://www.easyjet.com/en"),
        new("Wizz Air", "Low cost", "Europe", "https://wizzair.com/en-gb/flights"),
        new("Vueling", "Low cost", "Europe", "https://www.vueling.com/en/book-your-flight/new-search"),
        new("Transavia", "Low cost", "Europe", "https://www.transavia.com/en-EU/book-a-flight/flights/search/"),
        new("Royal Air Maroc", "Full service", "Africa", "https://www.royalairmaroc.com/us-en/book-flight"),
        new("Egyptair", "Full service", "Africa", "https://www.egyptair.com/en/Pages/Booking.aspx"),
        new("Ethiopian Airlines", "Full service", "Africa", "https://www.ethiopianairlines.com/aa/book"),
        new("Kenya Airways", "Full service", "Africa", "https://www.kenya-airways.com/en/book-and-manage/book-a-flight/"),
        new("Air Arabia", "Low cost", "Africa/Asia", "https://www.airarabia.com/en"),
        new("Emirates", "Full service", "Asia", "https://www.emirates.com/us/english/book/"),
        new("Qatar Airways", "Full service", "Asia", "https://www.qatarairways.com/en-us/search-results.html"),
        new("Etihad", "Full service", "Asia", "https://www.etihad.com/en-us/book"),
        new("Singapore Airlines", "Full service", "Asia", "https://www.singaporeair.com/en_UK/us/plan-travel/local-promotions/book-flights/"),
        new("Cathay Pacific", "Full service", "Asia", "https://www.cathaypacific.com/cx/en_US/book-a-trip/book-flights.html"),
        new("ANA", "Full service", "Asia", "https://www.ana.co.jp/en/us/"),
        new("Japan Airlines", "Full service", "Asia", "https://www.jal.co.jp/ar/en/"),
        new("AirAsia", "Low cost", "Asia", "https://www.airasia.com/flights/"),
        new("Scoot", "Low cost", "Asia", "https://www.flyscoot.com/en/booking"),
        new("IndiGo", "Low cost", "Asia", "https://www.goindigo.in/"),
        new("Qantas", "Full service", "Oceania", "https://www.qantas.com/us/en/book-a-trip/flights.html"),
        new("Air New Zealand", "Full service", "Oceania", "https://www.airnewzealand.com/flights"),
        new("Jetstar", "Low cost", "Oceania", "https://www.jetstar.com/us/en/home"),
        new("LATAM", "Full service", "South America", "https://www.latamairlines.com/us/en"),
        new("Avianca", "Full service", "South America", "https://www.avianca.com/en/"),
        new("GOL", "Low cost", "South America", "https://www.voegol.com.br/en-us"),
        new("Flybondi", "Low cost", "South America", "https://flybondi.com/ar")
    ];

    public async Task<IReadOnlyList<FlightOffer>> SearchAsync(FlightSearchQuery query, CancellationToken cancellationToken = default)
    {
        var customOffers = await TrySearchCustomProviderAsync(query, cancellationToken);
        if (customOffers.Count > 0)
        {
            return customOffers;
        }

        return GenerateDemoOffers(query).ToList();
    }

    public IReadOnlyList<AirlineSearchLink> GetAirlineSearchLinks(FlightSearchQuery query)
    {
        var routeRegions = new[] { query.Origin, query.Destination, query.OriginType.ToString(), query.DestinationType.ToString() };
        return AirlineDirectory
            .OrderByDescending(airline => routeRegions.Any(value => airline.Region.Contains(value, StringComparison.OrdinalIgnoreCase)))
            .ThenBy(airline => airline.Category)
            .ThenBy(airline => airline.Airline)
            .Select(airline => new AirlineSearchLink(
                airline.Airline,
                airline.Category,
                airline.Region,
                airline.Url,
                "Opens the airline booking page. Live prices stay on the airline site unless you configure a fare API."))
            .ToList();
    }

    private async Task<IReadOnlyList<FlightOffer>> TrySearchCustomProviderAsync(FlightSearchQuery query, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var setting = await db.IntegrationSettings.AsNoTracking()
            .FirstOrDefaultAsync(item => item.Kind == IntegrationKind.FlightProvider && item.Enabled, cancellationToken);

        if (setting is null || string.IsNullOrWhiteSpace(setting.SettingsJson))
        {
            return [];
        }

        var options = JsonSerializer.Deserialize<FlightProviderOptions>(setting.SettingsJson, JsonOptions()) ?? new();
        if (setting.Enabled && options.ProviderType.Equals("Amadeus", StringComparison.OrdinalIgnoreCase))
        {
            return await SearchAmadeusAsync(query, options, cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(options.EndpointUrl) || string.IsNullOrWhiteSpace(options.BodyTemplate))
        {
            return [];
        }

        try
        {
            var request = new HttpRequestMessage(new HttpMethod(options.HttpMethod), options.EndpointUrl)
            {
                Content = new StringContent(RenderTemplate(options.BodyTemplate, query), Encoding.UTF8, "application/json")
            };

            foreach (var header in ParseHeaders(options.HeadersJson))
            {
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            var client = httpClientFactory.CreateClient("flight-provider");
            using var response = await client.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));

            var priceText = ReadPath(document.RootElement, options.PriceJsonPath);
            if (!decimal.TryParse(priceText, out var price))
            {
                return [];
            }

            return
            [
                new FlightOffer(
                    "Custom API",
                    "Provider",
                    "API",
                    query.Origin,
                    query.Destination,
                    query.DepartFrom,
                    query.ReturnFrom,
                    price,
                    ReadPath(document.RootElement, options.CurrencyJsonPath) ?? query.Currency,
                    query.DirectOnly ? 0 : 1,
                    TimeSpan.FromHours(4),
                    ReadPath(document.RootElement, options.UrlJsonPath) ?? "#")
            ];
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Custom flight provider search failed.");
            return [];
        }
    }

    private async Task<IReadOnlyList<FlightOffer>> SearchAmadeusAsync(FlightSearchQuery query, FlightProviderOptions options, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.AmadeusClientId) || string.IsNullOrWhiteSpace(options.AmadeusClientSecret))
        {
            return [];
        }

        try
        {
            var baseUrl = GetAmadeusBaseUrl(options);
            var token = await GetAmadeusTokenAsync(baseUrl, options, cancellationToken);
            var routePairs = await ResolveRoutePairsAsync(query, cancellationToken);
            var offers = new List<FlightOffer>();

            foreach (var (origin, destination) in routePairs.Take(8))
            {
                var requestUrl = BuildAmadeusFlightOffersUrl(baseUrl, query, origin, destination, Math.Clamp(options.AmadeusMaxOffers, 1, 50));
                using var request = new HttpRequestMessage(HttpMethod.Get, requestUrl);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

                var client = httpClientFactory.CreateClient("amadeus");
                using var response = await client.SendAsync(request, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    logger.LogWarning("Amadeus search failed with status {StatusCode}.", response.StatusCode);
                    continue;
                }

                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
                offers.AddRange(ParseAmadeusOffers(document.RootElement, query, origin, destination));
            }

            return offers
                .OrderBy(offer => offer.Price)
                .Take(Math.Clamp(options.AmadeusMaxOffers, 1, 50))
                .ToList();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Amadeus flight search failed.");
            return [];
        }
    }

    private async Task<string> GetAmadeusTokenAsync(string baseUrl, FlightProviderOptions options, CancellationToken cancellationToken)
    {
        var cacheKey = $"amadeus-token:{baseUrl}:{options.AmadeusClientId}";
        if (memoryCache.TryGetValue(cacheKey, out string? token) && !string.IsNullOrWhiteSpace(token))
        {
            return token;
        }

        var client = httpClientFactory.CreateClient("amadeus");
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/v1/security/oauth2/token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = options.AmadeusClientId,
                ["client_secret"] = options.AmadeusClientSecret
            })
        };

        using var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        token = document.RootElement.GetProperty("access_token").GetString() ?? "";
        var expiresIn = document.RootElement.TryGetProperty("expires_in", out var expires) ? expires.GetInt32() : 1800;
        memoryCache.Set(cacheKey, token, TimeSpan.FromSeconds(Math.Max(60, expiresIn - 60)));
        return token;
    }

    private async Task<IReadOnlyList<(string Origin, string Destination)>> ResolveRoutePairsAsync(FlightSearchQuery query, CancellationToken cancellationToken)
    {
        var origins = await ResolveLocationCodesAsync(query.OriginType, query.Origin, cancellationToken);
        var destinations = await ResolveLocationCodesAsync(query.DestinationType, query.Destination, cancellationToken);

        return origins
            .SelectMany(origin => destinations.Select(destination => (Origin: origin, Destination: destination)))
            .Where(pair => !pair.Origin.Equals(pair.Destination, StringComparison.OrdinalIgnoreCase))
            .Take(12)
            .ToList();
    }

    private async Task<IReadOnlyList<string>> ResolveLocationCodesAsync(LocationType type, string value, CancellationToken cancellationToken)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 3 && trimmed.All(char.IsLetter))
        {
            return [trimmed.ToUpperInvariant()];
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var matches = await db.FlightLocations.AsNoTracking()
            .Where(location =>
                location.Type == type &&
                (location.Code.ToLower() == trimmed.ToLower() || location.Name.ToLower().Contains(trimmed.ToLower())))
            .ToListAsync(cancellationToken);

        if (type is LocationType.Airport or LocationType.City)
        {
            return matches
                .Select(location => location.Code)
                .Where(code => code.Length == 3)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(4)
                .ToList();
        }

        var scopeNames = matches.Select(match => match.Name).Append(trimmed).ToList();
        var airports = await db.FlightLocations.AsNoTracking()
            .Where(location => location.Type == LocationType.Airport)
            .ToListAsync(cancellationToken);

        return airports
            .Where(airport => type == LocationType.Continent
                ? scopeNames.Any(scope => airport.Continent.Equals(scope, StringComparison.OrdinalIgnoreCase) || airport.Code.Equals(scope, StringComparison.OrdinalIgnoreCase))
                : scopeNames.Any(scope => airport.CountryName == scope || airport.CountryCode == scope || airport.Code == scope))
            .Select(airport => airport.Code)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(4)
            .ToList();
    }

    private static string BuildAmadeusFlightOffersUrl(string baseUrl, FlightSearchQuery query, string origin, string destination, int maxOffers)
    {
        var parameters = new Dictionary<string, string?>
        {
            ["originLocationCode"] = origin,
            ["destinationLocationCode"] = destination,
            ["departureDate"] = query.DepartFrom.ToString("yyyy-MM-dd"),
            ["adults"] = query.Adults.ToString(),
            ["currencyCode"] = query.Currency,
            ["max"] = maxOffers.ToString()
        };

        if (query.ReturnFrom is { } returnDate)
        {
            parameters["returnDate"] = returnDate.ToString("yyyy-MM-dd");
        }

        if (query.Children > 0)
        {
            parameters["children"] = query.Children.ToString();
        }

        if (query.Infants > 0)
        {
            parameters["infants"] = query.Infants.ToString();
        }

        if (query.DirectOnly)
        {
            parameters["nonStop"] = "true";
        }

        parameters["travelClass"] = query.Cabin switch
        {
            CabinClass.PremiumEconomy => "PREMIUM_ECONOMY",
            CabinClass.Business => "BUSINESS",
            CabinClass.First => "FIRST",
            _ => "ECONOMY"
        };

        var queryString = string.Join("&", parameters
            .Where(parameter => !string.IsNullOrWhiteSpace(parameter.Value))
            .Select(parameter => $"{Uri.EscapeDataString(parameter.Key)}={Uri.EscapeDataString(parameter.Value!)}"));

        return $"{baseUrl}/v2/shopping/flight-offers?{queryString}";
    }

    private static IEnumerable<FlightOffer> ParseAmadeusOffers(JsonElement root, FlightSearchQuery query, string origin, string destination)
    {
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var offer in data.EnumerateArray())
        {
            var priceElement = offer.GetProperty("price");
            if (!decimal.TryParse(priceElement.GetProperty("grandTotal").GetString(), out var price))
            {
                continue;
            }

            var currency = priceElement.GetProperty("currency").GetString() ?? query.Currency;
            var itineraries = offer.GetProperty("itineraries").EnumerateArray().ToList();
            if (itineraries.Count == 0)
            {
                continue;
            }

            var firstItinerary = itineraries[0];
            var segments = firstItinerary.ValueKind == JsonValueKind.Object && firstItinerary.TryGetProperty("segments", out var segmentElement)
                ? segmentElement.EnumerateArray().ToList()
                : [];
            var firstSegment = segments.FirstOrDefault();
            var airline = firstSegment.ValueKind == JsonValueKind.Object && firstSegment.TryGetProperty("carrierCode", out var carrier) ? carrier.GetString() ?? "Airline" : "Airline";
            var flightNumber = firstSegment.ValueKind == JsonValueKind.Object && firstSegment.TryGetProperty("number", out var number) ? $"{airline}{number.GetString()}" : airline;
            var stops = Math.Max(0, segments.Count - 1);
            var duration = firstItinerary.TryGetProperty("duration", out var durationElement)
                ? ParseIsoDuration(durationElement.GetString())
                : TimeSpan.Zero;

            yield return new FlightOffer(
                "Amadeus",
                airline,
                flightNumber,
                origin,
                destination,
                query.DepartFrom,
                query.ReturnFrom,
                price,
                currency,
                stops,
                duration,
                "https://www.amadeus.com/en");
        }
    }

    private static TimeSpan ParseIsoDuration(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return TimeSpan.Zero;
        }

        try
        {
            return XmlConvert.ToTimeSpan(value);
        }
        catch
        {
            return TimeSpan.Zero;
        }
    }

    private static string GetAmadeusBaseUrl(FlightProviderOptions options)
    {
        return options.AmadeusEnvironment.Equals("Production", StringComparison.OrdinalIgnoreCase)
            ? "https://api.amadeus.com"
            : "https://test.api.amadeus.com";
    }

    private static IEnumerable<FlightOffer> GenerateDemoOffers(FlightSearchQuery query)
    {
        var seed = HashCode.Combine(query.Origin.ToUpperInvariant(), query.Destination.ToUpperInvariant(), query.DepartFrom, query.Adults, query.Cabin);
        var random = new Random(seed);
        var airlines = new[] { "Atlas", "Northline", "EuroJet", "Pacific Air", "Nomad" };

        for (var i = 0; i < 8; i++)
        {
            var stops = query.DirectOnly ? 0 : random.Next(0, Math.Min(query.MaxStops ?? 2, 2) + 1);
            var cabinMultiplier = query.Cabin switch
            {
                CabinClass.PremiumEconomy => 1.35m,
                CabinClass.Business => 2.4m,
                CabinClass.First => 3.8m,
                _ => 1m
            };
            var passengerMultiplier = query.Adults + (query.Children * 0.75m) + (query.Infants * 0.1m);
            var departDate = query.DepartFrom.AddDays(random.Next(Math.Max(1, query.DepartTo.DayNumber - query.DepartFrom.DayNumber + 1)));
            var basePrice = random.Next(110, 880) + stops * 45 + query.CheckedBags * 35;
            var price = decimal.Round(basePrice * cabinMultiplier * passengerMultiplier, 2);

            yield return new FlightOffer(
                "Demo fare engine",
                airlines[i % airlines.Length],
                $"{airlines[i % airlines.Length][0]}{random.Next(100, 999)}",
                query.Origin,
                query.Destination,
                departDate,
                query.ReturnFrom,
                price,
                query.Currency,
                stops,
                TimeSpan.FromMinutes(random.Next(120, 980)),
                "#");
        }
    }

    private static string RenderTemplate(string template, FlightSearchQuery query)
    {
        return template
            .Replace("{{origin}}", query.Origin, StringComparison.OrdinalIgnoreCase)
            .Replace("{{originType}}", query.OriginType.ToString(), StringComparison.OrdinalIgnoreCase)
            .Replace("{{destination}}", query.Destination, StringComparison.OrdinalIgnoreCase)
            .Replace("{{destinationType}}", query.DestinationType.ToString(), StringComparison.OrdinalIgnoreCase)
            .Replace("{{departFrom}}", query.DepartFrom.ToString("yyyy-MM-dd"), StringComparison.OrdinalIgnoreCase)
            .Replace("{{departTo}}", query.DepartTo.ToString("yyyy-MM-dd"), StringComparison.OrdinalIgnoreCase)
            .Replace("{{adults}}", query.Adults.ToString(), StringComparison.OrdinalIgnoreCase)
            .Replace("{{children}}", query.Children.ToString(), StringComparison.OrdinalIgnoreCase)
            .Replace("{{infants}}", query.Infants.ToString(), StringComparison.OrdinalIgnoreCase)
            .Replace("{{currency}}", query.Currency, StringComparison.OrdinalIgnoreCase);
    }

    private static Dictionary<string, string> ParseHeaders(string headersJson)
    {
        if (string.IsNullOrWhiteSpace(headersJson))
        {
            return [];
        }

        return JsonSerializer.Deserialize<Dictionary<string, string>>(headersJson, JsonOptions()) ?? [];
    }

    private static string? ReadPath(JsonElement root, string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var current = root;
        foreach (var segment in path.TrimStart('$', '.').Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var property = segment;
            var index = (int?)null;
            var bracket = segment.IndexOf('[', StringComparison.Ordinal);
            if (bracket >= 0)
            {
                property = segment[..bracket];
                var end = segment.IndexOf(']', bracket);
                if (end > bracket && int.TryParse(segment[(bracket + 1)..end], out var parsed))
                {
                    index = parsed;
                }
            }

            if (!string.IsNullOrEmpty(property) && !current.TryGetProperty(property, out current))
            {
                return null;
            }

            if (index is { } arrayIndex)
            {
                if (current.ValueKind != JsonValueKind.Array || current.GetArrayLength() <= arrayIndex)
                {
                    return null;
                }

                current = current[arrayIndex];
            }
        }

        return current.ValueKind == JsonValueKind.String ? current.GetString() : current.ToString();
    }

    private static JsonSerializerOptions JsonOptions() => new(JsonSerializerDefaults.Web);

    private sealed record AirlineDirectoryEntry(string Airline, string Category, string Region, string Url);
}
