using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using API.Data;
using API.Entities;
using API.Entities.OrderAggregate;
using Microsoft.EntityFrameworkCore;
using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;

namespace API.Services;

public interface IInvoicePdfService
{
    Task<byte[]> GenerateReceiptPdfAsync(Order order, CancellationToken ct = default);
    Task<byte[]> GenerateCancellationPdfAsync(Order order, CancellationToken ct = default);
}

public class InvoicePdfService(StoreContext context) : IInvoicePdfService
{
    public async Task<byte[]> GenerateReceiptPdfAsync(Order order, CancellationToken ct = default)
    {
        var contact = await context.Contacts
            .AsNoTracking()
            .OrderByDescending(c => c.UpdatedAt)
            .FirstOrDefaultAsync(ct);

        var sellerName = string.IsNullOrWhiteSpace(contact?.CompanyName) ? "Restore" : contact!.CompanyName!.Trim();
        var sellerTaxId = contact?.TaxId?.Trim();
        var sellerAddress = BuildSellerAddress(contact);
        var sellerEmail = contact?.Email?.Trim();

        var doc = new PdfDocument();
        doc.Info.Title = $"Recibo - Encomenda #{order.Id}";
        doc.Info.Subject = "Recibo (MVP)";
        doc.Info.CreationDate = DateTime.UtcNow;

        var page = doc.AddPage();
        page.Size = PdfSharpCore.PageSize.A4;
        var gfx = XGraphics.FromPdfPage(page);

        const double margin = 40;
        var y = margin;

        var fontTitle = new XFont("Verdana", 18, XFontStyle.Bold);
        var fontH = new XFont("Verdana", 11, XFontStyle.Bold);
        var font = new XFont("Verdana", 10, XFontStyle.Regular);
        var fontSmall = new XFont("Verdana", 9, XFontStyle.Regular);

        // Header
        gfx.DrawString(sellerName, fontH, XBrushes.Black, new XRect(margin, y, page.Width - margin * 2, 16), XStringFormats.TopLeft);
        y += 16;
        if (!string.IsNullOrWhiteSpace(sellerTaxId))
        {
            gfx.DrawString($"NIF: {sellerTaxId}", font, XBrushes.Black, new XRect(margin, y, page.Width - margin * 2, 14), XStringFormats.TopLeft);
            y += 14;
        }
        if (!string.IsNullOrWhiteSpace(sellerAddress))
        {
            gfx.DrawString(sellerAddress, font, XBrushes.Black, new XRect(margin, y, page.Width - margin * 2, 28), XStringFormats.TopLeft);
            y += 28;
        }
        if (!string.IsNullOrWhiteSpace(sellerEmail))
        {
            gfx.DrawString(sellerEmail, font, XBrushes.Black, new XRect(margin, y, page.Width - margin * 2, 14), XStringFormats.TopLeft);
            y += 14;
        }

        y += 10;
        gfx.DrawLine(XPens.LightGray, margin, y, page.Width - margin, y);
        y += 16;

        gfx.DrawString("Recibo", fontTitle, XBrushes.Black, new XRect(margin, y, page.Width - margin * 2, 22), XStringFormats.TopLeft);
        y += 26;

        gfx.DrawString($"Encomenda: #{order.Id}", font, XBrushes.Black, new XRect(margin, y, page.Width - margin * 2, 14), XStringFormats.TopLeft);
        y += 14;
        gfx.DrawString($"Data: {order.OrderDate:yyyy-MM-dd}", font, XBrushes.Black, new XRect(margin, y, page.Width - margin * 2, 14), XStringFormats.TopLeft);
        y += 14;
        gfx.DrawString($"Cliente: {order.BuyerEmail}", font, XBrushes.Black, new XRect(margin, y, page.Width - margin * 2, 14), XStringFormats.TopLeft);
        y += 18;

        var customerTaxId = (order.BillingTaxId ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(customerTaxId))
        {
            gfx.DrawString($"NIF do cliente: {customerTaxId}", font, XBrushes.Black, new XRect(margin, y, page.Width - margin * 2, 14), XStringFormats.TopLeft);
            y += 18;
        }

        // Shipping address
        gfx.DrawString("Morada de envio", fontH, XBrushes.Black, new XRect(margin, y, page.Width - margin * 2, 14), XStringFormats.TopLeft);
        y += 14;
        var ship = FormatShippingAddress(order.ShippingAddress);
        gfx.DrawString(ship, font, XBrushes.Black, new XRect(margin, y, page.Width - margin * 2, 40), XStringFormats.TopLeft);
        y += 44;

        // Items table
        gfx.DrawString("Artigos", fontH, XBrushes.Black, new XRect(margin, y, page.Width - margin * 2, 14), XStringFormats.TopLeft);
        y += 16;

        var xName = margin;
        var xQty = page.Width - margin - 180;
        var xPrice = page.Width - margin - 120;
        var xTotal = page.Width - margin - 60;

        gfx.DrawString("Nome", fontSmall, XBrushes.Black, new XRect(xName, y, xQty - xName - 10, 12), XStringFormats.TopLeft);
        gfx.DrawString("Qtd", fontSmall, XBrushes.Black, new XRect(xQty, y, 40, 12), XStringFormats.TopLeft);
        gfx.DrawString("Preço", fontSmall, XBrushes.Black, new XRect(xPrice, y, 60, 12), XStringFormats.TopLeft);
        gfx.DrawString("Total", fontSmall, XBrushes.Black, new XRect(xTotal, y, 60, 12), XStringFormats.TopLeft);
        y += 12;
        gfx.DrawLine(XPens.LightGray, margin, y, page.Width - margin, y);
        y += 10;

        foreach (var item in order.OrderItems)
        {
            if (y > page.Height - margin - 120)
            {
                page = doc.AddPage();
                page.Size = PdfSharpCore.PageSize.A4;
                gfx = XGraphics.FromPdfPage(page);
                y = margin;
            }

            var lineTotal = item.Price * item.Quantity;
            var name = item.ItemOrdered?.Name ?? "";

            gfx.DrawString(TrimToFit(gfx, name, font, xQty - xName - 10), font, XBrushes.Black, new XRect(xName, y, xQty - xName - 10, 14), XStringFormats.TopLeft);
            gfx.DrawString(item.Quantity.ToString(CultureInfo.InvariantCulture), font, XBrushes.Black, new XRect(xQty, y, 40, 14), XStringFormats.TopLeft);
            gfx.DrawString(FormatMoney(item.Price), font, XBrushes.Black, new XRect(xPrice, y, 60, 14), XStringFormats.TopLeft);
            gfx.DrawString(FormatMoney(lineTotal), font, XBrushes.Black, new XRect(xTotal, y, 60, 14), XStringFormats.TopLeft);

            y += 16;
        }

        y += 6;
        gfx.DrawLine(XPens.LightGray, margin, y, page.Width - margin, y);
        y += 14;

        // Summary
        DrawSummaryLine(gfx, page, font, "Subtotal", FormatMoney(order.Subtotal), ref y, margin);
        var discount = order.ProductDiscount + order.Discount;
        DrawSummaryLine(gfx, page, font, "Desconto", FormatMoney(discount), ref y, margin, isDiscount: true);
        DrawSummaryLine(gfx, page, font, "Taxa de entrega", FormatMoney(order.DeliveryFee), ref y, margin);
        y += 6;
        gfx.DrawLine(XPens.LightGray, page.Width - margin - 200, y, page.Width - margin, y);
        y += 10;
        DrawSummaryLine(gfx, page, new XFont("Verdana", 11, XFontStyle.Bold), "Total", FormatMoney(order.GetTotal()), ref y, margin);

        using var ms = new MemoryStream();
        doc.Save(ms, closeStream: false);
        return ms.ToArray();
    }

    public async Task<byte[]> GenerateCancellationPdfAsync(Order order, CancellationToken ct = default)
    {
        var contact = await context.Contacts
            .AsNoTracking()
            .OrderByDescending(c => c.UpdatedAt)
            .FirstOrDefaultAsync(ct);

        var sellerName = string.IsNullOrWhiteSpace(contact?.CompanyName) ? "Restore" : contact!.CompanyName!.Trim();
        var sellerTaxId = contact?.TaxId?.Trim();
        var sellerAddress = BuildSellerAddress(contact);
        var sellerEmail = contact?.Email?.Trim();

        var doc = new PdfDocument();
        doc.Info.Title = $"Anulação - Encomenda #{order.Id}";
        doc.Info.Subject = "Confirmação de anulação";
        doc.Info.CreationDate = DateTime.UtcNow;

        var page = doc.AddPage();
        page.Size = PdfSharpCore.PageSize.A4;
        var gfx = XGraphics.FromPdfPage(page);

        const double margin = 40;
        var y = margin;

        var fontTitle = new XFont("Verdana", 18, XFontStyle.Bold);
        var fontH = new XFont("Verdana", 11, XFontStyle.Bold);
        var font = new XFont("Verdana", 10, XFontStyle.Regular);

        // Header
        gfx.DrawString(sellerName, fontH, XBrushes.Black, new XRect(margin, y, page.Width - margin * 2, 16), XStringFormats.TopLeft);
        y += 16;
        if (!string.IsNullOrWhiteSpace(sellerTaxId))
        {
            gfx.DrawString($"NIF: {sellerTaxId}", font, XBrushes.Black, new XRect(margin, y, page.Width - margin * 2, 14), XStringFormats.TopLeft);
            y += 14;
        }
        if (!string.IsNullOrWhiteSpace(sellerAddress))
        {
            gfx.DrawString(sellerAddress, font, XBrushes.Black, new XRect(margin, y, page.Width - margin * 2, 28), XStringFormats.TopLeft);
            y += 28;
        }
        if (!string.IsNullOrWhiteSpace(sellerEmail))
        {
            gfx.DrawString(sellerEmail, font, XBrushes.Black, new XRect(margin, y, page.Width - margin * 2, 14), XStringFormats.TopLeft);
            y += 14;
        }

        y += 10;
        gfx.DrawLine(XPens.LightGray, margin, y, page.Width - margin, y);
        y += 16;

        gfx.DrawString("Confirmação de Anulação", fontTitle, XBrushes.Black, new XRect(margin, y, page.Width - margin * 2, 22), XStringFormats.TopLeft);
        y += 26;

        gfx.DrawString($"Encomenda: #{order.Id}", font, XBrushes.Black, new XRect(margin, y, page.Width - margin * 2, 14), XStringFormats.TopLeft);
        y += 14;
        gfx.DrawString($"Data da encomenda: {order.OrderDate:yyyy-MM-dd}", font, XBrushes.Black, new XRect(margin, y, page.Width - margin * 2, 14), XStringFormats.TopLeft);
        y += 14;
        gfx.DrawString($"Cliente: {order.BuyerEmail}", font, XBrushes.Black, new XRect(margin, y, page.Width - margin * 2, 14), XStringFormats.TopLeft);
        y += 14;
        var cancelledAt = order.CancelledAt ?? DateTime.UtcNow;
        gfx.DrawString($"Data de anulação: {cancelledAt:yyyy-MM-dd}", font, XBrushes.Black, new XRect(margin, y, page.Width - margin * 2, 14), XStringFormats.TopLeft);
        y += 18;

        gfx.DrawString($"Total: {FormatMoney(order.GetTotal())}", fontH, XBrushes.Black, new XRect(margin, y, page.Width - margin * 2, 16), XStringFormats.TopLeft);
        y += 20;

        var refundLine = string.IsNullOrWhiteSpace(order.RefundId)
            ? "Devolução: iniciada/pendente no Stripe"
            : $"Devolução (Stripe): {order.RefundId}";
        gfx.DrawString(refundLine, font, XBrushes.Black, new XRect(margin, y, page.Width - margin * 2, 14), XStringFormats.TopLeft);
        y += 18;

        gfx.DrawString("Este documento confirma que a encomenda acima foi anulada.", font, XBrushes.Black, new XRect(margin, y, page.Width - margin * 2, 28), XStringFormats.TopLeft);

        using var ms = new MemoryStream();
        doc.Save(ms, closeStream: false);
        return ms.ToArray();
    }

    private static void DrawSummaryLine(
        XGraphics gfx,
        PdfPage page,
        XFont font,
        string label,
        string value,
        ref double y,
        double margin,
        bool isDiscount = false)
    {
        var xLabel = page.Width - margin - 200;
        var xValue = page.Width - margin - 60;

        gfx.DrawString(label, font, XBrushes.Black, new XRect(xLabel, y, 140, 14), XStringFormats.TopLeft);
        gfx.DrawString(
            isDiscount ? $"-{value}" : value,
            font,
            isDiscount ? XBrushes.DarkGreen : XBrushes.Black,
            new XRect(xValue, y, 80, 14),
            XStringFormats.TopLeft);

        y += 16;
    }

    private static string FormatMoney(long amount)
    {
        var euros = amount / 100.0;
        return euros.ToString("0.00", CultureInfo.InvariantCulture) + " €";
    }

    private static string FormatShippingAddress(ShippingAddress address)
    {
        var line2 = string.IsNullOrWhiteSpace(address.Line2) ? string.Empty : $" {address.Line2}";
        return $"{address.Name}\n{address.Line1}{line2}\n{address.PostalCode} {address.City}, {address.State}\n{address.Country}";
    }

    private static string? BuildSellerAddress(Contact? contact)
    {
        if (contact == null) return null;

        var parts = new[]
        {
            contact.Address?.Trim(),
            string.Join(" ", new[] { contact.PostalCode?.Trim(), contact.City?.Trim() }.Where(s => !string.IsNullOrWhiteSpace(s))),
            contact.Country?.Trim()
        };

        var result = string.Join("\n", parts.Where(s => !string.IsNullOrWhiteSpace(s)));
        return string.IsNullOrWhiteSpace(result) ? null : result;
    }

    private static string TrimToFit(XGraphics gfx, string text, XFont font, double maxWidth)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        if (gfx.MeasureString(text, font).Width <= maxWidth) return text;

        const string ellipsis = "…";
        var current = text;
        while (current.Length > 1)
        {
            current = current[..^1];
            var candidate = current + ellipsis;
            if (gfx.MeasureString(candidate, font).Width <= maxWidth) return candidate;
        }

        return ellipsis;
    }
}
