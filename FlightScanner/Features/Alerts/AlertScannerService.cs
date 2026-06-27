using FlightScanner.Data;
using FlightScanner.Features.Flights;
using Microsoft.EntityFrameworkCore;

namespace FlightScanner.Features.Alerts;

public sealed class AlertScannerService(
    IServiceScopeFactory scopeFactory,
    ILogger<AlertScannerService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await ScanAsync(stoppingToken);

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(15));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await ScanAsync(stoppingToken);
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
                    alert.Adults,
                    alert.Children,
                    alert.Infants,
                    alert.Cabin,
                    alert.DirectOnly,
                    alert.MaxStops,
                    alert.CheckedBags,
                    alert.Currency);

                var offers = await search.SearchAsync(query, cancellationToken);
                var best = offers.OrderBy(offer => offer.Price).FirstOrDefault();
                alert.LastCheckedAt = DateTimeOffset.UtcNow;

                if (best is null)
                {
                    continue;
                }

                alert.LastObservedPrice = best.Price;
                var triggered = alert.TargetMode.Equals("Min", StringComparison.OrdinalIgnoreCase)
                    ? best.Price >= alert.TargetPrice
                    : best.Price <= alert.TargetPrice;
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

                if (triggered && (alert.LastMatchedAt is null || alert.LastMatchedAt < DateTimeOffset.UtcNow.AddHours(-12)))
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
