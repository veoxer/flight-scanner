namespace FlightScanner.Features.Alerts;

public sealed class AlertScanOptions
{
    public const int DefaultIntervalMinutes = 180;
    public const int MinIntervalMinutes = 15;
    public const int MaxIntervalMinutes = 10080;

    public int IntervalMinutes { get; set; } = DefaultIntervalMinutes;

    public TimeSpan Interval => TimeSpan.FromMinutes(NormalizeMinutes(IntervalMinutes));

    public static int NormalizeMinutes(int intervalMinutes) =>
        Math.Clamp(intervalMinutes, MinIntervalMinutes, MaxIntervalMinutes);
}
