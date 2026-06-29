using System.Text.Json;

namespace FlightScanner.Features.Integrations;

public static class EmailOptionsResolver
{
    public static EmailOptions FromConfiguration(IConfiguration configuration)
    {
        return new EmailOptions
        {
            SmtpHost = configuration["SMTP_HOST"] ?? "smtp.gmail.com",
            SmtpPort = int.TryParse(configuration["SMTP_PORT"], out var smtpPort) ? smtpPort : 587,
            UseStartTls = !bool.TryParse(configuration["SMTP_USE_STARTTLS"], out var useStartTls) || useStartTls,
            FromAddress = configuration["SMTP_FROM"] ?? "",
            UserName = configuration["SMTP_USER"] ?? "",
            Password = configuration["SMTP_PASSWORD"] ?? ""
        };
    }

    public static EmailOptions MergeWithConfigurationFallback(string? settingsJson, IConfiguration configuration)
    {
        var fallback = FromConfiguration(configuration);
        if (string.IsNullOrWhiteSpace(settingsJson))
        {
            return fallback;
        }

        try
        {
            using var document = JsonDocument.Parse(settingsJson);
            var root = document.RootElement;

            return new EmailOptions
            {
                SmtpHost = ReadString(root, nameof(EmailOptions.SmtpHost), fallback.SmtpHost),
                SmtpPort = ReadInt(root, nameof(EmailOptions.SmtpPort), fallback.SmtpPort),
                UseStartTls = ReadBool(root, nameof(EmailOptions.UseStartTls), fallback.UseStartTls),
                FromAddress = ReadString(root, nameof(EmailOptions.FromAddress), fallback.FromAddress),
                UserName = ReadString(root, nameof(EmailOptions.UserName), fallback.UserName),
                Password = ReadString(root, nameof(EmailOptions.Password), fallback.Password)
            };
        }
        catch (JsonException)
        {
            return fallback;
        }
    }

    private static string ReadString(JsonElement root, string propertyName, string fallback)
    {
        if (!TryGetProperty(root, propertyName, out var property))
        {
            return fallback;
        }

        var value = property.ValueKind == JsonValueKind.String ? property.GetString() : property.ToString();
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }

    private static int ReadInt(JsonElement root, string propertyName, int fallback)
    {
        if (!TryGetProperty(root, propertyName, out var property))
        {
            return fallback;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var number) && number > 0)
        {
            return number;
        }

        return property.ValueKind == JsonValueKind.String &&
            int.TryParse(property.GetString(), out var parsed) &&
            parsed > 0
                ? parsed
                : fallback;
    }

    private static bool ReadBool(JsonElement root, string propertyName, bool fallback)
    {
        if (!TryGetProperty(root, propertyName, out var property))
        {
            return fallback;
        }

        return property.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(property.GetString(), out var parsed) => parsed,
            _ => fallback
        };
    }

    private static bool TryGetProperty(JsonElement root, string propertyName, out JsonElement value)
    {
        if (root.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in root.EnumerateObject())
            {
                if (property.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase) ||
                    property.Name.Equals(JsonNamingPolicy.CamelCase.ConvertName(propertyName), StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }
}
