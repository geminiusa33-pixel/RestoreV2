using System.Threading;
using System.Threading.Tasks;

namespace API.Services.Invoicing;

public interface IInvoicingProvider
{
    string Name { get; }

    Task<IssueInvoiceResult> IssueInvoiceAsync(InvoiceIssueRequest request, CancellationToken ct = default);

    Task<GetInvoicePdfResult> GetInvoicePdfAsync(string providerInvoiceId, CancellationToken ct = default);
}
