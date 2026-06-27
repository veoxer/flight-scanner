namespace FlightScanner.Features.Alerts;

public sealed class AlertScanOptions
{
    public int IntervalMinutes { get; set; } = 180;

    public TimeSpan Interval => TimeSpan.FromMinutes(Math.Clamp(IntervalMinutes, 15, 10080));
}
