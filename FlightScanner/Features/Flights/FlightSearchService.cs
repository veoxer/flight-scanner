using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FlightScanner.Data;
using FlightScanner.Features.Integrations;
using Microsoft.EntityFrameworkCore;

namespace FlightScanner.Features.Flights;

public interface IFlightSearchService
{
    Task<IReadOnlyList<FlightOffer>> SearchAsync(FlightSearchQuery query, CancellationToken cancellationToken = default);
    Task<FlightOffer?> GetReturnFlightAsync(string departureToken, string currency, CancellationToken cancellationToken = default);
    IReadOnlyList<AirlineSearchLink> GetAirlineSearchLinks(FlightSearchQuery query);
}

public sealed class FlightSearchService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    IHttpClientFactory httpClientFactory,
    ILogger<FlightSearchService> logger) : IFlightSearchService
{
    private const int SerpApiMaxOffers = 20;
    private const string SerpApiGoogleCountry = "ma";
    private const string SerpApiLanguage = "en";
    private const string GoogleFlightsProvider = "SerpApiGoogleFlights";
    private const string FreebaseIdentifierType = "FreebaseId";

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
        return await TrySearchConfiguredProviderAsync(query, cancellationToken);
    }

    public async Task<FlightOffer?> GetReturnFlightAsync(string departureToken, string currency, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(departureToken))
        {
            return null;
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var setting = await db.IntegrationSettings.AsNoTracking()
            .FirstOrDefaultAsync(item => item.Kind == IntegrationKind.FlightProvider && item.Enabled, cancellationToken);
        if (setting is null || string.IsNullOrWhiteSpace(setting.SettingsJson))
        {
            return null;
        }

        var options = JsonSerializer.Deserialize<FlightProviderOptions>(setting.SettingsJson, JsonOptions()) ?? new();
        if (string.IsNullOrWhiteSpace(options.SerpApiApiKey))
        {
            return null;
        }

        try
        {
            var requestUrl = BuildSerpApiReturnUrl(options, departureToken, currency);
            using var request = new HttpRequestMessage(HttpMethod.Get, requestUrl);
            var client = httpClientFactory.CreateClient("serpapi");
            using var response = await client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("SerpApi Google Flights return details failed with status {StatusCode}.", response.StatusCode);
                return null;
            }

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            if (document.RootElement.TryGetProperty("error", out var error))
            {
                logger.LogWarning("SerpApi Google Flights return details returned an error: {Error}", error.GetString());
                return null;
            }

            return ParseSerpApiOffers(document.RootElement, null, null, null, "Return")
                .OrderBy(offer => offer.Price)
                .FirstOrDefault();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "SerpApi Google Flights return details failed.");
            return null;
        }
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

    private async Task<IReadOnlyList<FlightOffer>> TrySearchConfiguredProviderAsync(FlightSearchQuery query, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var setting = await db.IntegrationSettings.AsNoTracking()
            .FirstOrDefaultAsync(item => item.Kind == IntegrationKind.FlightProvider && item.Enabled, cancellationToken);

        if (setting is null || string.IsNullOrWhiteSpace(setting.SettingsJson))
        {
            return [];
        }

        var options = JsonSerializer.Deserialize<FlightProviderOptions>(setting.SettingsJson, JsonOptions()) ?? new();
        if (setting.Enabled && (options.ProviderType.Equals("SerpApi", StringComparison.OrdinalIgnoreCase) ||
            options.ProviderType.Equals("Amadeus", StringComparison.OrdinalIgnoreCase)))
        {
            return await SearchSerpApiAsync(query, options, cancellationToken);
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

    private async Task<IReadOnlyList<FlightOffer>> SearchSerpApiAsync(FlightSearchQuery query, FlightProviderOptions options, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.SerpApiApiKey))
        {
            return [];
        }

        try
        {
            var origin = await ResolveSerpApiLocationIdAsync(query.OriginType, query.Origin, cancellationToken);
            var destination = await ResolveSerpApiLocationIdAsync(query.DestinationType, query.Destination, cancellationToken);
            if (string.IsNullOrWhiteSpace(origin) || string.IsNullOrWhiteSpace(destination) ||
                origin.Equals(destination, StringComparison.OrdinalIgnoreCase))
            {
                return [];
            }

            var requestUrl = BuildSerpApiUrl(query, options, origin, destination);
            using var request = new HttpRequestMessage(HttpMethod.Get, requestUrl);

            var client = httpClientFactory.CreateClient("serpapi");
            using var response = await client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("SerpApi Google Flights search failed with status {StatusCode}.", response.StatusCode);
                return [];
            }

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            if (document.RootElement.TryGetProperty("error", out var error))
            {
                logger.LogWarning("SerpApi Google Flights returned an error: {Error}", error.GetString());
                return [];
            }

            return ParseSerpApiOffers(document.RootElement, query, origin, destination)
                .OrderBy(offer => offer.Price)
                .Take(SerpApiMaxOffers)
                .ToList();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "SerpApi Google Flights search failed.");
            return [];
        }
    }

    private async Task<string?> ResolveSerpApiLocationIdAsync(LocationType type, string value, CancellationToken cancellationToken)
    {
        var trimmed = value.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return null;
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var matches = await db.FlightLocations.AsNoTracking()
            .Where(location =>
                location.Type == type &&
                (location.Code.ToLower() == trimmed.ToLower() || location.Name.ToLower().Contains(trimmed.ToLower())))
            .ToListAsync(cancellationToken);

        if (type is LocationType.Airport)
        {
            return matches
                .Select(location => location.Code)
                .Where(code => code.Length == 3)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault() ?? (trimmed.Length == 3 && trimmed.All(char.IsLetter)
                    ? trimmed.ToUpperInvariant()
                    : null);
        }

        return await ResolveCachedLocationIdentifierAsync(db, type, trimmed, matches, cancellationToken);
    }

    private static async Task<string?> ResolveCachedLocationIdentifierAsync(
        ApplicationDbContext db,
        LocationType type,
        string value,
        IReadOnlyCollection<FlightLocation> matches,
        CancellationToken cancellationToken)
    {
        if (type is LocationType.Airport)
        {
            return null;
        }

        var locationCodes = matches
            .Select(match => match.Code)
            .Append(value)
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Select(code => code.Trim().ToUpperInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (locationCodes.Count == 0)
        {
            return null;
        }

        return await db.FlightLocationIdentifiers.AsNoTracking()
            .Where(identifier =>
                identifier.LocationType == type &&
                identifier.Provider == GoogleFlightsProvider &&
                identifier.IdentifierType == FreebaseIdentifierType &&
                locationCodes.Contains(identifier.LocationCode))
            .OrderBy(identifier => identifier.Source == "Seed" ? 0 : 1)
            .ThenBy(identifier => identifier.LocationCode)
            .Select(identifier => identifier.Identifier)
            .Distinct()
            .FirstOrDefaultAsync(cancellationToken);
    }

    private static string BuildSerpApiUrl(FlightSearchQuery query, FlightProviderOptions options, string origin, string destination)
    {
        var parameters = new Dictionary<string, string?>
        {
            ["engine"] = "google_flights",
            ["api_key"] = options.SerpApiApiKey,
            ["departure_id"] = origin,
            ["arrival_id"] = destination,
            ["outbound_date"] = query.DepartFrom.ToString("yyyy-MM-dd"),
            ["adults"] = query.Adults.ToString(),
            ["currency"] = query.Currency,
            ["hl"] = SerpApiLanguage,
            ["gl"] = SerpApiGoogleCountry,
            ["travel_class"] = SerpApiTravelClass(query.Cabin),
            ["sort_by"] = "2",
            ["output"] = "json"
        };

        if (query.ReturnFrom is { } returnDate)
        {
            parameters["type"] = "1";
            parameters["return_date"] = returnDate.ToString("yyyy-MM-dd");
        }
        else
        {
            parameters["type"] = "2";
        }

        if (query.Children > 0)
        {
            parameters["children"] = query.Children.ToString();
        }

        if (query.Infants > 0)
        {
            parameters["infants_on_lap"] = query.Infants.ToString();
        }

        var stops = SerpApiStops(query);
        if (stops > 0)
        {
            parameters["stops"] = stops.ToString();
        }

        var queryString = string.Join("&", parameters
            .Where(parameter => !string.IsNullOrWhiteSpace(parameter.Value))
            .Select(parameter => $"{Uri.EscapeDataString(parameter.Key)}={Uri.EscapeDataString(parameter.Value!)}"));

        return $"https://serpapi.com/search?{queryString}";
    }

    private static string BuildSerpApiReturnUrl(FlightProviderOptions options, string departureToken, string currency)
    {
        var parameters = new Dictionary<string, string?>
        {
            ["engine"] = "google_flights",
            ["api_key"] = options.SerpApiApiKey,
            ["departure_token"] = departureToken,
            ["currency"] = string.IsNullOrWhiteSpace(currency) ? "MAD" : currency.Trim().ToUpperInvariant(),
            ["hl"] = SerpApiLanguage,
            ["gl"] = SerpApiGoogleCountry,
            ["output"] = "json"
        };

        var queryString = string.Join("&", parameters
            .Where(parameter => !string.IsNullOrWhiteSpace(parameter.Value))
            .Select(parameter => $"{Uri.EscapeDataString(parameter.Key)}={Uri.EscapeDataString(parameter.Value!)}"));

        return $"https://serpapi.com/search?{queryString}";
    }

    private static IEnumerable<FlightOffer> ParseSerpApiOffers(
        JsonElement root,
        FlightSearchQuery? query,
        string? origin,
        string? destination,
        string legKind = "Departure")
    {
        foreach (var offer in ReadSerpApiOfferArray(root, "best_flights").Concat(ReadSerpApiOfferArray(root, "other_flights")))
        {
            if (!TryGetDecimal(offer, "price", out var price))
            {
                continue;
            }

            var flights = offer.TryGetProperty("flights", out var flightsElement) && flightsElement.ValueKind == JsonValueKind.Array
                ? flightsElement.EnumerateArray().ToList()
                : [];
            if (flights.Count == 0)
            {
                continue;
            }

            var firstFlight = flights[0];
            var lastFlight = flights[^1];
            var airline = ReadString(firstFlight, "airline") ?? "Airline";
            var flightNumber = ReadString(firstFlight, "flight_number") ?? airline;
            var stops = Math.Max(0, flights.Count - 1);
            var duration = TryGetInt(offer, "total_duration", out var totalDuration)
                ? TimeSpan.FromMinutes(totalDuration)
                : TimeSpan.FromMinutes(flights.Sum(flight => TryGetInt(flight, "duration", out var durationMinutes) ? durationMinutes : 0));
            var departDate = ReadAirportDate(firstFlight, "departure_airport") ?? query?.DepartFrom ?? DateOnly.FromDateTime(DateTime.Today);
            var offerOrigin = ReadAirportId(firstFlight, "departure_airport") ?? origin ?? "";
            var offerDestination = ReadAirportId(lastFlight, "arrival_airport") ?? destination ?? "";
            var bookingUrl = root.TryGetProperty("search_metadata", out var metadata)
                ? ReadString(metadata, "google_flights_url") ?? "https://www.google.com/travel/flights"
                : "https://www.google.com/travel/flights";
            var itineraryLegs = new[]
            {
                ParseSerpApiItineraryLeg(legKind, flights, offer)
            };
            var extensions = ReadStringArray(offer, "extensions");
            var carbonEmissions = ReadCarbonEmissions(offer);
            var responseCurrency = root.TryGetProperty("search_parameters", out var searchParameters)
                ? ReadString(searchParameters, "currency")
                : null;

            yield return new FlightOffer(
                "SerpApi Google Flights",
                airline,
                flightNumber,
                offerOrigin,
                offerDestination,
                departDate,
                query?.ReturnFrom,
                price,
                query?.Currency ?? responseCurrency ?? "MAD",
                stops,
                duration,
                bookingUrl,
                itineraryLegs,
                extensions,
                carbonEmissions.ThisFlight,
                carbonEmissions.Typical,
                carbonEmissions.DifferencePercent,
                ReadString(offer, "airline_logo") ?? ReadString(firstFlight, "airline_logo"),
                ReadString(offer, "departure_token"),
                ReadString(offer, "booking_token"),
                ReadString(offer, "type") ?? (query?.ReturnFrom is null ? "One way" : "Round trip"));
        }
    }

    private static FlightItineraryLeg ParseSerpApiItineraryLeg(string kind, IReadOnlyList<JsonElement> flights, JsonElement offer)
    {
        var segments = flights.Select(ParseSerpApiSegment).ToList();
        var duration = TryGetInt(offer, "total_duration", out var totalDuration)
            ? TimeSpan.FromMinutes(totalDuration)
            : TimeSpan.FromMinutes(flights.Sum(flight => TryGetInt(flight, "duration", out var durationMinutes) ? durationMinutes : 0));
        var date = segments.FirstOrDefault()?.DepartureAirport.Time is { } departureTime
            ? (DateOnly?)DateOnly.FromDateTime(departureTime)
            : null;

        return new FlightItineraryLeg(kind, date, duration, segments, ParseSerpApiLayovers(offer));
    }

    private static FlightSegment ParseSerpApiSegment(JsonElement flight)
    {
        return new FlightSegment(
            ReadAirport(flight, "departure_airport"),
            ReadAirport(flight, "arrival_airport"),
            TryGetInt(flight, "duration", out var duration) ? TimeSpan.FromMinutes(duration) : TimeSpan.Zero,
            ReadString(flight, "airline") ?? "Airline",
            ReadString(flight, "airline_logo"),
            ReadString(flight, "flight_number") ?? "",
            ReadString(flight, "airplane"),
            ReadString(flight, "travel_class"),
            ReadString(flight, "legroom"),
            ReadStringArray(flight, "extensions"));
    }

    private static IReadOnlyList<FlightLayover> ParseSerpApiLayovers(JsonElement offer)
    {
        if (!offer.TryGetProperty("layovers", out var layovers) || layovers.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return layovers.EnumerateArray()
            .Select(layover => new FlightLayover(
                ReadString(layover, "name") ?? "Layover",
                ReadString(layover, "id") ?? "",
                TryGetInt(layover, "duration", out var duration) ? TimeSpan.FromMinutes(duration) : TimeSpan.Zero,
                layover.TryGetProperty("overnight", out var overnight) && overnight.ValueKind == JsonValueKind.True))
            .ToList();
    }

    private static FlightAirport ReadAirport(JsonElement flight, string propertyName)
    {
        if (flight.ValueKind != JsonValueKind.Object ||
            !flight.TryGetProperty(propertyName, out var airport) ||
            airport.ValueKind != JsonValueKind.Object)
        {
            return new FlightAirport("Airport", "", null);
        }

        return new FlightAirport(
            ReadString(airport, "name") ?? "Airport",
            ReadString(airport, "id") ?? "",
            DateTime.TryParse(ReadString(airport, "time"), out var time) ? time : null);
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(propertyName, out var values) ||
            values.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return values.EnumerateArray()
            .Where(value => value.ValueKind == JsonValueKind.String)
            .Select(value => value.GetString())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .ToList();
    }

    private static (int? ThisFlight, int? Typical, int? DifferencePercent) ReadCarbonEmissions(JsonElement offer)
    {
        if (offer.ValueKind != JsonValueKind.Object ||
            !offer.TryGetProperty("carbon_emissions", out var emissions) ||
            emissions.ValueKind != JsonValueKind.Object)
        {
            return (null, null, null);
        }

        return (
            TryGetInt(emissions, "this_flight", out var thisFlight) ? thisFlight : null,
            TryGetInt(emissions, "typical_for_this_route", out var typical) ? typical : null,
            TryGetInt(emissions, "difference_percent", out var differencePercent) ? differencePercent : null);
    }

    private static IEnumerable<JsonElement> ReadSerpApiOfferArray(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var offers) || offers.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var offer in offers.EnumerateArray())
        {
            yield return offer;
        }
    }

    private static int SerpApiStops(FlightSearchQuery query)
    {
        if (query.DirectOnly || query.MaxStops == 0)
        {
            return 1;
        }

        return query.MaxStops switch
        {
            1 => 2,
            2 => 3,
            _ => 0
        };
    }

    private static string SerpApiTravelClass(CabinClass cabin)
    {
        return cabin switch
        {
            CabinClass.PremiumEconomy => "2",
            CabinClass.Business => "3",
            CabinClass.First => "4",
            _ => "1"
        };
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        return element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(propertyName, out var property) &&
            property.ValueKind == JsonValueKind.String
                ? property.GetString()
                : null;
    }

    private static string? ReadAirportId(JsonElement flight, string propertyName)
    {
        return flight.ValueKind == JsonValueKind.Object &&
            flight.TryGetProperty(propertyName, out var airport) &&
            airport.ValueKind == JsonValueKind.Object
                ? ReadString(airport, "id")
                : null;
    }

    private static DateOnly? ReadAirportDate(JsonElement flight, string propertyName)
    {
        if (flight.ValueKind != JsonValueKind.Object ||
            !flight.TryGetProperty(propertyName, out var airport) ||
            airport.ValueKind != JsonValueKind.Object ||
            ReadString(airport, "time") is not { } timeText)
        {
            return null;
        }

        return DateTime.TryParse(timeText, out var parsed)
            ? DateOnly.FromDateTime(parsed)
            : null;
    }

    private static bool TryGetDecimal(JsonElement element, string propertyName, out decimal value)
    {
        value = 0;
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(propertyName, out var property))
        {
            return false;
        }

        return property.ValueKind switch
        {
            JsonValueKind.Number => property.TryGetDecimal(out value),
            JsonValueKind.String => decimal.TryParse(property.GetString(), out value),
            _ => false
        };
    }

    private static bool TryGetInt(JsonElement element, string propertyName, out int value)
    {
        value = 0;
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(propertyName, out var property))
        {
            return false;
        }

        return property.ValueKind switch
        {
            JsonValueKind.Number => property.TryGetInt32(out value),
            JsonValueKind.String => int.TryParse(property.GetString(), out value),
            _ => false
        };
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
