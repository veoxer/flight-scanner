using System.Globalization;
using System.Net;
using System.Text.Json;
using FlightScanner.Data;
using FlightScanner.Features.Integrations;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;

namespace FlightScanner.Features.Flights;

public sealed record IndicativeFlightCandidate(
    DateOnly DepartureDate,
    DateOnly? ReturnDate,
    decimal Price,
    string Currency,
    string OriginAirport,
    string DestinationAirport,
    string Airline,
    int Stops);

public sealed record TravelpayoutsDiscoveryResult(
    IReadOnlyList<IndicativeFlightCandidate> Candidates,
    string? Error);

public interface ITravelpayoutsDiscoveryService
{
    Task<TravelpayoutsDiscoveryResult> SearchMonthAsync(
        string origin,
        string destination,
        int year,
        int month,
        int? stayDays,
        DayOfWeek? departureDay,
        CancellationToken cancellationToken = default);
}

public sealed class TravelpayoutsDiscoveryService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    IHttpClientFactory httpClientFactory,
    ILogger<TravelpayoutsDiscoveryService> logger) : ITravelpayoutsDiscoveryService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<TravelpayoutsDiscoveryResult> SearchMonthAsync(
        string origin,
        string destination,
        int year,
        int month,
        int? stayDays,
        DayOfWeek? departureDay,
        CancellationToken cancellationToken = default)
    {
        origin = origin.Trim().ToUpperInvariant();
        destination = destination.Trim().ToUpperInvariant();
        if (!IsIataCode(origin) || !IsIataCode(destination) || origin == destination ||
            year < DateTime.UtcNow.Year || year > DateTime.UtcNow.Year + 1 ||
            month is < 1 or > 12 || stayDays is < 1 or > 60)
        {
            return new([], "Enter different three-letter airport or city codes, a valid month, and a stay of 1–60 days.");
        }

        var firstDay = new DateOnly(year, month, 1);
        if (firstDay.AddMonths(1) <= DateOnly.FromDateTime(DateTime.UtcNow))
        {
            return new([], "Choose the current month or a future month.");
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var setting = await db.IntegrationSettings.AsNoTracking()
            .FirstOrDefaultAsync(item => item.Kind == IntegrationKind.Travelpayouts, cancellationToken);
        var options = setting is null
            ? new TravelpayoutsOptions()
            : JsonSerializer.Deserialize<TravelpayoutsOptions>(setting.SettingsJson, JsonOptions) ?? new();
        if (string.IsNullOrWhiteSpace(options.ApiToken))
        {
            return new([], "Save your Travelpayouts API token first.");
        }

        var parameters = new Dictionary<string, string?>
        {
            ["origin"] = origin,
            ["destination"] = destination,
            ["departure_at"] = firstDay.ToString("yyyy-MM", CultureInfo.InvariantCulture),
            ["group_by"] = "departure_at",
            ["currency"] = "eur",
            ["direct"] = "false"
        };
        if (stayDays is { } duration)
        {
            parameters["min_trip_duration"] = duration.ToString(CultureInfo.InvariantCulture);
            parameters["max_trip_duration"] = duration.ToString(CultureInfo.InvariantCulture);
        }

        var uri = QueryHelpers.AddQueryString("https://api.travelpayouts.com/aviasales/v3/grouped_prices", parameters);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.TryAddWithoutValidation("X-Access-Token", options.ApiToken.Trim());
        try
        {
            var client = httpClientFactory.CreateClient("travelpayouts");
            using var response = await client.SendAsync(request, cancellationToken);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return new([], "Travelpayouts rejected the token. Check Profile → API token and Aviasales access.");
            }
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Travelpayouts discovery returned HTTP {StatusCode}.", (int)response.StatusCode);
                return new([], $"Travelpayouts returned HTTP {(int)response.StatusCode}. Try again later.");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = document.RootElement;
            if (!root.TryGetProperty("success", out var success) || success.ValueKind != JsonValueKind.True)
            {
                return new([], "Travelpayouts did not return a successful response for this search.");
            }
            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            {
                return new([], "Travelpayouts returned no observed fares for this route and month.");
            }

            var candidates = new List<IndicativeFlightCandidate>();
            foreach (var item in data.EnumerateObject())
            {
                var fare = item.Value;
                if (fare.ValueKind != JsonValueKind.Object ||
                    !TryDate(fare, "departure_at", out var departure) ||
                    departure.Year != year || departure.Month != month ||
                    (departureDay is { } weekday && departure.DayOfWeek != weekday) ||
                    !fare.TryGetProperty("price", out var priceElement) ||
                    !priceElement.TryGetDecimal(out var price) || price <= 0)
                {
                    continue;
                }

                DateOnly? returnDate = TryDate(fare, "return_at", out var parsedReturn) ? parsedReturn : null;
                if (stayDays is { } days &&
                    (returnDate is null || returnDate.Value.DayNumber - departure.DayNumber != days))
                {
                    continue;
                }
                if (stayDays is null && returnDate is not null)
                {
                    continue;
                }

                candidates.Add(new IndicativeFlightCandidate(
                    departure,
                    returnDate,
                    price,
                    ReadString(fare, "currency")?.ToUpperInvariant() ?? "EUR",
                    ReadString(fare, "origin_airport") ?? origin,
                    ReadString(fare, "destination_airport") ?? destination,
                    ReadString(fare, "airline") ?? "—",
                    ReadInt(fare, "transfers")));
            }

            return new(candidates.OrderBy(candidate => candidate.Price).ToList(), null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Travelpayouts discovery request failed.");
            return new([], "Could not reach Travelpayouts. Try again later.");
        }
    }

    private static bool IsIataCode(string value) => value.Length == 3 && value.All(c => c is >= 'A' and <= 'Z');

    private static bool TryDate(JsonElement fare, string property, out DateOnly date)
    {
        date = default;
        var value = ReadString(fare, property);
        return value is { Length: >= 10 } &&
            DateOnly.TryParseExact(value[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
    }

    private static string? ReadString(JsonElement fare, string property) =>
        fare.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int ReadInt(JsonElement fare, string property) =>
        fare.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt32(out var parsed) ? parsed : 0;
}
