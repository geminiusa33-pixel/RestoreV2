using System;
using System.Threading;
using System.Threading.Tasks;
using API.Data;
using API.Entities.OrderAggregate;
using API.RequestHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace API.Services.Invoicing;

public interface ITaxInvoiceService
{
    // Returns an official invoice PDF if issuing is enabled and succeeded; otherwise null.
    Task<InvoicePdf?> TryGetOrIssueInvoicePdfAsync(Order fullOrder, CancellationToken ct = default);
}

public class TaxInvoiceService(
    StoreContext context,
    IOptions<InvoicingSettings> invoicingOptions,
    ILogger<TaxInvoiceService> logger)
    : ITaxInvoiceService
{
    private readonly InvoicingSettings _settings = invoicingOptions.Value;

    public async Task<InvoicePdf?> TryGetOrIssueInvoicePdfAsync(Order fullOrder, CancellationToken ct = default)
    {
        if (fullOrder == null) ArgumentNullException.ThrowIfNull(fullOrder);

        // In this project we treat the official (certified) invoice as "requested" only when
        // the buyer provided a NIF. Without a NIF we keep the store receipt PDF only.
        if (string.IsNullOrWhiteSpace(fullOrder.BillingTaxId))
            return null;

        var mode = (_settings.Mode ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(mode) || mode.Equals("Disabled", StringComparison.OrdinalIgnoreCase))
            return null;

        var issueProvider = ResolveProvider(mode);
        if (issueProvider == null) return null;

        var request = new InvoiceIssueRequest(
            OrderId: fullOrder.Id,
            BuyerEmail: fullOrder.BuyerEmail,
            BillingName: fullOrder.BillingName,
            BillingTaxId: fullOrder.BillingTaxId,
            BillingLine1: fullOrder.BillingLine1,
            BillingLine2: fullOrder.BillingLine2,
            BillingCity: fullOrder.BillingCity,
            BillingState: fullOrder.BillingState,
            BillingPostalCode: fullOrder.BillingPostalCode,
            BillingCountry: fullOrder.BillingCountry,
            SubtotalCents: fullOrder.Subtotal,
            DeliveryFeeCents: fullOrder.DeliveryFee,
            DiscountCents: fullOrder.Discount,
            TotalCents: fullOrder.GetTotal(),
            OrderDateUtc: fullOrder.OrderDate);

        // If already issued, try to fetch PDF; for Fake mode we can re-issue safely (deterministic).
        if (!string.IsNullOrWhiteSpace(fullOrder.TaxInvoiceProviderId) && fullOrder.TaxInvoiceIssuedAt.HasValue)
        {
            if (issueProvider is FakeInvoicingProvider)
            {
                var re = await issueProvider.IssueInvoiceAsync(request, ct);
                return re.Succeeded ? re.Pdf : null;
            }

            var get = await issueProvider.GetInvoicePdfAsync(fullOrder.TaxInvoiceProviderId!, ct);
            if (get.Succeeded && get.Pdf != null) return get.Pdf;

            logger.LogWarning("Could not fetch invoice PDF for order {OrderId} from provider {Provider}: {Error}",
                fullOrder.Id, issueProvider.Name, get.Error);
            return null;
        }

        // Mark attempt + ensure we don't double-issue if another thread issued first.
        var tracked = await context.Orders.FirstOrDefaultAsync(o => o.Id == fullOrder.Id, ct);
        if (tracked == null) return null;

        tracked.TaxInvoiceLastAttemptAt = DateTime.UtcNow;
        await context.SaveChangesAsync(ct);

        // Re-check after saving attempt (covers concurrent webhook/admin status updates).
        if (!string.IsNullOrWhiteSpace(tracked.TaxInvoiceProviderId) && tracked.TaxInvoiceIssuedAt.HasValue)
            return null;

        IssueInvoiceResult issued;
        try
        {
            issued = await issueProvider.IssueInvoiceAsync(request, ct);
        }
        catch (Exception ex)
        {
            tracked.TaxInvoiceLastError = ex.Message;
            await context.SaveChangesAsync(ct);
            logger.LogError(ex, "Invoicing provider {Provider} threw while issuing invoice for order {OrderId}", issueProvider.Name, fullOrder.Id);
            return null;
        }

        if (!issued.Succeeded || issued.Issued == null || issued.Pdf == null)
        {
            tracked.TaxInvoiceLastError = issued.Error ?? "Unknown invoicing error";
            await context.SaveChangesAsync(ct);
            logger.LogWarning("Invoice issuance failed for order {OrderId} via {Provider}: {Error}", fullOrder.Id, issueProvider.Name, tracked.TaxInvoiceLastError);
            return null;
        }

        tracked.TaxInvoiceProvider = issued.Issued.Provider;
        tracked.TaxInvoiceProviderId = issued.Issued.ProviderInvoiceId;
        tracked.TaxInvoiceNumber = issued.Issued.InvoiceNumber;
        tracked.TaxInvoiceIssuedAt = issued.Issued.IssuedAtUtc;
        tracked.TaxInvoiceLastError = null;
        await context.SaveChangesAsync(ct);

        return issued.Pdf;
    }

    private static IInvoicingProvider? ResolveProvider(string mode)
    {
        if (mode.Equals("Fake", StringComparison.OrdinalIgnoreCase)) return new FakeInvoicingProvider();
        if (mode.Equals("InvoiceXpress", StringComparison.OrdinalIgnoreCase)) return new InvoiceXpressInvoicingProvider();

        // Placeholder for future providers.
        return null;
    }
}
