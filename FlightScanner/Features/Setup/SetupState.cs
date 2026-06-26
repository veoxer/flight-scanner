using FlightScanner.Data;
using FlightScanner.Features.Alerts;
using Microsoft.EntityFrameworkCore;

namespace FlightScanner.Features.Setup;

public sealed class SetupState(IDbContextFactory<ApplicationDbContext> dbFactory)
{
    public async Task<bool> IsCompleteAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.AppSettings
            .Where(setting => setting.Key == "SetupComplete")
            .Select(setting => setting.Value == "true")
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task MarkCompleteAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var setting = await db.AppSettings.FirstAsync(setting => setting.Key == "SetupComplete", cancellationToken);
        setting.Value = "true";
        setting.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }
}
