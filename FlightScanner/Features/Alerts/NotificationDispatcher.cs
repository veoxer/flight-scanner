using System.Net;
using System.Net.Http.Headers;
using System.Net.Mail;
using System.Globalization;
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
    IConfiguration configuration,
    ILogger<NotificationDispatcher> logger) : INotificationDispatcher
{
    public async Task DispatchPriceMatchAsync(PriceAlert alert, FlightOffer offer, CancellationToken cancellationToken = default)
    {
        var settings = await LoadSettingsAsync(cancellationToken);
        var culture = NormalizeCulture(alert.User?.PreferredCulture);
        var subject = BuildSubject(alert, offer, culture);
        var body = BuildAlertMessage(alert, offer, culture);

        if (settings.Email.Enabled && !string.IsNullOrWhiteSpace(alert.User?.Email))
        {
            await SendEmailAsync(alert, alert.User.Email, subject, body, settings.Email.Options, cancellationToken);
        }

        var whatsAppRecipient = FirstNonEmpty(alert.WhatsAppTo, settings.WhatsApp.Options.To, alert.User?.PhoneNumber);
        if (settings.WhatsApp.Enabled && !string.IsNullOrWhiteSpace(whatsAppRecipient))
        {
            await SendWhatsAppAsync(alert, whatsAppRecipient, subject, body, settings.WhatsApp.Options, cancellationToken);
        }

        if (settings.WebPush.Enabled)
        {
            var pushReady = !string.IsNullOrWhiteSpace(settings.WebPush.Options.PublicKey)
                && !string.IsNullOrWhiteSpace(settings.WebPush.Options.PrivateKey);
            await RecordAttemptAsync(
                alert.Id,
                "Push",
                alert.UserId,
                subject,
                body,
                pushReady,
                pushReady ? "Browser push subscription stored. Web Push sender delivery is ready for implementation." : "Mobile notifications are enabled but VAPID keys are not configured.",
                cancellationToken);
        }
    }

    private async Task SendEmailAsync(PriceAlert alert, string recipient, string subject, string body, EmailOptions options, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.SmtpHost) || string.IsNullOrWhiteSpace(options.FromAddress))
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

    private async Task SendWhatsAppAsync(PriceAlert alert, string recipient, string subject, string body, WhatsAppOptions options, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.EndpointUrl))
        {
            await RecordAttemptAsync(alert.Id, "WhatsApp", recipient, subject, body, false, "WhatsApp integration is not configured.", cancellationToken);
            return;
        }

        try
        {
            var request = new HttpRequestMessage(new HttpMethod(options.HttpMethod), options.EndpointUrl)
            {
                Content = new StringContent(RenderWhatsAppBody(options.BodyTemplate, recipient, subject, body), Encoding.UTF8, "application/json")
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

    private async Task<ReminderDeliverySettings> LoadSettingsAsync(CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var settings = await db.IntegrationSettings.AsNoTracking().ToListAsync(cancellationToken);
        var emailSetting = settings.FirstOrDefault(item => item.Kind == IntegrationKind.Email);

        return new ReminderDeliverySettings(
            new EnabledOptions<EmailOptions>(
                emailSetting?.Enabled ?? false,
                EmailOptionsResolver.MergeWithConfigurationFallback(emailSetting?.SettingsJson, configuration)),
            Read<WhatsAppOptions>(settings, IntegrationKind.WhatsApp),
            Read<WebPushOptions>(settings, IntegrationKind.WebPush));
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

    private static string RenderWhatsAppBody(string template, string recipient, string subject, string message)
    {
        template = string.IsNullOrWhiteSpace(template)
            ? "{\"to\":\"{{to}}\",\"message\":\"{{message}}\"}"
            : template;

        return template
            .Replace("{{to}}", JsonStringValue(recipient), StringComparison.OrdinalIgnoreCase)
            .Replace("{{subject}}", JsonStringValue(subject), StringComparison.OrdinalIgnoreCase)
            .Replace("{{message}}", JsonStringValue(message), StringComparison.OrdinalIgnoreCase);
    }

    private static string JsonStringValue(string value)
    {
        var serialized = JsonSerializer.Serialize(value);
        return serialized.Length >= 2 ? serialized[1..^1] : value;
    }

    private static string BuildSubject(PriceAlert alert, FlightOffer offer, string culture)
    {
        return culture switch
        {
            "fr" => $"Alerte vol : {alert.Origin} vers {alert.Destination} a {offer.Price:0.##} {offer.Currency}",
            "ar" => $"تنبيه رحلة: {alert.Origin} إلى {alert.Destination} بسعر {offer.Price:0.##} {offer.Currency}",
            _ => $"Flight alert: {alert.Origin} to {alert.Destination} at {offer.Price:0.##} {offer.Currency}"
        };
    }

    private static string BuildAlertMessage(PriceAlert alert, FlightOffer offer, string culture)
    {
        var target = TargetText(alert, culture);
        var trip = alert.ReturnFrom is null
            ? Text(culture, "One way", "Aller simple", "ذهاب فقط")
            : Text(culture, "Round trip", "Aller-retour", "ذهاب وعودة");
        var dates = alert.FlexibleDates
            ? FlexibleDatesText(alert, offer, culture)
            : alert.ReturnFrom is null
                ? FormatDate(alert.DepartFrom, culture)
                : $"{FormatDate(alert.DepartFrom, culture)} -> {FormatDate(alert.ReturnFrom.Value, culture)}";
        var travellers = TravellersText(alert, culture);
        var stops = offer.Stops <= 0
            ? Text(culture, "Direct", "Direct", "مباشر")
            : string.Format(CultureInfo.InvariantCulture, Text(culture, "{0} stop(s)", "{0} escale(s)", "{0} توقف"), offer.Stops);
        var routeWord = Text(culture, "Route", "Trajet", "المسار");
        var tripWord = Text(culture, "Trip", "Voyage", "الرحلة");
        var travellersWord = Text(culture, "Travellers", "Voyageurs", "المسافرون");
        var priceWord = Text(culture, "Price", "Prix", "السعر");
        var flightWord = Text(culture, "Flight", "Vol", "الطيران");
        var currentFare = Text(culture, "Current fare", "Tarif actuel", "السعر الحالي");
        var targetWord = Text(culture, "Your target", "Votre objectif", "السعر المستهدف");
        var airlineWord = Text(culture, "Airline", "Compagnie", "شركة الطيران");
        var stopsWord = Text(culture, "Stops", "Escales", "التوقفات");
        var travelTime = Text(culture, "Travel time", "Duree", "مدة الرحلة");
        var openText = Text(culture, "View details and booking options", "Voir les details et options de reservation", "عرض التفاصيل وخيارات الحجز");
        var intro = Text(
            culture,
            "Good news! Your reminder just matched",
            "Bonne nouvelle ! Votre rappel vient de correspondre",
            "خبر رائع! التذكير الخاص بك تحقق");

        return $"""
            ✈️ {Text(culture, "Flight Price Alert", "Alerte prix de vol", "تنبيه سعر الرحلة")}

            {intro} 🎯

            🌍 {routeWord}
            {alert.Origin} → {alert.Destination}

            📅 {tripWord}
            {trip}
            {dates}

            👤 {travellersWord}
            {travellers}

            💸 {priceWord}
            {currentFare}: {offer.Price:0.##} {offer.Currency}
            {targetWord}: {target}

            🛫 {flightWord}
            {airlineWord}: {offer.Airline}
            {stopsWord}: {stops}
            {travelTime}: {FormatDuration(offer.Duration)}

            🔎 {openText}:
            https://flight.veoxer.com/alerts
            """;
    }

    private static string TargetText(PriceAlert alert, string culture)
    {
        var maxTarget = alert.MaxTargetPrice ?? (!alert.TargetMode.Equals("Min", StringComparison.OrdinalIgnoreCase) && alert.TargetPrice > 0 ? alert.TargetPrice : null);
        var minTarget = alert.MinTargetPrice ?? (alert.TargetMode.Equals("Min", StringComparison.OrdinalIgnoreCase) && alert.TargetPrice > 0 ? alert.TargetPrice : null);
        var parts = new List<string>();
        if (maxTarget is { } max)
        {
            parts.Add($"{Text(culture, "under", "sous", "أقل من")} {max:0.##} {alert.Currency}");
        }

        if (minTarget is { } min)
        {
            parts.Add($"{Text(culture, "over", "au-dessus de", "أكثر من")} {min:0.##} {alert.Currency}");
        }

        return parts.Count == 0 ? $"{alert.TargetPrice:0.##} {alert.Currency}" : string.Join(" / ", parts);
    }

    private static string TravellersText(PriceAlert alert, string culture)
    {
        var adults = string.Format(
            CultureInfo.InvariantCulture,
            Text(culture, "{0} adult", "{0} adulte", "{0} بالغ"),
            Math.Max(1, alert.Adults));
        var children = alert.Children > 0
            ? " · " + string.Format(CultureInfo.InvariantCulture, Text(culture, "{0} child", "{0} enfant", "{0} طفل"), alert.Children)
            : "";
        return $"{adults}{children} · {alert.Cabin}";
    }

    private static string FlexibleDatesText(PriceAlert alert, FlightOffer offer, string culture)
    {
        var cultureInfo = CultureInfoFor(culture);
        var month = alert.FlexibleMonth is >= 1 and <= 12
            ? cultureInfo.DateTimeFormat.GetMonthName(alert.FlexibleMonth.Value)
            : Text(culture, "selected month", "mois choisi", "الشهر المحدد");
        var weekday = alert.FlexibleDepartureDay is { } departureDay
            ? cultureInfo.DateTimeFormat.GetDayName(departureDay)
            : Text(culture, "selected weekday", "jour choisi", "اليوم المحدد");
        var matched = offer.ReturnDate is { } returnDate
            ? $"{FormatDate(offer.DepartDate, culture)} -> {FormatDate(returnDate, culture)}"
            : FormatDate(offer.DepartDate, culture);

        if (alert.ReturnFrom is null)
        {
            return Text(
                culture,
                $"Flexible: {month} {alert.FlexibleYear}, {weekday}\nMatched: {matched}",
                $"Flexible : {month} {alert.FlexibleYear}, {weekday}\nCorrespondance : {matched}",
                $"مرن: {month} {alert.FlexibleYear}، {weekday}\nالمطابقة: {matched}");
        }

        var stay = alert.FlexibleStayDays ?? 1;

        return Text(
            culture,
            $"Flexible: {month} {alert.FlexibleYear}, {weekday}, {stay} day stay\nMatched: {matched}",
            $"Flexible : {month} {alert.FlexibleYear}, {weekday}, sejour de {stay} jours\nCorrespondance : {matched}",
            $"مرن: {month} {alert.FlexibleYear}، {weekday}، إقامة {stay} أيام\nالمطابقة: {matched}");
    }

    private static string FormatDate(DateOnly date, string culture)
    {
        return date.ToString("dd/MM/yyyy", CultureInfoFor(culture));
    }

    private static CultureInfo CultureInfoFor(string culture)
    {
        return culture switch
        {
            "fr" => CultureInfo.GetCultureInfo("fr-FR"),
            "ar" => CultureInfo.GetCultureInfo("ar-MA"),
            _ => CultureInfo.GetCultureInfo("en-US")
        };
    }

    private static string FormatDuration(TimeSpan duration)
    {
        return duration <= TimeSpan.Zero ? "N/A" : $"{(int)duration.TotalHours} h {duration.Minutes:00} m";
    }

    private static string NormalizeCulture(string? culture)
    {
        return culture?.Equals("fr", StringComparison.OrdinalIgnoreCase) == true
            ? "fr"
            : culture?.Equals("ar", StringComparison.OrdinalIgnoreCase) == true
                ? "ar"
                : "en";
    }

    private static string Text(string culture, string en, string fr, string ar)
    {
        return culture switch
        {
            "fr" => fr,
            "ar" => ar,
            _ => en
        };
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? "";
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

    private static EnabledOptions<T> Read<T>(IEnumerable<IntegrationSetting> settings, IntegrationKind kind) where T : new()
    {
        var setting = settings.FirstOrDefault(item => item.Kind == kind);
        if (setting is null)
        {
            return new EnabledOptions<T>(false, new T());
        }

        return new EnabledOptions<T>(
            setting.Enabled,
            JsonSerializer.Deserialize<T>(setting.SettingsJson, JsonOptions()) ?? new T());
    }

    private sealed record ReminderDeliverySettings(
        EnabledOptions<EmailOptions> Email,
        EnabledOptions<WhatsAppOptions> WhatsApp,
        EnabledOptions<WebPushOptions> WebPush);

    private sealed record EnabledOptions<T>(bool Enabled, T Options);
}
