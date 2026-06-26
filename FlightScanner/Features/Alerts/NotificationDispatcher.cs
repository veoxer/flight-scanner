using System.Net;
using System.Net.Http.Headers;
using System.Net.Mail;
using System.Text;
using System.Text.Json;
using FlightScanner.Data;
using FlightScanner.Features.Flights;
using FlightScanner.Features.Integrations;
using Microsoft.EntityFrameworkCore;

namespace FlightScanner.Features.Alerts;

public interface INotificationDispatcher
{
    Task DispatchPriceMatchAsync(PriceAlert alert, FlightOffer offer, CancellationToken cancellationToken = default);
}

public sealed class NotificationDispatcher(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    IHttpClientFactory httpClientFactory,
    ILogger<NotificationDispatcher> logger) : INotificationDispatcher
{
    public async Task DispatchPriceMatchAsync(PriceAlert alert, FlightOffer offer, CancellationToken cancellationToken = default)
    {
        var subject = $"Flight alert: {offer.Origin} to {offer.Destination} at {offer.Price:0.##} {offer.Currency}";
        var body = $"{offer.Airline} {offer.FlightNumber} is now {offer.Price:0.##} {offer.Currency} for {offer.DepartDate:yyyy-MM-dd}. Target: {alert.TargetPrice:0.##} {alert.Currency}.";

        if (alert.NotifyByEmail && !string.IsNullOrWhiteSpace(alert.User?.Email))
        {
            await SendEmailAsync(alert, alert.User.Email, subject, body, cancellationToken);
        }

        if (alert.NotifyByWhatsApp && !string.IsNullOrWhiteSpace(alert.WhatsAppTo))
        {
            await SendWhatsAppAsync(alert, alert.WhatsAppTo, subject, body, cancellationToken);
        }

        if (alert.NotifyByPush)
        {
            await RecordAttemptAsync(alert.Id, "Push", alert.UserId, subject, body, true, "Browser push subscription stored. Full background Web Push requires VAPID sender configuration.", cancellationToken);
        }
    }

    private async Task SendEmailAsync(PriceAlert alert, string recipient, string subject, string body, CancellationToken cancellationToken)
    {
        var options = await LoadOptionsAsync<EmailOptions>(IntegrationKind.Email, cancellationToken);
        if (options is null || string.IsNullOrWhiteSpace(options.SmtpHost) || string.IsNullOrWhiteSpace(options.FromAddress))
        {
            await RecordAttemptAsync(alert.Id, "Email", recipient, subject, body, false, "Email integration is not configured.", cancellationToken);
            return;
        }

        try
        {
            using var message = new MailMessage(options.FromAddress, recipient, subject, body);
            using var client = new SmtpClient(options.SmtpHost, options.SmtpPort)
            {
                EnableSsl = options.UseStartTls
            };

            if (!string.IsNullOrWhiteSpace(options.UserName))
            {
                client.Credentials = new NetworkCredential(options.UserName, options.Password);
            }

            await client.SendMailAsync(message, cancellationToken);
            await RecordAttemptAsync(alert.Id, "Email", recipient, subject, body, true, null, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Email alert failed for alert {AlertId}.", alert.Id);
            await RecordAttemptAsync(alert.Id, "Email", recipient, subject, body, false, ex.Message, cancellationToken);
        }
    }

    private async Task SendWhatsAppAsync(PriceAlert alert, string recipient, string subject, string body, CancellationToken cancellationToken)
    {
        var options = await LoadOptionsAsync<WhatsAppOptions>(IntegrationKind.WhatsApp, cancellationToken);
        if (options is null || string.IsNullOrWhiteSpace(options.EndpointUrl))
        {
            await RecordAttemptAsync(alert.Id, "WhatsApp", recipient, subject, body, false, "WhatsApp integration is not configured.", cancellationToken);
            return;
        }

        try
        {
            var request = new HttpRequestMessage(new HttpMethod(options.HttpMethod), options.EndpointUrl)
            {
                Content = new StringContent(RenderWhatsAppBody(options.BodyTemplate, recipient, body), Encoding.UTF8, "application/json")
            };

            foreach (var header in ParseHeaders(options.HeadersJson))
            {
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            var client = httpClientFactory.CreateClient("whatsapp");
            using var response = await client.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            await RecordAttemptAsync(alert.Id, "WhatsApp", recipient, subject, body, true, null, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "WhatsApp alert failed for alert {AlertId}.", alert.Id);
            await RecordAttemptAsync(alert.Id, "WhatsApp", recipient, subject, body, false, ex.Message, cancellationToken);
        }
    }

    private async Task<T?> LoadOptionsAsync<T>(IntegrationKind kind, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var setting = await db.IntegrationSettings.AsNoTracking()
            .FirstOrDefaultAsync(item => item.Kind == kind && item.Enabled, cancellationToken);

        if (setting is null)
        {
            return default;
        }

        return JsonSerializer.Deserialize<T>(setting.SettingsJson, JsonOptions());
    }

    private async Task RecordAttemptAsync(int? alertId, string channel, string recipient, string subject, string body, bool succeeded, string? error, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        db.NotificationAttempts.Add(new NotificationAttempt
        {
            PriceAlertId = alertId,
            Channel = channel,
            Recipient = recipient,
            Subject = subject,
            Body = body,
            Succeeded = succeeded,
            Error = error
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    private static string RenderWhatsAppBody(string template, string recipient, string message)
    {
        return template
            .Replace("{{to}}", recipient, StringComparison.OrdinalIgnoreCase)
            .Replace("{{message}}", message, StringComparison.OrdinalIgnoreCase);
    }

    private static Dictionary<string, string> ParseHeaders(string headersJson)
    {
        if (string.IsNullOrWhiteSpace(headersJson))
        {
            return [];
        }

        return JsonSerializer.Deserialize<Dictionary<string, string>>(headersJson, JsonOptions()) ?? [];
    }

    private static JsonSerializerOptions JsonOptions() => new(JsonSerializerDefaults.Web);
}
