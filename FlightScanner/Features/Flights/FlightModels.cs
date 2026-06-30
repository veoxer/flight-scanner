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
    [MaxLength(120)]
    public string? NameFr { get; set; }
    [MaxLength(120)]
    public string? NameAr { get; set; }
    [MaxLength(2)]
    public string? CountryCode { get; set; }
    [MaxLength(80)]
    public string? CountryName { get; set; }
    [MaxLength(80)]
    public string? CountryNameFr { get; set; }
    [MaxLength(80)]
    public string? CountryNameAr { get; set; }
    [MaxLength(32)]
    public string Continent { get; set; } = "";
    [MaxLength(32)]
    public string? ContinentFr { get; set; }
    [MaxLength(32)]
    public string? ContinentAr { get; set; }
}

public sealed class FlightLocationIdentifier
{
    public int Id { get; set; }
    public LocationType LocationType { get; set; }
    [MaxLength(16)]
    public string LocationCode { get; set; } = "";
    [MaxLength(40)]
    public string Provider { get; set; } = "SerpApiGoogleFlights";
    [MaxLength(32)]
    public string IdentifierType { get; set; } = "FreebaseId";
    [MaxLength(128)]
    public string Identifier { get; set; } = "";
    [MaxLength(80)]
    public string Source { get; set; } = "Wikidata";
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
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
    bool FlexibleDates,
    int? FlexibleYear,
    int? FlexibleMonth,
    DayOfWeek? FlexibleDepartureDay,
    int? FlexibleStayDays,
    int Adults,
    int Children,
    int Infants,
    CabinClass Cabin,
    bool DirectOnly,
    int? MaxStops,
    int CheckedBags,
    int? OutboundTimeFromHour,
    int? OutboundTimeToHour,
    int? ReturnTimeFromHour,
    int? ReturnTimeToHour,
    string Currency);

public sealed class FlightOffer
{
    public FlightOffer(
        string provider,
        string airline,
        string flightNumber,
        string origin,
        string destination,
        DateOnly departDate,
        DateOnly? returnDate,
        decimal price,
        string currency,
        int stops,
        TimeSpan duration,
        string bookingUrl,
        IReadOnlyList<FlightItineraryLeg>? itineraryLegs = null,
        IReadOnlyList<string>? extensions = null,
        int? carbonEmissionsGrams = null,
        int? typicalCarbonEmissionsGrams = null,
        int? emissionsDifferencePercent = null,
        string? airlineLogoUrl = null,
        string? departureToken = null,
        string? bookingToken = null,
        string tripType = "One way",
        string resultKind = "Departure",
        string? parentDepartureToken = null,
        decimal? componentPrice = null,
        decimal? pairedReturnPrice = null,
        decimal? roundTripTotalPrice = null)
    {
        Provider = provider;
        Airline = airline;
        FlightNumber = flightNumber;
        Origin = origin;
        Destination = destination;
        DepartDate = departDate;
        ReturnDate = returnDate;
        Price = price;
        Currency = currency;
        Stops = stops;
        Duration = duration;
        BookingUrl = bookingUrl;
        ItineraryLegs = itineraryLegs ?? [];
        Extensions = extensions ?? [];
        CarbonEmissionsGrams = carbonEmissionsGrams;
        TypicalCarbonEmissionsGrams = typicalCarbonEmissionsGrams;
        EmissionsDifferencePercent = emissionsDifferencePercent;
        AirlineLogoUrl = airlineLogoUrl;
        DepartureToken = departureToken;
        BookingToken = bookingToken;
        TripType = tripType;
        ResultKind = resultKind;
        ParentDepartureToken = parentDepartureToken;
        ComponentPrice = componentPrice;
        PairedReturnPrice = pairedReturnPrice;
        RoundTripTotalPrice = roundTripTotalPrice;
    }

    public string Provider { get; }
    public string Airline { get; }
    public string FlightNumber { get; }
    public string Origin { get; }
    public string Destination { get; }
    public DateOnly DepartDate { get; }
    public DateOnly? ReturnDate { get; }
    public decimal Price { get; }
    public string Currency { get; }
    public int Stops { get; }
    public TimeSpan Duration { get; }
    public string BookingUrl { get; }
    public IReadOnlyList<FlightItineraryLeg> ItineraryLegs { get; }
    public IReadOnlyList<string> Extensions { get; }
    public int? CarbonEmissionsGrams { get; }
    public int? TypicalCarbonEmissionsGrams { get; }
    public int? EmissionsDifferencePercent { get; }
    public string? AirlineLogoUrl { get; }
    public string? DepartureToken { get; }
    public string? BookingToken { get; }
    public string TripType { get; }
    public string ResultKind { get; }
    public string? ParentDepartureToken { get; }
    public decimal? ComponentPrice { get; }
    public decimal? PairedReturnPrice { get; }
    public decimal? RoundTripTotalPrice { get; }
}

public sealed record FlightItineraryLeg(
    string Kind,
    DateOnly? Date,
    TimeSpan Duration,
    IReadOnlyList<FlightSegment> Segments,
    IReadOnlyList<FlightLayover> Layovers);

public sealed record FlightSegment(
    FlightAirport DepartureAirport,
    FlightAirport ArrivalAirport,
    TimeSpan Duration,
    string Airline,
    string? AirlineLogoUrl,
    string FlightNumber,
    string? Airplane,
    string? TravelClass,
    string? Legroom,
    IReadOnlyList<string> Extensions);

public sealed record FlightAirport(
    string Name,
    string Code,
    DateTime? Time);

public sealed record FlightLayover(
    string Name,
    string Code,
    TimeSpan Duration,
    bool Overnight);

public sealed record AirlineSearchLink(
    string Airline,
    string Category,
    string Region,
    string SearchUrl,
    string Notes);
