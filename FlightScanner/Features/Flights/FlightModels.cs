using System.ComponentModel.DataAnnotations;

namespace FlightScanner.Features.Flights;

public enum LocationType
{
    Airport = 0,
    City = 1,
    Country = 2,
    Continent = 3
}

public enum CabinClass
{
    Economy = 0,
    PremiumEconomy = 1,
    Business = 2,
    First = 3
}

public sealed class FlightLocation
{
    public int Id { get; set; }
    public LocationType Type { get; set; }
    [MaxLength(16)]
    public string Code { get; set; } = "";
    [MaxLength(120)]
    public string Name { get; set; } = "";
    [MaxLength(2)]
    public string? CountryCode { get; set; }
    [MaxLength(80)]
    public string? CountryName { get; set; }
    [MaxLength(32)]
    public string Continent { get; set; } = "";
}

public sealed record FlightSearchQuery(
    LocationType OriginType,
    string Origin,
    LocationType DestinationType,
    string Destination,
    DateOnly DepartFrom,
    DateOnly DepartTo,
    DateOnly? ReturnFrom,
    DateOnly? ReturnTo,
    int Adults,
    int Children,
    int Infants,
    CabinClass Cabin,
    bool DirectOnly,
    int? MaxStops,
    int CheckedBags,
    string Currency);

public sealed record FlightOffer(
    string Provider,
    string Airline,
    string FlightNumber,
    string Origin,
    string Destination,
    DateOnly DepartDate,
    DateOnly? ReturnDate,
    decimal Price,
    string Currency,
    int Stops,
    TimeSpan Duration,
    string BookingUrl);

public sealed record AirlineSearchLink(
    string Airline,
    string Category,
    string Region,
    string SearchUrl,
    string Notes);
