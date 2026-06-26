using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using FlightScanner.Features.Alerts;
using FlightScanner.Features.Flights;
using FlightScanner.Features.Integrations;

namespace FlightScanner.Data;

public class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : IdentityDbContext<ApplicationUser>(options)
{
    public DbSet<AppSetting> AppSettings => Set<AppSetting>();
    public DbSet<FlightLocation> FlightLocations => Set<FlightLocation>();
    public DbSet<PriceAlert> PriceAlerts => Set<PriceAlert>();
    public DbSet<FlightScanResult> FlightScanResults => Set<FlightScanResult>();
    public DbSet<IntegrationSetting> IntegrationSettings => Set<IntegrationSetting>();
    public DbSet<NotificationAttempt> NotificationAttempts => Set<NotificationAttempt>();
    public DbSet<PushSubscriptionRecord> PushSubscriptions => Set<PushSubscriptionRecord>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<AppSetting>()
            .HasIndex(setting => setting.Key)
            .IsUnique();

        builder.Entity<FlightLocation>()
            .HasIndex(location => new { location.Type, location.Code })
            .IsUnique();

        builder.Entity<PriceAlert>()
            .HasOne(alert => alert.User)
            .WithMany()
            .HasForeignKey(alert => alert.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Entity<PriceAlert>()
            .Property(alert => alert.TargetPrice)
            .HasPrecision(10, 2);

        builder.Entity<FlightScanResult>()
            .HasOne(result => result.PriceAlert)
            .WithMany(alert => alert.ScanResults)
            .HasForeignKey(result => result.PriceAlertId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Entity<FlightScanResult>()
            .Property(result => result.Price)
            .HasPrecision(10, 2);

        builder.Entity<IntegrationSetting>()
            .HasIndex(setting => setting.Kind)
            .IsUnique();

        builder.Entity<PushSubscriptionRecord>()
            .HasIndex(subscription => subscription.Endpoint)
            .IsUnique();
    }
}
