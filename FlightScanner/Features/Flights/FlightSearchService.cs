using System.Globalization;
using System.Net.Http.Headers;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using FlightScanner.Data;
using FlightScanner.Features.Integrations;
using Microsoft.EntityFrameworkCore;

namespace FlightScanner.Features.Flights;

public interface IFlightSearchService
{
    Task<IReadOnlyList<FlightOffer>> SearchAsync(FlightSearchQuery query, CancellationToken cancellationToken = default);
    Task<FlightOffer?> GetReturnFlightAsync(string departureToken, string currency, string? departureId = null, string? arrivalId = null, DateOnly? outboundDate = null, DateOnly? returnDate = null, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<FlightOffer>> GetReturnFlightsAsync(string departureToken, string currency, string? departureId = null, string? arrivalId = null, DateOnly? outboundDate = null, DateOnly? returnDate = null, CancellationToken cancellationToken = default);
    IReadOnlyList<AirlineSearchLink> GetAirlineSearchLinks(FlightSearchQuery query);
}

public sealed class FlightSearchService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration,
    ILogger<FlightSearchService> logger) : IFlightSearchService
{
    private const int SerpApiMaxOffers = 20;
    private const string SerpApiGoogleCountry = "ma";
    private const string SerpApiLanguage = "en";
    private const string GoogleFlightsProvider = "SerpApiGoogleFlights";
    private const string FreebaseIdentifierType = "FreebaseId";
    private static readonly TimeSpan OfferCacheDuration = TimeSpan.FromMinutes(2);
    private static int serpApiKeyCursor;
    private static readonly ConcurrentDictionary<string, Lazy<Task<FlightOfferCacheEntry>>> SearchOfferCache = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, Lazy<Task<FlightOfferCacheEntry>>> ReturnOfferCache = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, DateTimeOffset> WikidataIdentifierMissCache = new(StringComparer.OrdinalIgnoreCase);

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
        return await GetCachedOffersAsync(
            SearchOfferCache,
            BuildSearchCacheKey(query),
            () => SearchCoreAsync(query, CancellationToken.None),
            cancellationToken);
    }

    private async Task<IReadOnlyList<FlightOffer>> SearchCoreAsync(FlightSearchQuery query, CancellationToken cancellationToken)
    {
        if (UseDummyFlightData())
        {
            return BuildDummyOffers(query, false);
        }

        if (!query.FlexibleDates)
        {
            return await TrySearchConfiguredProviderAsync(query, cancellationToken);
        }

        var concreteQueries = ExpandFlexibleQueries(query).ToList();
        if (concreteQueries.Count == 0)
        {
            return [];
        }

        var offers = new List<FlightOffer>();
        foreach (var concreteQuery in concreteQueries)
        {
            offers.AddRange(await TrySearchConfiguredProviderAsync(concreteQuery, cancellationToken));
        }

        return offers
            .OrderBy(offer => offer.Price)
            .Take(SerpApiMaxOffers)
            .ToList();
    }

    public async Task<FlightOffer?> GetReturnFlightAsync(string departureToken, string currency, string? departureId = null, string? arrivalId = null, DateOnly? outboundDate = null, DateOnly? returnDate = null, CancellationToken cancellationToken = default)
    {
        return (await GetReturnFlightsAsync(departureToken, currency, departureId, arrivalId, outboundDate, returnDate, cancellationToken))
            .OrderBy(offer => offer.Price)
            .FirstOrDefault();
    }

    public async Task<IReadOnlyList<FlightOffer>> GetReturnFlightsAsync(string departureToken, string currency, string? departureId = null, string? arrivalId = null, DateOnly? outboundDate = null, DateOnly? returnDate = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(departureToken))
        {
            return [];
        }

        if (UseDummyFlightData())
        {
            return BuildDummyReturnOffers(departureToken, currency, departureId, arrivalId, outboundDate, returnDate);
        }

        return await GetCachedOffersAsync(
            ReturnOfferCache,
            BuildReturnCacheKey(departureToken, currency, departureId, arrivalId, outboundDate, returnDate),
            () => GetReturnFlightsCoreAsync(departureToken, currency, departureId, arrivalId, outboundDate, returnDate, CancellationToken.None),
            cancellationToken);
    }

    private async Task<IReadOnlyList<FlightOffer>> GetReturnFlightsCoreAsync(string departureToken, string currency, string? departureId, string? arrivalId, DateOnly? outboundDate, DateOnly? returnDate, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var setting = await db.IntegrationSettings.AsNoTracking()
            .FirstOrDefaultAsync(item => item.Kind == IntegrationKind.FlightProvider && item.Enabled, cancellationToken);
        if (setting is null || string.IsNullOrWhiteSpace(setting.SettingsJson))
        {
            return [];
        }

        var options = JsonSerializer.Deserialize<FlightProviderOptions>(setting.SettingsJson, JsonOptions()) ?? new();
        var apiKeys = GetSerpApiApiKeys(options);
        if (apiKeys.Count == 0)
        {
            return [];
        }

        var client = httpClientFactory.CreateClient("serpapi");
        return await FetchSerpApiReturnOffersAsync(client, apiKeys, departureToken, departureId, arrivalId, outboundDate, returnDate, currency, cancellationToken);
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

    private async Task<IReadOnlyList<FlightOffer>> GetCachedOffersAsync(
        ConcurrentDictionary<string, Lazy<Task<FlightOfferCacheEntry>>> cache,
        string key,
        Func<Task<IReadOnlyList<FlightOffer>>> factory,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        if (cache.TryGetValue(key, out var existing) &&
            existing.IsValueCreated &&
            existing.Value.IsCompletedSuccessfully)
        {
            var completed = await existing.Value;
            if (now - completed.CreatedAt <= OfferCacheDuration)
            {
                return completed.Offers;
            }

            cache.TryRemove(KeyValuePair.Create(key, existing));
        }

        PruneOfferCache(cache, now);
        var created = new Lazy<Task<FlightOfferCacheEntry>>(async () =>
            new FlightOfferCacheEntry(DateTimeOffset.UtcNow, await factory()));
        var current = cache.GetOrAdd(key, created);

        try
        {
            var entry = await current.Value.WaitAsync(cancellationToken);
            return entry.Offers;
        }
        catch
        {
            cache.TryRemove(KeyValuePair.Create(key, current));
            throw;
        }
    }

    private static void PruneOfferCache(ConcurrentDictionary<string, Lazy<Task<FlightOfferCacheEntry>>> cache, DateTimeOffset now)
    {
        if (cache.Count < 128)
        {
            return;
        }

        foreach (var item in cache)
        {
            if (!item.Value.IsValueCreated || !item.Value.Value.IsCompletedSuccessfully)
            {
                continue;
            }

            var entry = item.Value.Value.Result;
            if (now - entry.CreatedAt > OfferCacheDuration)
            {
                cache.TryRemove(item);
            }
        }
    }

    private bool UseDummyFlightData()
    {
        return configuration.GetValue<bool>("FlightScanner:UseDummyFlightData") ||
            configuration.GetValue<bool>("FLIGHTSCANNER_USE_DUMMY_FLIGHT_DATA") ||
            string.Equals(Environment.GetEnvironmentVariable("FLIGHTSCANNER_USE_DUMMY_FLIGHT_DATA"), "true", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildSearchCacheKey(FlightSearchQuery query)
    {
        return string.Join("|",
            "search",
            query.OriginType,
            query.Origin,
            query.DestinationType,
            query.Destination,
            query.DepartFrom,
            query.DepartTo,
            query.ReturnFrom,
            query.ReturnTo,
            query.FlexibleDates,
            query.FlexibleYear,
            query.FlexibleMonth,
            query.FlexibleDepartureDay,
            query.FlexibleStayDays,
            query.Adults,
            query.Children,
            query.Infants,
            query.Cabin,
            query.DirectOnly,
            query.MaxStops,
            query.CheckedBags,
            query.OutboundTimeFromHour,
            query.OutboundTimeToHour,
            query.ReturnTimeFromHour,
            query.ReturnTimeToHour,
            query.Currency);
    }

    private static string BuildReturnCacheKey(string departureToken, string currency, string? departureId, string? arrivalId, DateOnly? outboundDate, DateOnly? returnDate)
    {
        return string.Join("|",
            "return",
            departureToken,
            currency,
            departureId,
            arrivalId,
            outboundDate,
            returnDate);
    }

    private static IReadOnlyList<FlightOffer> BuildDummyOffers(FlightSearchQuery query, bool returns)
    {
        var airlines = returns
            ? new[] { "Transavia", "Royal Air Maroc", "Air France", "Iberia" }
            : new[] { "Royal Air Maroc", "Air France", "Transavia", "Iberia", "Turkish Airlines", "easyJet", "Ryanair", "TAP Air Portugal" };
        var origin = returns ? query.Destination : query.Origin;
        var destination = returns ? query.Origin : query.Destination;
        var date = returns
            ? query.ReturnFrom ?? query.DepartFrom.AddDays(Math.Clamp(query.FlexibleStayDays ?? 4, 1, 30))
            : query.DepartFrom;

        var fromHour = returns ? query.ReturnTimeFromHour : query.OutboundTimeFromHour;
        var toHour = returns ? query.ReturnTimeToHour : query.OutboundTimeToHour;
        return airlines.Select((airline, index) =>
        {
            var stops = query.DirectOnly ? 0 : index % 3 == 0 ? 1 : 0;
            var departTime = date.ToDateTime(new TimeOnly(6 + (index * 2 % 12), index % 2 == 0 ? 15 : 45));
            var duration = TimeSpan.FromMinutes(105 + (index * 35) + (stops * 45));
            var arrivalTime = departTime.Add(duration);
            var price = (returns ? 820 : 1480) + (index * 170) + (query.Adults * 55) + (query.Children * 35);
            var flightNumber = $"{airline.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0][0]}{(returns ? 70 : 40)}{index + 1}";
            var leg = new FlightItineraryLeg(
                returns ? "Return" : "Departure",
                date,
                duration,
                [
                    new FlightSegment(
                        new FlightAirport(origin, origin, departTime),
                        new FlightAirport(destination, destination, arrivalTime),
                        duration,
                        airline,
                        null,
                        flightNumber,
                        index % 2 == 0 ? "Airbus A320neo" : "Boeing 737",
                        query.Cabin.ToString(),
                        null,
                        stops == 0 ? ["Nonstop", "Dummy fare"] : ["1 stop", "Dummy fare"])
                ],
                []);

            return new FlightOffer(
                "Dummy flights",
                airline,
                flightNumber,
                origin,
                destination,
                date,
                returns ? null : query.ReturnFrom,
                price,
                string.IsNullOrWhiteSpace(query.Currency) ? "MAD" : query.Currency,
                stops,
                duration,
                "#",
                [leg],
                ["Generated test data", query.Cabin.ToString()],
                110000 + (index * 9000),
                132000,
                -12 + index,
                null,
                returns ? null : $"dummy-token-{index + 1}-{date:yyyyMMdd}",
                null,
                query.ReturnFrom is null ? "One way" : "Round trip",
                returns ? "Return" : "Departure");
        })
        .Where(offer => MatchesTimeWindow(offer, fromHour, toHour))
        .ToList();
    }

    private static bool MatchesTimeWindow(FlightOffer offer, int? fromHour, int? toHour)
    {
        if (fromHour is null && toHour is null)
        {
            return true;
        }

        var departureTime = offer.ItineraryLegs
            .SelectMany(leg => leg.Segments)
            .Select(segment => segment.DepartureAirport.Time)
            .FirstOrDefault(time => time is not null);
        if (departureTime is null)
        {
            return true;
        }

        var from = Math.Clamp(fromHour ?? 0, 0, 23);
        var to = Math.Clamp(toHour ?? 23, 0, 23);
        if (to < from)
        {
            (from, to) = (to, from);
        }

        return departureTime.Value.Hour >= from && departureTime.Value.Hour <= to;
    }

    private static IReadOnlyList<FlightOffer> BuildDummyReturnOffers(string departureToken, string currency, string? departureId, string? arrivalId, DateOnly? outboundDate, DateOnly? returnDate)
    {
        var departDate = outboundDate ?? DateOnly.FromDateTime(DateTime.Today.AddDays(14));
        var query = new FlightSearchQuery(
            LocationType.Airport,
            string.IsNullOrWhiteSpace(departureId) ? "Origin" : departureId,
            LocationType.Airport,
            string.IsNullOrWhiteSpace(arrivalId) ? "Destination" : arrivalId,
            departDate,
            departDate,
            returnDate ?? departDate.AddDays(4),
            returnDate ?? departDate.AddDays(4),
            false,
            null,
            null,
            null,
            null,
            1,
            0,
            0,
            CabinClass.Economy,
            false,
            null,
            0,
            null,
            null,
            null,
            null,
            string.IsNullOrWhiteSpace(currency) ? "MAD" : currency);

        return BuildDummyOffers(query, true)
            .Select(offer => CloneOffer(offer, resultKind: "Return", parentDepartureToken: departureToken))
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
        var apiKeys = GetSerpApiApiKeys(options);
        if (apiKeys.Count == 0)
        {
            return [];
        }

        var origin = await ResolveSerpApiLocationIdAsync(query.OriginType, query.Origin, cancellationToken);
        var destination = await ResolveSerpApiLocationIdAsync(query.DestinationType, query.Destination, cancellationToken);
        if (string.IsNullOrWhiteSpace(origin) || string.IsNullOrWhiteSpace(destination) ||
            origin.Equals(destination, StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        var client = httpClientFactory.CreateClient("serpapi");
        foreach (var apiKey in OrderSerpApiApiKeys(apiKeys))
        {
            try
            {
                var requestUrl = BuildSerpApiUrl(query, apiKey, origin, destination);
                using var request = new HttpRequestMessage(HttpMethod.Get, requestUrl);

                using var response = await client.SendAsync(request, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    logger.LogWarning("SerpApi Google Flights search failed with status {StatusCode} for key {ApiKey}. Trying next key if available.", response.StatusCode, MaskApiKey(apiKey));
                    continue;
                }                                                                         

                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
                if (document.RootElement.TryGetProperty("error", out var error))
                {
                    logger.LogWarning("SerpApi Google Flights returned an error for key {ApiKey}: {Error}. Trying next key if available.", MaskApiKey(apiKey), error.GetString());
                    continue;
                }

                var departureOffers = ParseSerpApiOffers(document.RootElement, query, origin, destination)
                    .OrderBy(offer => offer.Price)
                    .Take(SerpApiMaxOffers)
                    .ToList();
                return departureOffers;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "SerpApi Google Flights search failed for key {ApiKey}. Trying next key if available.", MaskApiKey(apiKey));
            }
        }

        return [];
    }

    private async Task<IReadOnlyList<FlightOffer>> BuildRoundTripOffersAsync(
        HttpClient client,
        IReadOnlyList<string> apiKeys,
        IReadOnlyList<FlightOffer> departureOffers,
        IReadOnlyList<FlightOffer> returnEligibleDepartures,
        string departureId,
        string arrivalId,
        string currency,
        CancellationToken cancellationToken)
    {
        var offers = new List<FlightOffer>(departureOffers);
        var returnEligibleByToken = returnEligibleDepartures
            .Where(offer => !string.IsNullOrWhiteSpace(offer.DepartureToken))
            .GroupBy(offer => offer.DepartureToken!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        foreach (var departure in returnEligibleByToken.Values)
        {
            var returnOptions = await FetchSerpApiReturnOffersAsync(client, apiKeys, departure.DepartureToken!, departureId, arrivalId, departure.DepartDate, departure.ReturnDate, currency, cancellationToken);
            offers.AddRange(returnOptions.Select(returnOffer => CloneOffer(
                returnOffer,
                resultKind: "Return",
                parentDepartureToken: departure.DepartureToken,
                componentPrice: returnOffer.Price)));
        }

        return offers
            .Where(offer => !offer.ResultKind.Equals("Return", StringComparison.OrdinalIgnoreCase) || !string.IsNullOrWhiteSpace(offer.ParentDepartureToken))
            .GroupBy(BuildOfferDedupeKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderBy(offer => offer.Price).First())
            .OrderBy(offer => offer.ResultKind.Equals("Return", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .ThenBy(offer => offer.Price)
            .Take(SerpApiMaxOffers * 2)
            .ToList();
    }

    private async Task<IReadOnlyList<FlightOffer>> FetchSerpApiReturnOffersAsync(
        HttpClient client,
        IReadOnlyList<string> apiKeys,
        string departureToken,
        string? departureId,
        string? arrivalId,
        DateOnly? outboundDate,
        DateOnly? returnDate,
        string currency,
        CancellationToken cancellationToken)
    {
        foreach (var apiKey in OrderSerpApiApiKeys(apiKeys))
        {
            try
            {
                var requestUrl = BuildSerpApiReturnUrl(apiKey, departureToken, departureId, arrivalId, outboundDate, returnDate, currency);
                using var request = new HttpRequestMessage(HttpMethod.Get, requestUrl);
                using var response = await client.SendAsync(request, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    logger.LogWarning("SerpApi Google Flights return details failed with status {StatusCode} for key {ApiKey}. Trying next key if available.", response.StatusCode, MaskApiKey(apiKey));
                    continue;
                }

                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
                if (document.RootElement.TryGetProperty("error", out var error))
                {
                    logger.LogWarning("SerpApi Google Flights return details returned an error for key {ApiKey}: {Error}. Trying next key if available.", MaskApiKey(apiKey), error.GetString());
                    continue;
                }

                return ParseSerpApiOffers(document.RootElement, null, null, null, "Return")
                    .OrderBy(offer => offer.Price)
                    .Take(SerpApiMaxOffers)
                    .ToList();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "SerpApi Google Flights return details failed for key {ApiKey}. Trying next key if available.", MaskApiKey(apiKey));
            }
        }

        return [];
    }

    private static IEnumerable<FlightSearchQuery> ExpandFlexibleQueries(FlightSearchQuery query)
    {
        if (!query.FlexibleDates)
        {
            yield return query;
            yield break;
        }

        if (query.FlexibleYear is not { } year ||
            query.FlexibleMonth is not { } month ||
            query.FlexibleDepartureDay is not { } departureDay ||
            year < 1900 ||
            month is < 1 or > 12)
        {
            yield break;
        }

        var stayDays = Math.Clamp(query.FlexibleStayDays ?? 1, 1, 365);
        var firstDay = new DateOnly(year, month, 1);
        var daysInMonth = DateTime.DaysInMonth(year, month);
        var isRoundTrip = query.ReturnFrom is not null;

        for (var day = 1; day <= daysInMonth; day++)
        {
            var departDate = firstDay.AddDays(day - 1);
            if (departDate.DayOfWeek != departureDay)
            {
                continue;
            }

            var returnDate = isRoundTrip
                ? departDate.AddDays(stayDays)
                : (DateOnly?)null;

            yield return query with
            {
                DepartFrom = departDate,
                DepartTo = departDate,
                ReturnFrom = returnDate,
                ReturnTo = returnDate,
                FlexibleDates = false
            };
        }
    }

    private static IReadOnlyList<string> GetSerpApiApiKeys(FlightProviderOptions options)
    {
        return new[] { options.SerpApiApiKeys, options.SerpApiApiKey }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .SelectMany(value => value.Split(['\r', '\n', ',', ';', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private static IReadOnlyList<string> OrderSerpApiApiKeys(IReadOnlyList<string> apiKeys)
    {
        if (apiKeys.Count <= 1)
        {
            return apiKeys;
        }

        var next = System.Threading.Interlocked.Increment(ref serpApiKeyCursor);
        var start = (int)((uint)next % (uint)apiKeys.Count);
        return apiKeys.Skip(start).Concat(apiKeys.Take(start)).ToList();
    }

    private static string MaskApiKey(string apiKey)
    {
        var trimmed = apiKey.Trim();
        return trimmed.Length <= 8
            ? "****"
            : $"{trimmed[..4]}...{trimmed[^4..]}";
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
                (location.Code.ToLower() == trimmed.ToLower() ||
                    ((type == LocationType.City || type == LocationType.Country) &&
                        location.Name.ToLower() == trimmed.ToLower())))
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

        var cachedIdentifier = await ResolveCachedLocationIdentifierAsync(db, type, trimmed, matches, cancellationToken);
        if (!string.IsNullOrWhiteSpace(cachedIdentifier))
        {
            return cachedIdentifier;
        }

        return type is LocationType.City
            ? await ResolveAndCacheWikidataCityIdentifierAsync(db, trimmed, matches, cancellationToken)
            : type is LocationType.Country
                ? await ResolveAndCacheWikidataCountryIdentifierAsync(db, trimmed, matches, cancellationToken)
                : null;
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

    private async Task<string?> ResolveAndCacheWikidataCityIdentifierAsync(
        ApplicationDbContext db,
        string value,
        IReadOnlyCollection<FlightLocation> matches,
        CancellationToken cancellationToken)
    {
        var city = PickUnambiguousCity(value, matches);
        if (city is null || string.IsNullOrWhiteSpace(city.CountryCode))
        {
            return null;
        }

        var missCacheKey = $"City:{city.Code}:{city.CountryCode}:{city.Name}";
        if (WikidataIdentifierMissCache.TryGetValue(missCacheKey, out var missedAt) &&
            DateTimeOffset.UtcNow - missedAt < TimeSpan.FromHours(12))
        {
            return null;
        }

        try
        {
            var freebaseId = await LookupWikidataCityFreebaseIdAsync(city.Name, city.CountryCode, cancellationToken);
            if (string.IsNullOrWhiteSpace(freebaseId))
            {
                WikidataIdentifierMissCache[missCacheKey] = DateTimeOffset.UtcNow;
                return null;
            }

            await UpsertCachedIdentifierAsync(db, LocationType.City, city.Code, freebaseId, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            WikidataIdentifierMissCache.TryRemove(missCacheKey, out _);
            logger.LogInformation("Cached Wikidata Freebase ID for city {City}, {CountryCode}.", city.Name, city.CountryCode);
            return freebaseId;
        }
        catch (Exception ex)
        {
            WikidataIdentifierMissCache[missCacheKey] = DateTimeOffset.UtcNow;
            logger.LogWarning(ex, "On-demand Wikidata city identifier lookup failed for {City}, {CountryCode}.", city.Name, city.CountryCode);
            return null;
        }
    }

    private async Task<string?> ResolveAndCacheWikidataCountryIdentifierAsync(
        ApplicationDbContext db,
        string value,
        IReadOnlyCollection<FlightLocation> matches,
        CancellationToken cancellationToken)
    {
        var country = PickUnambiguousCountry(value, matches);
        if (country is null || string.IsNullOrWhiteSpace(country.Code))
        {
            return null;
        }

        var missCacheKey = $"Country:{country.Code}:{country.Name}";
        if (WikidataIdentifierMissCache.TryGetValue(missCacheKey, out var missedAt) &&
            DateTimeOffset.UtcNow - missedAt < TimeSpan.FromHours(12))
        {
            return null;
        }

        try
        {
            var freebaseId = await LookupWikidataCountryFreebaseIdAsync(country.Code, cancellationToken);
            if (string.IsNullOrWhiteSpace(freebaseId))
            {
                WikidataIdentifierMissCache[missCacheKey] = DateTimeOffset.UtcNow;
                return null;
            }

            await UpsertCachedIdentifierAsync(db, LocationType.Country, country.Code, freebaseId, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            WikidataIdentifierMissCache.TryRemove(missCacheKey, out _);
            logger.LogInformation("Cached Wikidata Freebase ID for country {Country} ({CountryCode}).", country.Name, country.Code);
            return freebaseId;
        }
        catch (Exception ex)
        {
            WikidataIdentifierMissCache[missCacheKey] = DateTimeOffset.UtcNow;
            logger.LogWarning(ex, "On-demand Wikidata country identifier lookup failed for {Country} ({CountryCode}).", country.Name, country.Code);
            return null;
        }
    }

    private static async Task UpsertCachedIdentifierAsync(
        ApplicationDbContext db,
        LocationType locationType,
        string locationCode,
        string freebaseId,
        CancellationToken cancellationToken)
    {
        var normalizedCode = locationCode.Trim().ToUpperInvariant();
        var existing = await db.FlightLocationIdentifiers.FirstOrDefaultAsync(identifier =>
            identifier.LocationType == locationType &&
            identifier.LocationCode == normalizedCode &&
            identifier.Provider == GoogleFlightsProvider &&
            identifier.IdentifierType == FreebaseIdentifierType,
            cancellationToken);

        if (existing is null)
        {
            db.FlightLocationIdentifiers.Add(new FlightLocationIdentifier
            {
                LocationType = locationType,
                LocationCode = normalizedCode,
                Provider = GoogleFlightsProvider,
                IdentifierType = FreebaseIdentifierType,
                Identifier = freebaseId,
                Source = "WikidataOnDemand",
                UpdatedAt = DateTimeOffset.UtcNow
            });
            return;
        }

        existing.Identifier = freebaseId;
        existing.Source = "WikidataOnDemand";
        existing.UpdatedAt = DateTimeOffset.UtcNow;
    }

    private async Task<string?> LookupWikidataCityFreebaseIdAsync(string cityName, string countryCode, CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient("wikidata-on-demand");
        var candidateIds = await SearchWikidataEntitiesAsync(client, cityName, cancellationToken);
        if (candidateIds.Count == 0)
        {
            return null;
        }

        using var cityDocument = await GetWikidataEntitiesAsync(client, candidateIds, cancellationToken);
        var candidates = ReadWikidataEntities(cityDocument.RootElement)
            .Select(entity => new
            {
                Id = entity.Key,
                FreebaseId = ReadWikidataClaimString(entity.Value, "P646"),
                CountryId = ReadWikidataClaimEntityId(entity.Value, "P17"),
                IsCityLike = ReadWikidataClaimEntityIds(entity.Value, "P31")
                    .Any(IsCityLikeWikidataType)
            })
            .Where(candidate =>
                !string.IsNullOrWhiteSpace(candidate.FreebaseId) &&
                !string.IsNullOrWhiteSpace(candidate.CountryId))
            .ToList();
        if (candidates.Count == 0)
        {
            return null;
        }

        var countryIds = candidates
            .Select(candidate => candidate.CountryId!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        using var countryDocument = await GetWikidataEntitiesAsync(client, countryIds, cancellationToken);
        var matchingCountryIds = ReadWikidataEntities(countryDocument.RootElement)
            .Where(entity => ReadWikidataClaimString(entity.Value, "P297")?.Equals(countryCode.Trim().ToUpperInvariant(), StringComparison.OrdinalIgnoreCase) == true)
            .Select(entity => entity.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return candidates
            .Where(candidate => matchingCountryIds.Contains(candidate.CountryId!))
            .OrderByDescending(candidate => candidate.IsCityLike)
            .Select(candidate => candidate.FreebaseId)
            .FirstOrDefault();
    }

    private static async Task<IReadOnlyList<string>> SearchWikidataEntitiesAsync(HttpClient client, string searchText, CancellationToken cancellationToken)
    {
        var requestUrl = "https://www.wikidata.org/w/api.php?action=wbsearchentities" +
            $"&search={Uri.EscapeDataString(searchText.Trim())}" +
            "&language=en&format=json&type=item&limit=10";
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUrl);
        request.Headers.UserAgent.ParseAdd("FlightScanner/1.0 (self-hosted app; Wikidata entity search)");
        using var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));

        if (!document.RootElement.TryGetProperty("search", out var search) || search.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return search.EnumerateArray()
            .Select(item => item.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static async Task<JsonDocument> GetWikidataEntitiesAsync(HttpClient client, IReadOnlyCollection<string> entityIds, CancellationToken cancellationToken)
    {
        var requestUrl = "https://www.wikidata.org/w/api.php?action=wbgetentities" +
            $"&ids={Uri.EscapeDataString(string.Join('|', entityIds))}" +
            "&props=claims|labels&languages=en&format=json";
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUrl);
        request.Headers.UserAgent.ParseAdd("FlightScanner/1.0 (self-hosted app; Wikidata entity claims)");
        using var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
    }

    private async Task<string?> LookupWikidataCountryFreebaseIdAsync(string countryCode, CancellationToken cancellationToken)
    {
        var query = $$"""
            SELECT DISTINCT ?freebase WHERE {
              ?country wdt:P297 "{{EscapeSparqlString(countryCode.Trim().ToUpperInvariant())}}";
                       wdt:P646 ?freebase.
            }
            LIMIT 1
            """;

        var requestUrl = $"https://query.wikidata.org/sparql?format=json&query={Uri.EscapeDataString(query)}";
        var client = httpClientFactory.CreateClient("wikidata-on-demand");
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUrl);
        request.Headers.UserAgent.ParseAdd("FlightScanner/1.0 (self-hosted app; on-demand country lookup)");
        using var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));

        return ReadSparqlBindings(document.RootElement)
            .Select(binding => ReadSparqlValue(binding, "freebase"))
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
    }

    private static FlightLocation? PickUnambiguousCity(string value, IReadOnlyCollection<FlightLocation> matches)
    {
        if (matches.Count == 0)
        {
            return null;
        }

        var codeMatches = matches
            .Where(match => match.Code.Equals(value, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (codeMatches.Count == 1)
        {
            return codeMatches[0];
        }

        var exactNameMatches = matches
            .Where(match => match.Name.Equals(value, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (exactNameMatches.Select(match => match.CountryCode).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1)
        {
            return exactNameMatches
                .OrderBy(match => match.Code.Length)
                .ThenBy(match => match.Code)
                .FirstOrDefault();
        }

        return null;
    }

    private static FlightLocation? PickUnambiguousCountry(string value, IReadOnlyCollection<FlightLocation> matches)
    {
        if (matches.Count == 0)
        {
            return null;
        }

        var codeMatches = matches
            .Where(match => match.Code.Equals(value, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (codeMatches.Count == 1)
        {
            return codeMatches[0];
        }

        var exactNameMatches = matches
            .Where(match => match.Name.Equals(value, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return exactNameMatches.Count == 1 ? exactNameMatches[0] : null;
    }

    private static string EscapeSparqlString(string value)
    {
        return value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);
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

    private static IEnumerable<KeyValuePair<string, JsonElement>> ReadWikidataEntities(JsonElement root)
    {
        if (!root.TryGetProperty("entities", out var entities) || entities.ValueKind != JsonValueKind.Object)
        {
            yield break;
        }

        foreach (var entity in entities.EnumerateObject())
        {
            if (entity.Value.ValueKind == JsonValueKind.Object &&
                (!entity.Value.TryGetProperty("missing", out var missing) || missing.ValueKind != JsonValueKind.String))
            {
                yield return new KeyValuePair<string, JsonElement>(entity.Name, entity.Value);
            }
        }
    }

    private static string? ReadWikidataClaimString(JsonElement entity, string propertyId)
    {
        return ReadWikidataClaims(entity, propertyId)
            .Select(claim => TryReadWikidataClaimValue(claim, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
    }

    private static string? ReadWikidataClaimEntityId(JsonElement entity, string propertyId)
    {
        return ReadWikidataClaimEntityIds(entity, propertyId).FirstOrDefault();
    }

    private static IEnumerable<string> ReadWikidataClaimEntityIds(JsonElement entity, string propertyId)
    {
        foreach (var claim in ReadWikidataClaims(entity, propertyId))
        {
            if (!TryReadWikidataClaimValue(claim, out var value) ||
                value.ValueKind != JsonValueKind.Object ||
                !value.TryGetProperty("id", out var id) ||
                id.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(id.GetString()))
            {
                continue;
            }

            yield return id.GetString()!;
        }
    }

    private static IEnumerable<JsonElement> ReadWikidataClaims(JsonElement entity, string propertyId)
    {
        if (!entity.TryGetProperty("claims", out var claims) ||
            claims.ValueKind != JsonValueKind.Object ||
            !claims.TryGetProperty(propertyId, out var propertyClaims) ||
            propertyClaims.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var claim in propertyClaims.EnumerateArray())
        {
            yield return claim;
        }
    }

    private static bool TryReadWikidataClaimValue(JsonElement claim, out JsonElement value)
    {
        value = default;
        if (!claim.TryGetProperty("mainsnak", out var mainsnak) ||
            mainsnak.ValueKind != JsonValueKind.Object ||
            !mainsnak.TryGetProperty("datavalue", out var dataValue) ||
            dataValue.ValueKind != JsonValueKind.Object ||
            !dataValue.TryGetProperty("value", out value))
        {
            return false;
        }

        return true;
    }

    private static bool IsCityLikeWikidataType(string entityId)
    {
        return entityId is "Q515" or "Q1549591" or "Q3957" or "Q486972";
    }

    private static string BuildSerpApiUrl(FlightSearchQuery query, string apiKey, string origin, string destination)
    {
        var parameters = new Dictionary<string, string?>
        {
            ["engine"] = "google_flights",
            ["api_key"] = apiKey,
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

        var outboundTimes = BuildSerpApiTimeRange(query.OutboundTimeFromHour, query.OutboundTimeToHour);
        if (!string.IsNullOrWhiteSpace(outboundTimes))
        {
            parameters["outbound_times"] = outboundTimes;
        }

        var returnTimes = query.ReturnFrom is null
            ? null
            : BuildSerpApiTimeRange(query.ReturnTimeFromHour, query.ReturnTimeToHour);
        if (!string.IsNullOrWhiteSpace(returnTimes))
        {
            parameters["return_times"] = returnTimes;
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

    private static string? BuildSerpApiTimeRange(int? fromHour, int? toHour)
    {
        if (fromHour is null && toHour is null)
        {
            return null;
        }

        var from = Math.Clamp(fromHour ?? 0, 0, 23);
        var to = Math.Clamp(toHour ?? 23, 0, 23);
        if (to < from)
        {
            (from, to) = (to, from);
        }

        return $"{from},{to}";
    }

    private static string BuildSerpApiReturnUrl(string apiKey, string departureToken, string? departureId, string? arrivalId, DateOnly? outboundDate, DateOnly? returnDate, string currency)
    {
        var parameters = new Dictionary<string, string?>
        {
            ["engine"] = "google_flights",
            ["api_key"] = apiKey,
            ["departure_id"] = departureId,
            ["arrival_id"] = arrivalId,
            ["outbound_date"] = outboundDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["return_date"] = returnDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
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
        return ParseSerpApiOfferElements(
            root,
            ReadSerpApiOfferArray(root, "best_flights").Concat(ReadSerpApiOfferArray(root, "other_flights")),
            query,
            origin,
            destination,
            legKind);
    }

    private static IEnumerable<FlightOffer> ParseSerpApiOfferElements(
        JsonElement root,
        IEnumerable<JsonElement> offerElements,
        FlightSearchQuery? query,
        string? origin,
        string? destination,
        string legKind = "Departure")
    {
        foreach (var offer in offerElements)
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
                ReadString(offer, "type") ?? (query?.ReturnFrom is null ? "One way" : "Round trip"),
                legKind);
        }
    }

    private static IReadOnlyList<FlightOffer> ParseTopSerpApiDepartureOffersForReturnSearch(
        JsonElement root,
        FlightSearchQuery query,
        string origin,
        string destination)
    {
        var best = ParseSerpApiOfferElements(root, ReadSerpApiOfferArray(root, "best_flights"), query, origin, destination)
            .OrderBy(offer => offer.Price)
            .Where(offer => !string.IsNullOrWhiteSpace(offer.DepartureToken))
            .Take(2)
            .ToList();
        if (best.Count > 0)
        {
            return best;
        }

        return ParseSerpApiOfferElements(root, ReadSerpApiOfferArray(root, "other_flights"), query, origin, destination)
            .OrderBy(offer => offer.Price)
            .Where(offer => !string.IsNullOrWhiteSpace(offer.DepartureToken))
            .Take(2)
            .ToList();
    }

    private static string BuildOfferDedupeKey(FlightOffer offer)
    {
        var segments = string.Join(";", offer.ItineraryLegs
            .SelectMany(leg => leg.Segments)
            .Select(segment => string.Join(":",
                segment.DepartureAirport.Code,
                FormatDedupeTime(segment.DepartureAirport.Time),
                segment.ArrivalAirport.Code,
                FormatDedupeTime(segment.ArrivalAirport.Time),
                segment.FlightNumber)));
        var departureToken = offer.ResultKind.Equals("Return", StringComparison.OrdinalIgnoreCase)
            ? ""
            : offer.DepartureToken ?? "";

        return string.Join("|",
            offer.ResultKind,
            departureToken,
            segments,
            offer.Price.ToString("0.##", CultureInfo.InvariantCulture),
            offer.Currency);
    }

    private static string FormatDedupeTime(DateTime? value)
    {
        return value?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) ?? "";
    }

    private static FlightOffer CloneOffer(
        FlightOffer offer,
        decimal? price = null,
        string? resultKind = null,
        string? parentDepartureToken = null,
        decimal? componentPrice = null,
        decimal? pairedReturnPrice = null,
        decimal? roundTripTotalPrice = null)
    {
        return new FlightOffer(
            offer.Provider,
            offer.Airline,
            offer.FlightNumber,
            offer.Origin,
            offer.Destination,
            offer.DepartDate,
            offer.ReturnDate,
            price ?? offer.Price,
            offer.Currency,
            offer.Stops,
            offer.Duration,
            offer.BookingUrl,
            offer.ItineraryLegs,
            offer.Extensions,
            offer.CarbonEmissionsGrams,
            offer.TypicalCarbonEmissionsGrams,
            offer.EmissionsDifferencePercent,
            offer.AirlineLogoUrl,
            offer.DepartureToken,
            offer.BookingToken,
            offer.TripType,
            resultKind ?? offer.ResultKind,
            parentDepartureToken ?? offer.ParentDepartureToken,
            componentPrice ?? offer.ComponentPrice,
            pairedReturnPrice ?? offer.PairedReturnPrice,
            roundTripTotalPrice ?? offer.RoundTripTotalPrice);
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
            .Replace("{{outboundTimeFromHour}}", query.OutboundTimeFromHour?.ToString() ?? "", StringComparison.OrdinalIgnoreCase)
            .Replace("{{outboundTimeToHour}}", query.OutboundTimeToHour?.ToString() ?? "", StringComparison.OrdinalIgnoreCase)
            .Replace("{{returnTimeFromHour}}", query.ReturnTimeFromHour?.ToString() ?? "", StringComparison.OrdinalIgnoreCase)
            .Replace("{{returnTimeToHour}}", query.ReturnTimeToHour?.ToString() ?? "", StringComparison.OrdinalIgnoreCase)
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

    private sealed record FlightOfferCacheEntry(DateTimeOffset CreatedAt, IReadOnlyList<FlightOffer> Offers);
}
