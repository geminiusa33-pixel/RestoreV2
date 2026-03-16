using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;

namespace API.Services.Invoicing;

public class FakeInvoicingProvider : IInvoicingProvider
{
    public string Name => "Fake";

    public Task<IssueInvoiceResult> IssueInvoiceAsync(InvoiceIssueRequest request, CancellationToken ct = default)
    {
        // Deterministic mock identifiers so repeated calls are effectively idempotent for the same order.
        var providerInvoiceId = $"fake-{request.OrderId}";
        var invoiceNumber = $"FT-MOCK-{request.OrderId:000000}";
        var issuedAt = DateTime.UtcNow;

        var pdf = GenerateMockInvoicePdf(request, invoiceNumber);

        var issued = new IssuedInvoice(
            Provider: Name,
            ProviderInvoiceId: providerInvoiceId,
            InvoiceNumber: invoiceNumber,
            IssuedAtUtc: issuedAt);

        return Task.FromResult(new IssueInvoiceResult(
            Succeeded: true,
            Issued: issued,
            Pdf: pdf,
            Error: null));
    }

    public Task<GetInvoicePdfResult> GetInvoicePdfAsync(string providerInvoiceId, CancellationToken ct = default)
    {
        // For the fake provider, we can't reconstruct the full content without the order.
        // Callers should prefer IssueInvoiceAsync (which returns the PDF) when using Fake mode.
        return Task.FromResult(new GetInvoicePdfResult(
            Succeeded: false,
            Pdf: null,
            Error: "Fake provider does not support GetInvoicePdf without the Order context."));
    }

    private static InvoicePdf GenerateMockInvoicePdf(InvoiceIssueRequest request, string invoiceNumber)
    {
        var doc = new PdfDocument();
        doc.Info.Title = $"Fatura (MOCK) - {invoiceNumber}";
        doc.Info.Subject = "Documento fiscal simulado (MOCK)";
        doc.Info.CreationDate = DateTime.UtcNow;

        var page = doc.AddPage();
        page.Size = PdfSharpCore.PageSize.A4;
        var gfx = XGraphics.FromPdfPage(page);

        const double margin = 40;
        var y = margin;

        var fontTitle = new XFont("Verdana", 18, XFontStyle.Bold);
        var fontH = new XFont("Verdana", 11, XFontStyle.Bold);
        var font = new XFont("Verdana", 10, XFontStyle.Regular);

        gfx.DrawString("FATURA (MOCK)", fontTitle, XBrushes.Black, new XRect(margin, y, page.Width - margin * 2, 22), XStringFormats.TopLeft);
        y += 26;

        gfx.DrawString($"Número: {invoiceNumber}", font, XBrushes.Black, new XRect(margin, y, page.Width - margin * 2, 14), XStringFormats.TopLeft);
        y += 14;
        gfx.DrawString($"Encomenda: #{request.OrderId}", font, XBrushes.Black, new XRect(margin, y, page.Width - margin * 2, 14), XStringFormats.TopLeft);
        y += 14;
        gfx.DrawString($"Data: {request.OrderDateUtc:yyyy-MM-dd}", font, XBrushes.Black, new XRect(margin, y, page.Width - margin * 2, 14), XStringFormats.TopLeft);
        y += 18;

        gfx.DrawLine(XPens.LightGray, margin, y, page.Width - margin, y);
        y += 16;

        gfx.DrawString("Dados de faturação", fontH, XBrushes.Black, new XRect(margin, y, page.Width - margin * 2, 14), XStringFormats.TopLeft);
        y += 14;

        var name = string.IsNullOrWhiteSpace(request.BillingName) ? request.BuyerEmail : request.BillingName.Trim();
        gfx.DrawString($"Nome: {name}", font, XBrushes.Black, new XRect(margin, y, page.Width - margin * 2, 14), XStringFormats.TopLeft);
        y += 14;

        if (!string.IsNullOrWhiteSpace(request.BillingTaxId))
        {
            gfx.DrawString($"NIF: {request.BillingTaxId.Trim()}", font, XBrushes.Black, new XRect(margin, y, page.Width - margin * 2, 14), XStringFormats.TopLeft);
            y += 14;
        }

        var addr = BuildAddress(request);
        if (!string.IsNullOrWhiteSpace(addr))
        {
            gfx.DrawString(addr, font, XBrushes.Black, new XRect(margin, y, page.Width - margin * 2, 42), XStringFormats.TopLeft);
            y += 46;
        }
        else
        {
            y += 10;
        }

        gfx.DrawLine(XPens.LightGray, margin, y, page.Width - margin, y);
        y += 16;

        gfx.DrawString("Totais", fontH, XBrushes.Black, new XRect(margin, y, page.Width - margin * 2, 14), XStringFormats.TopLeft);
        y += 14;

        gfx.DrawString($"Subtotal: {FormatMoney(request.SubtotalCents)}", font, XBrushes.Black, new XRect(margin, y, page.Width - margin * 2, 14), XStringFormats.TopLeft);
        y += 14;
        gfx.DrawString($"Entrega: {FormatMoney(request.DeliveryFeeCents)}", font, XBrushes.Black, new XRect(margin, y, page.Width - margin * 2, 14), XStringFormats.TopLeft);
        y += 14;
        gfx.DrawString($"Desconto: -{FormatMoney(request.DiscountCents)}", font, XBrushes.Black, new XRect(margin, y, page.Width - margin * 2, 14), XStringFormats.TopLeft);
        y += 14;
        gfx.DrawString($"Total: {FormatMoney(request.TotalCents)}", new XFont("Verdana", 11, XFontStyle.Bold), XBrushes.Black, new XRect(margin, y, page.Width - margin * 2, 14), XStringFormats.TopLeft);
        y += 18;

        gfx.DrawString("Nota: este documento é uma simulação (modo MOCK) e não substitui faturação certificada AT.",
            new XFont("Verdana", 9, XFontStyle.Italic), XBrushes.Gray, new XRect(margin, y, page.Width - margin * 2, 40), XStringFormats.TopLeft);

        using var ms = new MemoryStream();
        doc.Save(ms, closeStream: false);

        return new InvoicePdf($"fatura-mock-{request.OrderId}.pdf", "application/pdf", ms.ToArray());
    }

    private static string FormatMoney(long cents)
    {
        var euros = cents / 100.0;
        return euros.ToString("0.00", CultureInfo.InvariantCulture) + " €";
    }

    private static string? BuildAddress(InvoiceIssueRequest r)
    {
        var line1 = (r.BillingLine1 ?? string.Empty).Trim();
        var line2 = (r.BillingLine2 ?? string.Empty).Trim();
        var city = (r.BillingCity ?? string.Empty).Trim();
        var state = (r.BillingState ?? string.Empty).Trim();
        var postal = (r.BillingPostalCode ?? string.Empty).Trim();
        var country = (r.BillingCountry ?? string.Empty).Trim();

        var a = string.Empty;
        if (!string.IsNullOrWhiteSpace(line1)) a += line1;
        if (!string.IsNullOrWhiteSpace(line2)) a += (string.IsNullOrWhiteSpace(a) ? "" : "\n") + line2;

        var line3 = string.Join(" ", new[] { postal, city }.Where(s => !string.IsNullOrWhiteSpace(s)));
        if (!string.IsNullOrWhiteSpace(line3)) a += (string.IsNullOrWhiteSpace(a) ? "" : "\n") + line3;

        var line4 = string.Join(" ", new[] { state, country }.Where(s => !string.IsNullOrWhiteSpace(s)));
        if (!string.IsNullOrWhiteSpace(line4)) a += (string.IsNullOrWhiteSpace(a) ? "" : "\n") + line4;

        return string.IsNullOrWhiteSpace(a) ? null : a;
    }
}
