using System.ComponentModel.DataAnnotations;

namespace FlightScanner.Features.Integrations;

public enum IntegrationKind
{
    FlightProvider = 0,
    Email = 1,
    WhatsApp = 2,
    WebPush = 3
}

public sealed class IntegrationSetting
{
    public int Id { get; set; }
    public IntegrationKind Kind { get; set; }
    public bool Enabled { get; set; }
    public string SettingsJson { get; set; } = "{}";
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class PushSubscriptionRecord
{
    public int Id { get; set; }
    public string UserId { get; set; } = "";
    [MaxLength(2048)]
    public string Endpoint { get; set; } = "";
    public string P256dh { get; set; } = "";
    public string Auth { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastSeenAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class FlightProviderOptions
{
    public string EndpointUrl { get; set; } = "";
    public string HttpMethod { get; set; } = "POST";
    public string HeadersJson { get; set; } = "{}";
    public string BodyTemplate { get; set; } = "";
    public string PriceJsonPath { get; set; } = "$.offers[0].price";
    public string CurrencyJsonPath { get; set; } = "$.offers[0].currency";
    public string UrlJsonPath { get; set; } = "$.offers[0].url";
}

public sealed class EmailOptions
{
    public string SmtpHost { get; set; } = "smtp.gmail.com";
    public int SmtpPort { get; set; } = 587;
    public bool UseStartTls { get; set; } = true;
    public string FromAddress { get; set; } = "";
    public string UserName { get; set; } = "";
    public string Password { get; set; } = "";
}

public sealed class WhatsAppOptions
{
    public string EndpointUrl { get; set; } = "";
    public string HttpMethod { get; set; } = "POST";
    public string HeadersJson { get; set; } = "{}";
    public string BodyTemplate { get; set; } = "{\"to\":\"{{to}}\",\"message\":\"{{message}}\"}";
}

public sealed class WebPushOptions
{
    public string PublicKey { get; set; } = "";
    public string PrivateKey { get; set; } = "";
    public string Subject { get; set; } = "mailto:admin@example.com";
}
