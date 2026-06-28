using FlightScanner.Data;
using FlightScanner.Features.Flights;
using FlightScanner.Features.Integrations;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace FlightScanner.Features.Alerts;

public sealed class AlertScannerService(
    IServiceScopeFactory scopeFactory,
    ILogger<AlertScannerService> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        DateTimeOffset? lastScanAt = null;
        var lastLoggedIntervalMinutes = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            var interval = await GetScanIntervalAsync(stoppingToken);
            var intervalMinutes = (int)interval.TotalMinutes;
            if (lastLoggedIntervalMinutes != intervalMinutes)
            {
                lastLoggedIntervalMinutes = intervalMinutes;
                logger.LogInformation("Alert scanner interval is {IntervalMinutes} minutes.", intervalMinutes);
            }

            if (lastScanAt is null || DateTimeOffset.UtcNow - lastScanAt >= interval)
            {
                await ScanAsync(stoppingToken);
                lastScanAt = DateTimeOffset.UtcNow;
            }

            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }
    }

    private async Task<TimeSpan> GetScanIntervalAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var setting = await db.IntegrationSettings.AsNoTracking()
                .FirstOrDefaultAsync(item => item.Kind == IntegrationKind.FlightProvider, cancellationToken);
            if (setting is null)
            {
                return TimeSpan.FromMinutes(AlertScanOptions.DefaultIntervalMinutes);
            }

            var options = JsonSerializer.Deserialize<FlightProviderOptions>(setting.SettingsJson, JsonOptions) ?? new();
            return TimeSpan.FromMinutes(AlertScanOptions.NormalizeMinutes(options.AlertScanIntervalMinutes));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not read alert scan interval. Falling back to {IntervalMinutes} minutes.", AlertScanOptions.DefaultIntervalMinutes);
            return TimeSpan.FromMinutes(AlertScanOptions.DefaultIntervalMinutes);
        }
    }

    private async Task ScanAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var search = scope.ServiceProvider.GetRequiredService<IFlightSearchService>();
            var dispatcher = scope.ServiceProvider.GetRequiredService<INotificationDispatcher>();

            var alerts = await db.PriceAlerts
                .Include(alert => alert.User)
                .Where(alert => alert.IsActive)
                .ToListAsync(cancellationToken);
            alerts = alerts
                .OrderBy(alert => alert.LastCheckedAt ?? DateTimeOffset.MinValue)
                .Take(50)
                .ToList();

            foreach (var alert in alerts)
            {
                var query = new FlightSearchQuery(
                    alert.OriginType,
                    alert.Origin,
                    alert.DestinationType,
                    alert.Destination,
                    alert.DepartFrom,
                    alert.DepartTo,
                    alert.ReturnFrom,
                    alert.ReturnTo,
                    alert.FlexibleDates,
                    alert.FlexibleYear,
                    alert.FlexibleMonth,
                    alert.FlexibleDepartureDay,
                    alert.FlexibleStayDays,
                    alert.Adults,
                    alert.Children,
                    alert.Infants,
                    alert.Cabin,
                    alert.DirectOnly,
                    alert.MaxStops,
                    alert.CheckedBags,
                    alert.OutboundTimeFromHour,
                    alert.OutboundTimeToHour,
                    alert.ReturnTimeFromHour,
                    alert.ReturnTimeToHour,
                    alert.Currency);

                var offers = await search.SearchAsync(query, cancellationToken);
                var pricedOffers = offers.Where(offer => !offer.ResultKind.Equals("Return", StringComparison.OrdinalIgnoreCase));
                var best = pricedOffers
                    .OrderBy(offer => offer.Price)
                    .FirstOrDefault();
                alert.LastCheckedAt = DateTimeOffset.UtcNow;

                if (best is null)
                {
                    continue;
                }

                alert.LastObservedPrice = best.Price;
                var maxTarget = alert.MaxTargetPrice ?? (!alert.TargetMode.Equals("Min", StringComparison.OrdinalIgnoreCase) && alert.TargetPrice > 0 ? alert.TargetPrice : null);
                var minTarget = alert.MinTargetPrice ?? (alert.TargetMode.Equals("Min", StringComparison.OrdinalIgnoreCase) && alert.TargetPrice > 0 ? alert.TargetPrice : null);
                var triggered = (maxTarget is not null && best.Price <= maxTarget.Value) ||
                    (minTarget is not null && best.Price >= minTarget.Value);
                db.FlightScanResults.Add(new FlightScanResult
                {
                    PriceAlertId = alert.Id,
                    Provider = best.Provider,
                    Airline = best.Airline,
                    FlightNumber = best.FlightNumber,
                    Price = best.Price,
                    Currency = best.Currency,
                    Stops = best.Stops,
                    BookingUrl = best.BookingUrl,
                    TriggeredAlert = triggered
                });

                //if (triggered && (alert.LastMatchedAt is null || alert.LastMatchedAt < DateTimeOffset.UtcNow.AddHours(-12)))
                if (triggered)
                {
                    alert.LastMatchedAt = DateTimeOffset.UtcNow;
                    await dispatcher.DispatchPriceMatchAsync(alert, best, cancellationToken);
                }
            }

            await db.SaveChangesAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Alert scan failed.");
        }
    }
}
