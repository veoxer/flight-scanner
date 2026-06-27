using System.ComponentModel.DataAnnotations;
using FlightScanner.Data;
using FlightScanner.Features.Flights;

namespace FlightScanner.Features.Alerts;

public sealed class PriceAlert
{
    public int Id { get; set; }
    public string UserId { get; set; } = "";
    public ApplicationUser? User { get; set; }
    public LocationType OriginType { get; set; }
    [MaxLength(120)]
    public string Origin { get; set; } = "";
    public LocationType DestinationType { get; set; }
    [MaxLength(120)]
    public string Destination { get; set; } = "";
    public DateOnly DepartFrom { get; set; }
    public DateOnly DepartTo { get; set; }
    public DateOnly? ReturnFrom { get; set; }
    public DateOnly? ReturnTo { get; set; }
    public int Adults { get; set; } = 1;
    public int Children { get; set; }
    public int Infants { get; set; }
    public CabinClass Cabin { get; set; }
    public bool DirectOnly { get; set; }
    public int? MaxStops { get; set; }
    public int CheckedBags { get; set; }
    [MaxLength(3)]
    public string Currency { get; set; } = "MAD";
    [MaxLength(8)]
    public string TargetMode { get; set; } = "Max";
    public decimal TargetPrice { get; set; }
    public decimal? MaxTargetPrice { get; set; }
    public decimal? MinTargetPrice { get; set; }
    public bool NotifyByPush { get; set; } = true;
    public bool NotifyByEmail { get; set; } = true;
    public bool NotifyByWhatsApp { get; set; }
    public string? WhatsAppTo { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastCheckedAt { get; set; }
    public decimal? LastObservedPrice { get; set; }
    public DateTimeOffset? LastMatchedAt { get; set; }
    public ICollection<FlightScanResult> ScanResults { get; set; } = [];
}

public sealed class FlightScanResult
{
    public int Id { get; set; }
    public int PriceAlertId { get; set; }
    public PriceAlert? PriceAlert { get; set; }
    public string Provider { get; set; } = "";
    public string Airline { get; set; } = "";
    public string FlightNumber { get; set; } = "";
    public decimal Price { get; set; }
    public string Currency { get; set; } = "USD";
    public int Stops { get; set; }
    public string BookingUrl { get; set; } = "";
    public DateTimeOffset CheckedAt { get; set; } = DateTimeOffset.UtcNow;
    public bool TriggeredAlert { get; set; }
}

public sealed class NotificationAttempt
{
    public int Id { get; set; }
    public int? PriceAlertId { get; set; }
    public string Channel { get; set; } = "";
    public string Recipient { get; set; } = "";
    public string Subject { get; set; } = "";
    public string Body { get; set; } = "";
    public bool Succeeded { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
