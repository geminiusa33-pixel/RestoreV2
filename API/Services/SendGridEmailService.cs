using System.Threading.Tasks;
using API.RequestHelpers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SendGrid;
using SendGrid.Helpers.Mail;
using System;
using System.Collections.Generic;

namespace API.Services;

public class SendGridEmailService : IEmailService
{
    private readonly EmailSettings _settings;
    private readonly ILogger<SendGridEmailService> _logger;

    public SendGridEmailService(IOptions<EmailSettings> options, ILogger<SendGridEmailService> logger)
    {
        _settings = options.Value;
        _logger = logger;
    }

    public async Task<bool> SendEmailAsync(string toEmail, string subject, string htmlContent, string? replyToEmail = null)
    {
        // ReplyTo is only supported on the non-attachment path via this wrapper.
        // If you need ReplyTo with attachments, extend SendEmailWithAttachmentsAsync signature.
        if (string.IsNullOrWhiteSpace(_settings.SendGridApiKey) || string.IsNullOrWhiteSpace(_settings.FromEmail))
        {
            // Let the underlying method log configuration issues.
        }

        // Build the message here so we can set ReplyTo.
        if (string.IsNullOrWhiteSpace(_settings.SendGridApiKey))
        {
            _logger.LogWarning("SendGrid email not sent: EmailSettings.SendGridApiKey is not configured.");
            return false;
        }

        if (string.IsNullOrWhiteSpace(_settings.FromEmail))
        {
            _logger.LogWarning("SendGrid email not sent: EmailSettings.FromEmail is not configured.");
            return false;
        }

        htmlContent = EmailTemplate.TryWrap(_settings, htmlContent, preheader: subject);

        var client = new SendGridClient(_settings.SendGridApiKey);
        var from = new EmailAddress(_settings.FromEmail, _settings.FromName);
        var to = new EmailAddress(toEmail);
        var plain = StripHtml(htmlContent);
        var msg = MailHelper.CreateSingleEmail(from, to, subject, plain, htmlContent);

        if (!string.IsNullOrWhiteSpace(replyToEmail))
        {
            msg.ReplyTo = new EmailAddress(replyToEmail);
        }

        if (_settings.UseSandbox)
        {
            msg.MailSettings = new MailSettings { SandboxMode = new SandboxMode { Enable = true } };
            _logger.LogWarning("SendGrid sandbox mode is enabled (EmailSettings.UseSandbox=true). Email will not be delivered.");
        }

        try
        {
            var response = await client.SendEmailAsync(msg);
            string body = string.Empty;
            try
            {
                if (response.Body != null)
                {
                    body = await response.Body.ReadAsStringAsync();
                }
            }
            catch
            {
                // ignore body read failures
            }

            _logger.LogInformation("SendGrid send finished. Status: {StatusCode}. Body: {Body}", response.StatusCode, body);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SendGrid exception sending email");
            return false;
        }
    }

    public async Task<bool> SendEmailWithAttachmentsAsync(
        string toEmail,
        string subject,
        string htmlContent,
        IReadOnlyList<EmailAttachment> attachments)
    {
        if (string.IsNullOrWhiteSpace(_settings.SendGridApiKey))
        {
            _logger.LogWarning("SendGrid email not sent: EmailSettings.SendGridApiKey is not configured.");
            return false;
        }

        if (string.IsNullOrWhiteSpace(_settings.FromEmail))
        {
            _logger.LogWarning("SendGrid email not sent: EmailSettings.FromEmail is not configured.");
            return false;
        }

        htmlContent = EmailTemplate.TryWrap(_settings, htmlContent, preheader: subject);

        var client = new SendGridClient(_settings.SendGridApiKey);
        var from = new EmailAddress(_settings.FromEmail, _settings.FromName);
        var to = new EmailAddress(toEmail);
        var plain = StripHtml(htmlContent);
        var msg = MailHelper.CreateSingleEmail(from, to, subject, plain, htmlContent);

        // Attach files (SendGrid expects base64 content)
        if (attachments != null)
        {
            foreach (var a in attachments)
            {
                try
                {
                    if (a?.Content == null || a.Content.Length == 0) continue;
                    // Keep memory bounded: reject very large attachments
                    if (a.Content.Length > 10 * 1024 * 1024) continue;

                    var name = string.IsNullOrWhiteSpace(a.FileName) ? "attachment" : a.FileName;
                    var type = string.IsNullOrWhiteSpace(a.ContentType) ? "application/octet-stream" : a.ContentType;
                    var base64 = Convert.ToBase64String(a.Content);

                    msg.AddAttachment(name, base64, type, "attachment");
                }
                catch
                {
                    // ignore individual attachment errors; still try to deliver
                }
            }
        }

        // If configured for sandbox/dev mode, enable SendGrid sandbox (won't deliver)
        if (_settings.UseSandbox)
        {
            msg.MailSettings = new MailSettings { SandboxMode = new SandboxMode { Enable = true } };
            _logger.LogWarning("SendGrid sandbox mode is enabled (EmailSettings.UseSandbox=true). Email will not be delivered.");
        }

        try
        {
            var response = await client.SendEmailAsync(msg);
            string body = string.Empty;
            try
            {
                if (response.Body != null)
                {
                    body = await response.Body.ReadAsStringAsync();
                }
            }
            catch
            {
                // ignore body read failures
            }

            _logger.LogInformation("SendGrid send finished. Status: {StatusCode}. Body: {Body}", response.StatusCode, body);
            return response.IsSuccessStatusCode;
        }
        catch (System.Exception ex)
        {
            _logger.LogError(ex, "SendGrid exception sending email");
            return false;
        }
    }

    private static string StripHtml(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return string.Empty;
        // Very small utility to produce a plain-text fallback
        try
        {
            var withoutTags = System.Text.RegularExpressions.Regex.Replace(html, "<[^>]+>", string.Empty);
            return System.Net.WebUtility.HtmlDecode(withoutTags);
        }
        catch
        {
            return html ?? string.Empty;
        }
    }
}
