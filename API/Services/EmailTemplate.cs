using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using API.Entities.OrderAggregate;
using API.RequestHelpers;

namespace API.Services;

public static class EmailTemplate
{
    public static string TryWrap(EmailSettings settings, string? htmlContent, string? preheader = null)
    {
        if (string.IsNullOrWhiteSpace(htmlContent))
            return string.Empty;

        // If caller already provides a full HTML doc, do not wrap.
        if (LooksLikeFullDocument(htmlContent))
            return htmlContent;

        var brand = string.IsNullOrWhiteSpace(settings.FromName) ? "Restore" : settings.FromName.Trim();
        var brandEncoded = WebUtility.HtmlEncode(brand);

        var siteUrl = (settings.FrontendUrl ?? string.Empty).Trim();
        siteUrl = siteUrl.TrimEnd('/');
        var hasSiteUrl = !string.IsNullOrWhiteSpace(siteUrl);

        var pre = string.IsNullOrWhiteSpace(preheader) ? string.Empty : WebUtility.HtmlEncode(preheader.Trim());

        // Table-based layout for broad email client compatibility.
        return $$"""
<!doctype html>
<html lang=\"pt\">
<head>
  <meta charset=\"utf-8\">
  <meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">
  <meta name=\"x-apple-disable-message-reformatting\">
  <title>{{brandEncoded}}</title>
</head>
<body style=\"margin:0;padding:0;background-color:#f3f4f6;\">
  <div style=\"display:none;font-size:1px;line-height:1px;max-height:0;max-width:0;opacity:0;overflow:hidden;\">{{pre}}</div>

  <table role=\"presentation\" cellpadding=\"0\" cellspacing=\"0\" width=\"100%\" style=\"background-color:#f3f4f6;\">
    <tr>
      <td align=\"center\" style=\"padding:24px 12px;\">
        <table role=\"presentation\" cellpadding=\"0\" cellspacing=\"0\" width=\"600\" style=\"max-width:600px;width:100%;\">
          <tr>
            <td style=\"padding:0 0 12px 0;\">
              <div style=\"font-family:Arial,sans-serif;font-size:16px;font-weight:700;letter-spacing:0.2px;color:#111827;\">
                {{(hasSiteUrl ? $"<a href=\"{WebUtility.HtmlEncode(siteUrl)}\" style=\"color:#111827;text-decoration:none\">{brandEncoded}</a>" : brandEncoded)}}
              </div>
            </td>
          </tr>

          <tr>
            <td style=\"background-color:#ffffff;border:1px solid #e5e7eb;border-radius:12px;\">
              <div style=\"padding:20px 18px;font-family:Arial,sans-serif;font-size:14px;line-height:1.6;color:#111827;\">
                {{htmlContent}}
              </div>
            </td>
          </tr>

          <tr>
            <td style=\"padding:12px 0 0 0;\">
              <div style=\"font-family:Arial,sans-serif;font-size:12px;line-height:1.4;color:#6b7280;\">
                Este email foi enviado automaticamente. Se não reconhecer este pedido, pode ignorar esta mensagem.
                {{(hasSiteUrl ? $"<div style=\\\"margin-top:6px\\\">\n  <a href=\\\"{WebUtility.HtmlEncode(siteUrl)}\\\" style=\\\"color:#2563eb;text-decoration:none\\\">{WebUtility.HtmlEncode(siteUrl)}</a>\n</div>" : string.Empty)}}
              </div>
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

    public static string PrimaryButton(string url, string text)
    {
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(text)) return string.Empty;
        var safeUrl = WebUtility.HtmlEncode(url);
        var safeText = WebUtility.HtmlEncode(text);

        return $"<a href=\"{safeUrl}\" style=\"display:inline-block;background:#2563eb;color:#ffffff;text-decoration:none;padding:10px 14px;border-radius:10px;font-weight:700\">{safeText}</a>";
    }

    public static string RenderProductHighlight(
        string title,
        string name,
        string? imageUrl,
        string? description,
        string? productUrl,
        string? ctaText)
    {
        var safeTitle = WebUtility.HtmlEncode(title);
        var safeName = WebUtility.HtmlEncode(name);
        var absImage = ToAbsoluteUrl(productUrl, imageUrl);
        var safeDesc = string.IsNullOrWhiteSpace(description) ? string.Empty : WebUtility.HtmlEncode(Truncate(description.Trim(), 180));

        var imageHtml = string.IsNullOrWhiteSpace(absImage)
            ? string.Empty
            : $"<img src=\"{WebUtility.HtmlEncode(absImage)}\" alt=\"{safeName}\" width=\"72\" height=\"72\" style=\"display:block;width:72px;height:72px;object-fit:cover;border-radius:10px;border:1px solid #e5e7eb\" />";

        var cta = (!string.IsNullOrWhiteSpace(productUrl) && !string.IsNullOrWhiteSpace(ctaText))
            ? PrimaryButton(productUrl, ctaText)
            : string.Empty;

        var descBlock = string.IsNullOrWhiteSpace(safeDesc)
            ? string.Empty
            : $"<div style=\"margin-top:6px;color:#374151;font-size:13px\">{safeDesc}</div>";

        return $"""
<div style=\"margin:0 0 14px\">
  <div style=\"font-size:13px;color:#6b7280;margin:0 0 6px\">{safeTitle}</div>
  <table role=\"presentation\" cellpadding=\"0\" cellspacing=\"0\" width=\"100%\">
    <tr>
      <td style=\"width:80px;vertical-align:top;padding-right:10px\">{imageHtml}</td>
      <td style=\"vertical-align:top\">
        <div style=\"font-size:16px;font-weight:700;color:#111827\">{safeName}</div>
        {descBlock}
        <div style=\"margin-top:10px\">{cta}</div>
      </td>
    </tr>
  </table>
</div>
""";
    }

    public static string RenderOrderSummary(
        Order order,
        string? frontendUrl,
        IReadOnlyDictionary<int, string?>? productDescriptions = null,
        bool includeTotals = true)
    {
        if (order?.OrderItems == null || order.OrderItems.Count == 0) return string.Empty;

        var items = RenderOrderItemsTable(order.OrderItems, frontendUrl, productDescriptions);
        var totals = includeTotals ? RenderTotals(order) : string.Empty;

        return $"""
<div style=\"margin:16px 0 0\">
  <div style=\"font-size:14px;font-weight:700;margin:0 0 10px\">Produtos</div>
  {items}
  {totals}
</div>
""";
    }

    public static string RenderOrderItemsTable(
        IReadOnlyList<OrderItem> items,
        string? frontendUrl,
        IReadOnlyDictionary<int, string?>? productDescriptions = null)
    {
        if (items == null || items.Count == 0) return string.Empty;

        var rows = string.Join("", items.Select(oi => RenderOrderItemRow(oi, frontendUrl, productDescriptions)));
        return $"""
<table role=\"presentation\" cellpadding=\"0\" cellspacing=\"0\" width=\"100%\" style=\"border-collapse:separate;border-spacing:0 10px\">
  {rows}
</table>
""";
    }

    private static string RenderOrderItemRow(
        OrderItem oi,
        string? frontendUrl,
        IReadOnlyDictionary<int, string?>? productDescriptions)
    {
        var productId = oi.ItemOrdered.ProductId;
        var name = WebUtility.HtmlEncode(oi.ItemOrdered.Name);
        var color = string.IsNullOrWhiteSpace(oi.ItemOrdered.VariantColor) ? string.Empty : WebUtility.HtmlEncode(oi.ItemOrdered.VariantColor.Trim());
        var variantLine = string.IsNullOrWhiteSpace(color) ? string.Empty : $"<div style=\"font-size:12px;color:#6b7280\">Cor: {color}</div>";

        string? desc = null;
        if (productDescriptions != null && productDescriptions.TryGetValue(productId, out var d) && !string.IsNullOrWhiteSpace(d))
        {
            desc = Truncate(d.Trim(), 140);
        }
        var descLine = string.IsNullOrWhiteSpace(desc) ? string.Empty : $"<div style=\"font-size:12px;color:#374151;margin-top:4px\">{WebUtility.HtmlEncode(desc)}</div>";

        var productUrl = BuildProductUrl(frontendUrl, productId);
        var linkLine = string.IsNullOrWhiteSpace(productUrl)
            ? string.Empty
            : $"<div style=\"margin-top:8px\"><a href=\"{WebUtility.HtmlEncode(productUrl)}\" style=\"color:#2563eb;text-decoration:none;font-weight:700;font-size:12px\">Ver produto</a></div>";

        var imageUrl = ToAbsoluteUrl(frontendUrl, oi.ItemOrdered.PictureUrl);
        var imageHtml = string.IsNullOrWhiteSpace(imageUrl)
            ? ""
            : $"<img src=\"{WebUtility.HtmlEncode(imageUrl)}\" alt=\"{name}\" width=\"64\" height=\"64\" style=\"display:block;width:64px;height:64px;object-fit:cover;border-radius:10px;border:1px solid #e5e7eb\" />";

        var qty = oi.Quantity;
        var lineTotal = oi.Price * qty;

        return $"""
<tr>
  <td style=\"background:#ffffff;border:1px solid #e5e7eb;border-radius:12px;padding:12px\">
    <table role=\"presentation\" cellpadding=\"0\" cellspacing=\"0\" width=\"100%\">
      <tr>
        <td style=\"width:74px;vertical-align:top;padding-right:10px\">{imageHtml}</td>
        <td style=\"vertical-align:top\">
          <div style=\"font-size:14px;font-weight:700;color:#111827\">{name}</div>
          {variantLine}
          {descLine}
          {linkLine}
        </td>
        <td style=\"width:140px;vertical-align:top;text-align:right\">
          <div style=\"font-size:12px;color:#6b7280\">{qty} × {FormatEur(oi.Price)}</div>
          <div style=\"font-size:14px;font-weight:700;color:#111827\">{FormatEur(lineTotal)}</div>
        </td>
      </tr>
    </table>
  </td>
</tr>
""";
    }

    private static string RenderTotals(Order order)
    {
        var subtotal = FormatEur(order.Subtotal);
        var delivery = FormatEur(order.DeliveryFee);
        var discount = order.Discount > 0 ? "-" + FormatEur(order.Discount) : FormatEur(0);
        var total = FormatEur(order.GetTotal());

        var productDiscountLine = order.ProductDiscount > 0
            ? $"<tr><td style=\"padding:0 0 6px\">Descontos (produtos)</td><td style=\"padding:0 0 6px;text-align:right\">-{FormatEur(order.ProductDiscount)}</td></tr>"
            : string.Empty;

        return $"""
<div style=\"margin-top:8px\">
  <table role=\"presentation\" cellpadding=\"0\" cellspacing=\"0\" width=\"100%\" style=\"font-size:13px;color:#111827\">
    <tr><td style=\"padding:0 0 6px\">Subtotal</td><td style=\"padding:0 0 6px;text-align:right\">{subtotal}</td></tr>
    {productDiscountLine}
    <tr><td style=\"padding:0 0 6px\">Desconto (cupão)</td><td style=\"padding:0 0 6px;text-align:right\">{discount}</td></tr>
    <tr><td style=\"padding:0 0 6px\">Envio</td><td style=\"padding:0 0 6px;text-align:right\">{delivery}</td></tr>
    <tr><td style=\"padding-top:8px;font-weight:700\">Total</td><td style=\"padding-top:8px;text-align:right;font-weight:700\">{total}</td></tr>
  </table>
</div>
""";
    }

    private static bool LooksLikeFullDocument(string html)
    {
        var h = html.AsSpan().TrimStart();
        // quick checks (case-insensitive) for a full document
        return h.StartsWith("<!doctype".AsSpan(), StringComparison.OrdinalIgnoreCase)
            || h.StartsWith("<html".AsSpan(), StringComparison.OrdinalIgnoreCase)
            || html.Contains("<head", StringComparison.OrdinalIgnoreCase)
            || html.Contains("<body", StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatEur(long cents)
    {
      // Order amounts are stored in cents.
      var culture = CultureInfo.GetCultureInfo("pt-PT");
      var value = cents / 100m;
      return string.Format(culture, "{0:C}", value);
    }

    private static string Truncate(string s, int max)
    {
      if (string.IsNullOrEmpty(s)) return string.Empty;
      if (s.Length <= max) return s;
      return s[..max].TrimEnd() + "…";
    }

    private static string? BuildProductUrl(string? frontendUrl, int productId)
    {
      if (string.IsNullOrWhiteSpace(frontendUrl)) return null;
      var baseUrl = frontendUrl.Trim().TrimEnd('/');
      return $"{baseUrl}/catalog/{productId}";
    }

    private static string? ToAbsoluteUrl(string? baseUrlOrPageUrl, string? maybeRelativeUrl)
    {
      if (string.IsNullOrWhiteSpace(maybeRelativeUrl)) return null;
      var u = maybeRelativeUrl.Trim();

      if (u.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
        u.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
        u.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        return u;

      if (string.IsNullOrWhiteSpace(baseUrlOrPageUrl)) return u;

      // If a full product page URL is passed, reduce to origin-ish base.
      var baseUrl = baseUrlOrPageUrl.Trim();
      if (baseUrl.Contains("/catalog/", StringComparison.OrdinalIgnoreCase))
      {
        var idx = baseUrl.IndexOf("/catalog/", StringComparison.OrdinalIgnoreCase);
        if (idx > 0) baseUrl = baseUrl[..idx];
      }

      baseUrl = baseUrl.TrimEnd('/');
      if (u.StartsWith('/')) return baseUrl + u;
      return baseUrl + "/" + u;
    }
}
