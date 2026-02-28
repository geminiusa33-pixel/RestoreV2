using System;
using System.Threading;
using System.Threading.Tasks;

namespace API.Services.Invoicing;

// Stub provider: intentionally does NOT call external APIs yet.
// It exists so the app can be configured to a "real" provider name and
// exercise error/fallback paths while the store receipt PDF remains complete.
public sealed class InvoiceXpressInvoicingProvider : IInvoicingProvider
{
    public string Name => "InvoiceXpress";

    public Task<IssueInvoiceResult> IssueInvoiceAsync(InvoiceIssueRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return Task.FromResult(new IssueInvoiceResult(
            Succeeded: false,
            Issued: null,
            Pdf: null,
            Error: "InvoiceXpress provider stub: not implemented yet. Configure a real integration (API key/account) and implement API calls before enabling this mode."));
    }

    public Task<GetInvoicePdfResult> GetInvoicePdfAsync(string providerInvoiceId, CancellationToken ct = default)
    {
        return Task.FromResult(new GetInvoicePdfResult(
            Succeeded: false,
            Pdf: null,
            Error: "InvoiceXpress provider stub: not implemented yet."));
    }
}
