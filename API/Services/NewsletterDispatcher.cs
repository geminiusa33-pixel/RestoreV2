using System;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using API.Data;
using API.Entities;
using API.RequestHelpers;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace API.Services;

public class NewsletterDispatcher : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<NewsletterDispatcher> _logger;

    public NewsletterDispatcher(IServiceScopeFactory scopeFactory, ILogger<NewsletterDispatcher> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // Small delay to avoid racing app startup/DB init
            await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await ProcessDueNewsletters(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    // expected on shutdown
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[Newsletter] Dispatcher loop error");
                }

                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // expected on shutdown
        }
    }

    private async Task ProcessDueNewsletters(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<StoreContext>();
        var sender = scope.ServiceProvider.GetRequiredService<INewsletterSender>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        var emailOptions = scope.ServiceProvider.GetRequiredService<IOptions<EmailSettings>>();
        var frontendUrl = (emailOptions.Value.FrontendUrl ?? string.Empty).TrimEnd('/');

        var now = DateTime.UtcNow;

        var due = await context.Newsletters
            .Include(n => n.Attachments)
            .Where(n => n.Status == NewsletterStatus.Scheduled && n.ScheduledForUtc != null && n.ScheduledForUtc <= now)
            .OrderBy(n => n.ScheduledForUtc)
            .Take(5)
            .ToListAsync(ct);

        foreach (var n in due)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                // Mark as sending
                n.Status = NewsletterStatus.Sending;
                n.UpdatedAtUtc = DateTime.UtcNow;
                n.LastError = null;
                await context.SaveChangesAsync(ct);

                var recipients = await context.Users
                    .AsNoTracking()
                    .Where(u => u.NewsletterOptIn && u.Email != null && u.Email != "")
                    .ToListAsync(ct);

                n.TotalRecipients = recipients.Count;
                n.SentCount = 0;
                n.FailedCount = 0;
                n.LastError = null;
                await context.SaveChangesAsync(ct);

                if (n.TotalRecipients == 0)
                {
                    n.Status = NewsletterStatus.Failed;
                    n.LastError = "No opted-in recipients";
                    n.SentAtUtc = DateTime.UtcNow;
                    n.UpdatedAtUtc = DateTime.UtcNow;
                    await context.SaveChangesAsync(ct);
                    continue;
                }

                foreach (var user in recipients)
                {
                    ct.ThrowIfCancellationRequested();

                    if (string.IsNullOrWhiteSpace(user.Email))
                    {
                        n.FailedCount++;
                        n.LastError ??= "Recipient email is empty";
                        continue;
                    }

                    var token = await userManager.GenerateUserTokenAsync(
                        user,
                        TokenOptions.DefaultProvider,
                        NewsletterTokens.UnsubscribePurpose);

                    var unsubscribeUrl = string.IsNullOrWhiteSpace(frontendUrl)
                        ? $"/api/newsletters/unsubscribe?userId={WebUtility.UrlEncode(user.Id)}&token={WebUtility.UrlEncode(token)}"
                        : $"{frontendUrl}/api/newsletters/unsubscribe?userId={WebUtility.UrlEncode(user.Id)}&token={WebUtility.UrlEncode(token)}";

                    var htmlWithUnsubscribe = AppendUnsubscribeFooter(n.HtmlContent, unsubscribeUrl);

                    var result = await sender.SendNewsletterAsync(user.Email, n.Subject, htmlWithUnsubscribe, n.Attachments, ct);
                    if (result.Ok)
                    {
                        n.SentCount++;
                    }
                    else
                    {
                        n.FailedCount++;
                        // keep first error message for diagnostics
                        n.LastError ??= result.Error;
                    }

                    // Very small throttle to reduce rate-limit risk
                    await Task.Delay(100, ct);
                }

                n.Status = n.FailedCount == 0 ? NewsletterStatus.Sent : NewsletterStatus.Failed;
                n.SentAtUtc = DateTime.UtcNow;
                n.UpdatedAtUtc = DateTime.UtcNow;
                await context.SaveChangesAsync(ct);

                _logger.LogInformation("[Newsletter] Sent newsletter {Id} to {Total} (ok={Ok}, failed={Failed})",
                    n.Id, n.TotalRecipients, n.SentCount, n.FailedCount);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Newsletter] Failed sending newsletter {Id}", n.Id);
                n.Status = NewsletterStatus.Failed;
                n.LastError = ex.Message;
                n.UpdatedAtUtc = DateTime.UtcNow;
                await context.SaveChangesAsync(ct);
            }
        }
    }

    private static string AppendUnsubscribeFooter(string? html, string unsubscribeUrl)
    {
        html ??= string.Empty;

        if (html.IndexOf("api/newsletters/unsubscribe", StringComparison.OrdinalIgnoreCase) >= 0)
            return html;

        var footer = $"\n<hr style=\"margin:24px 0;border:none;border-top:1px solid #e5e7eb;\" />\n" +
                 $"<p style=\"font-family:Arial, sans-serif; font-size:12px; color:#6b7280; line-height:1.4;\">\n" +
                 $"    Se não quiser receber mais emails, pode <a href=\"{unsubscribeUrl}\" style=\"color:#2563eb;\">cancelar a subscrição</a>.\n" +
                 $"</p>";

        return html + footer;
    }
}
