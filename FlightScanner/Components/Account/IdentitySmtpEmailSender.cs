using System.Net;
using System.Net.Mail;
using System.Globalization;
using FlightScanner.Data;
using FlightScanner.Features.Integrations;
using FlightScanner.Features.Localization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace FlightScanner.Components.Account;

internal sealed class IdentitySmtpEmailSender(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    IConfiguration configuration,
    ILogger<IdentitySmtpEmailSender> logger) : IEmailSender<ApplicationUser>
{
    public Task SendConfirmationLinkAsync(ApplicationUser user, string email, string confirmationLink) =>
        SendIdentityEmailAsync(user,
            email,
            "EmailConfirmSubject",
            BuildLinkEmail(user, "EmailConfirmTitle", "EmailConfirmIntro", "EmailConfirmButton", confirmationLink));

    public Task SendPasswordResetLinkAsync(ApplicationUser user, string email, string resetLink) =>
        SendIdentityEmailAsync(user,
            email,
            "EmailResetSubject",
            BuildLinkEmail(user, "EmailResetTitle", "EmailResetIntro", "EmailResetButton", resetLink));

    public Task SendPasswordResetCodeAsync(ApplicationUser user, string email, string resetCode) =>
        SendIdentityEmailAsync(user,
            email,
            "EmailResetSubject",
            BuildCodeEmail(user, resetCode));

    private async Task SendIdentityEmailAsync(ApplicationUser user, string recipient, string subjectKey, string htmlBody)
    {
        var options = await LoadEmailOptionsAsync();
        if (options is null)
        {
            logger.LogWarning("Identity email was not sent because SMTP settings are incomplete.");
            return;
        }

        try
        {
            using var message = new MailMessage(options.FromAddress, recipient)
            {
                Subject = UiText.T(subjectKey, ResolveCulture(user)),
                Body = htmlBody,
                IsBodyHtml = true
            };

            using var client = new SmtpClient(options.SmtpHost, options.SmtpPort)
            {
                EnableSsl = options.UseStartTls
            };

            if (!string.IsNullOrWhiteSpace(options.UserName))
            {
                client.Credentials = new NetworkCredential(options.UserName, options.Password);
            }

            await client.SendMailAsync(message);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Identity email failed for recipient {Recipient}.", recipient);
        }
    }

    private async Task<EmailOptions?> LoadEmailOptionsAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var setting = await db.IntegrationSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Kind == IntegrationKind.Email);

        var options = EmailOptionsResolver.MergeWithConfigurationFallback(setting?.SettingsJson, configuration);
        if (string.IsNullOrWhiteSpace(options.SmtpHost) ||
            string.IsNullOrWhiteSpace(options.FromAddress))
        {
            return null;
        }

        return options;
    }

    private static string BuildLinkEmail(ApplicationUser user, string titleKey, string introKey, string buttonKey, string url)
    {
        var culture = ResolveCulture(user);
        var safeUrl = WebUtility.HtmlEncode(WebUtility.HtmlDecode(url));
        return BuildEmailShell(
            culture,
            UiText.T(titleKey, culture),
            UiText.T(introKey, culture),
            $"""
            <a href="{safeUrl}" style="display:inline-block;background:#0f766e;color:#ffffff;text-decoration:none;font-weight:800;font-size:16px;line-height:1;padding:16px 22px;border-radius:12px;box-shadow:0 14px 26px rgba(15,118,110,.22);">
                {WebUtility.HtmlEncode(UiText.T(buttonKey, culture))}
            </a>
            <p style="margin:22px 0 0;color:#64748b;font-size:13px;line-height:1.6;">{WebUtility.HtmlEncode(UiText.T("EmailOpenLinkFallback", culture))}</p>
            <p style="margin:8px 0 0;word-break:break-all;color:#0f766e;font-size:13px;line-height:1.6;">{safeUrl}</p>
            """);
    }

    private static string BuildCodeEmail(ApplicationUser user, string resetCode)
    {
        var culture = ResolveCulture(user);
        return BuildEmailShell(
            culture,
            UiText.T("EmailResetTitle", culture),
            UiText.T("EmailResetIntro", culture),
            $"""
            <p style="margin:0 0 12px;color:#334155;font-size:15px;line-height:1.7;">{WebUtility.HtmlEncode(UiText.T("EmailResetCodeIntro", culture))}</p>
            <div style="display:inline-block;background:#ecfeff;color:#155e75;font-size:26px;font-weight:900;letter-spacing:4px;padding:16px 20px;border-radius:14px;border:1px solid #a5f3fc;">
                {WebUtility.HtmlEncode(resetCode)}
            </div>
            """);
    }

    private static string BuildEmailShell(string culture, string title, string intro, string actionHtml)
    {
        var direction = culture == "ar" ? "rtl" : "ltr";
        var textAlign = culture == "ar" ? "right" : "left";
        return $$"""
        <!doctype html>
        <html lang="{{culture}}" dir="{{direction}}">
        <body style="margin:0;padding:0;background:#edf6f9;font-family:Arial,'Segoe UI',sans-serif;color:#0f172a;">
            <div style="display:none;max-height:0;overflow:hidden;color:transparent;">{{WebUtility.HtmlEncode(intro)}}</div>
            <table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="background:#edf6f9;padding:28px 12px;">
                <tr>
                    <td align="center">
                        <table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="max-width:620px;background:#ffffff;border-radius:24px;overflow:hidden;border:1px solid #dbeafe;box-shadow:0 22px 70px rgba(15,23,42,.12);">
                            <tr>
                                <td style="background:linear-gradient(135deg,#0f766e,#0ea5e9);padding:28px 30px;text-align:{{textAlign}};">
                                    <div style="display:inline-block;background:rgba(255,255,255,.16);color:#ffffff;border:1px solid rgba(255,255,255,.28);border-radius:999px;padding:7px 12px;font-size:12px;font-weight:800;letter-spacing:.08em;text-transform:uppercase;">Flight Scanner</div>
                                    <h1 style="margin:18px 0 0;color:#ffffff;font-size:30px;line-height:1.15;font-weight:900;">{{WebUtility.HtmlEncode(title)}}</h1>
                                </td>
                            </tr>
                            <tr>
                                <td style="padding:30px;text-align:{{textAlign}};">
                                    <p style="margin:0 0 24px;color:#334155;font-size:16px;line-height:1.75;">{{WebUtility.HtmlEncode(intro)}}</p>
                                    {{actionHtml}}
                                    <p style="margin:26px 0 0;color:#64748b;font-size:14px;line-height:1.7;">{{WebUtility.HtmlEncode(UiText.T("EmailIgnoreNote", culture))}}</p>
                                </td>
                            </tr>
                            <tr>
                                <td style="background:#f8fafc;padding:18px 30px;text-align:{{textAlign}};color:#64748b;font-size:13px;line-height:1.6;border-top:1px solid #e2e8f0;">
                                    {{WebUtility.HtmlEncode(UiText.T("EmailFooter", culture))}}
                                </td>
                            </tr>
                        </table>
                    </td>
                </tr>
            </table>
        </body>
        </html>
        """;
    }

    private static string ResolveCulture(ApplicationUser user)
    {
        var current = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        if (current is "fr" or "ar" or "en")
        {
            return current;
        }

        return user.PreferredCulture is "fr" or "ar" or "en" ? user.PreferredCulture : "en";
    }
}
