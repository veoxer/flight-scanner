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
    IReadOnlyList<AirlineSearchLink> GetAirlineSearchLinks(FlightSearchQuery query);
}

public sealed class FlightSearchService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    IHttpClientFactory httpClientFactory,
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
