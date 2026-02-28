using System;
using System.Linq;
using API.Data;
using API.DTOs;
using API.Entities;
using API.Entities.OrderAggregate;
using API.Extensions;
using API.RequestHelpers;
using API.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Stripe;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Hosting;
using System.IO;
using System.Collections.Generic;
using Microsoft.AspNetCore.Identity;
using System.Net;
using API.Services.Invoicing;

namespace API.Controllers;

[Authorize]
public class OrdersController(
    StoreContext context,
    IConfiguration config,
    ILogger<OrdersController> logger,
    API.Services.DiscountService discountService,
    IEmailService emailService,
    IOptions<EmailSettings> emailOptions,
    IInvoicePdfService invoicePdfService,
    ITaxInvoiceService taxInvoiceService,
    IWebHostEnvironment env,
    UserManager<User> userManager,
    INotificationService notificationService,
    StripeReversalService stripeReversalService) : BaseApiController
{
    private const decimal DefaultRate = 5m;
    private const decimal DefaultFreeShippingThreshold = 100m;

    [HttpGet]
    public async Task<ActionResult<List<OrderDto>>> GetOrders()
    {
        var orders = await context.Orders
            .ProjectToDto()
            .Where(x => x.BuyerEmail == User.GetEmail())
            .ToListAsync();

        return orders;
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<OrderDto>> GetOrderDetails(int id)
    {
        var order = await context.Orders
            .ProjectToDto()
            .Where(x => x.BuyerEmail == User.GetEmail() && id == x.Id)
            .FirstOrDefaultAsync();

        if (order == null) return NotFound();

        return order;
    }

    [HttpPost("{id:int}/refund")]
    public async Task<IActionResult> RequestRefund(int id, [FromBody] RequestRefundDto? dto, CancellationToken ct)
    {
        var email = User.GetEmail();
        if (string.IsNullOrWhiteSpace(email)) return Unauthorized();

        var order = await context.Orders
            .FirstOrDefaultAsync(o => o.Id == id && o.BuyerEmail == email, ct);

        if (order == null) return NotFound();

        if (order.OrderStatus == OrderStatus.Pending || order.OrderStatus == OrderStatus.PaymentFailed)
            return BadRequest("Não é possível pedir devolução para esta encomenda");

        // 30-day window
        var deadline = order.OrderDate.AddDays(30);
        if (DateTime.UtcNow > deadline)
            return BadRequest("O prazo de 30 dias para devolução já terminou");

        if (order.OrderStatus == OrderStatus.Cancelled || order.RefundRequestStatus == RefundRequestStatus.Approved)
            return NoContent();

        if (order.RefundRequestStatus == RefundRequestStatus.PendingReview)
            return NoContent();

        if (order.RefundRequestStatus == RefundRequestStatus.Rejected)
            return BadRequest("O pedido de devolução desta encomenda já foi recusado");

        if (dto == null) return BadRequest("É obrigatório indicar o motivo e o método de devolução");

        var reason = (dto.Reason ?? string.Empty).Trim();
        if (reason.Length > 1000) reason = reason[..1000];

        if (reason.Length < 20)
            return BadRequest("O comentário deve ter pelo menos 20 caracteres");

        if (dto.ReturnMethod == RefundReturnMethod.None)
            return BadRequest("Indique se a devolução é na loja ou por correio");

        order.RefundRequestStatus = RefundRequestStatus.PendingReview;
        order.RefundRequestedAt ??= DateTime.UtcNow;
        order.RefundReturnMethod = dto.ReturnMethod;
        order.RefundRequestReason = string.IsNullOrWhiteSpace(reason) ? null : reason;
        // Clear any previous review fields
        order.RefundReviewedAt = null;
        order.RefundReviewNote = null;

        var saved = await context.SaveChangesAsync(ct) > 0;
        if (!saved) return BadRequest("Não foi possível criar o pedido de devolução");

        await notificationService.TryCreateForEmailAsync(
            order.BuyerEmail,
            "Pedido de devolução em avaliação",
            $"O pedido de devolução da encomenda #{order.Id} está em avaliação.",
            $"/orders/{order.Id}",
            ct);

        await TrySendRefundRequestInstructionsEmail(order);
        await TryNotifyAdminsRefundRequestedAsync(order);

        return NoContent();
    }

    [Authorize(Roles = "Admin")]
    [HttpPut("all/{id:int}/refund/approve")]
    public async Task<IActionResult> ApproveRefundRequest(int id, [FromBody] RefundDecisionDto? dto, CancellationToken ct)
    {
        var order = await context.Orders
            .Include(o => o.OrderItems)
            .FirstOrDefaultAsync(o => o.Id == id, ct);

        if (order == null) return NotFound();

        if (order.RefundRequestStatus != RefundRequestStatus.PendingReview)
            return BadRequest("Não existe um pedido de devolução em avaliação para esta encomenda");

        // Validate window (admin can still reject even after deadline, but approval should respect policy)
        var deadline = order.OrderDate.AddDays(30);
        if (DateTime.UtcNow > deadline)
            return BadRequest("O prazo de 30 dias para devolução já terminou");

        var note = (dto?.Note ?? string.Empty).Trim();
        if (note.Length > 2000) note = note[..2000];

        // Stripe refund/cancel happens ONLY on admin approval
        var reversal = await stripeReversalService.ReverseAsync(order, ct);
        if (!reversal.Succeeded)
            return BadRequest(reversal.Error ?? "Não foi possível criar a devolução no Stripe");

        order.RefundRequestStatus = RefundRequestStatus.Approved;
        order.RefundReviewedAt ??= DateTime.UtcNow;
        order.RefundReviewNote = string.IsNullOrWhiteSpace(note) ? null : note;

        order.OrderStatus = OrderStatus.Cancelled;
        order.CancelledAt ??= DateTime.UtcNow;
        if (reversal.Kind == StripeReversalKind.Refunded && !string.IsNullOrWhiteSpace(reversal.RefundId))
        {
            order.RefundId ??= reversal.RefundId;
            order.RefundedAt ??= DateTime.UtcNow;
        }

        await TryRestockAndAdjustStatsAsync(order, ct);

        var saved = await context.SaveChangesAsync(ct) > 0;
        if (!saved) return BadRequest("Não foi possível aprovar o pedido de devolução");

        await notificationService.TryCreateForEmailAsync(
            order.BuyerEmail,
            "Pedido de devolução aceite",
            $"O pedido de devolução da encomenda #{order.Id} foi aceite e a devolução foi iniciada.",
            $"/orders/{order.Id}",
            ct);

        await TrySendStatusEmail(order, OrderStatus.Cancelled);

        await TryNotifyAdminsRefundApprovedAsync(order);

        return NoContent();
    }

    [Authorize(Roles = "Admin")]
    [HttpPut("all/{id:int}/refund/reject")]
    public async Task<IActionResult> RejectRefundRequest(int id, [FromBody] RefundDecisionDto? dto, CancellationToken ct)
    {
        var order = await context.Orders
            .FirstOrDefaultAsync(o => o.Id == id, ct);

        if (order == null) return NotFound();

        if (order.RefundRequestStatus != RefundRequestStatus.PendingReview)
            return BadRequest("Não existe um pedido de devolução em avaliação para esta encomenda");

        var note = (dto?.Note ?? string.Empty).Trim();
        if (note.Length > 2000) note = note[..2000];

        order.RefundRequestStatus = RefundRequestStatus.Rejected;
        order.RefundReviewedAt ??= DateTime.UtcNow;
        order.RefundReviewNote = string.IsNullOrWhiteSpace(note) ? null : note;

        var saved = await context.SaveChangesAsync(ct) > 0;
        if (!saved) return BadRequest("Não foi possível recusar o pedido de devolução");

        await notificationService.TryCreateForEmailAsync(
            order.BuyerEmail,
            "Pedido de devolução recusado",
            $"O pedido de devolução da encomenda #{order.Id} foi recusado.",
            $"/orders/{order.Id}",
            ct);

        await TrySendRefundRejectedEmail(order);

        await TryNotifyAdminsRefundRejectedAsync(order);

        return NoContent();
    }

    private async Task TryRestockAndAdjustStatsAsync(Order order, CancellationToken ct)
    {
        // Restock + sales stat adjustments should only happen once.
        if (order.RestockedAt.HasValue && order.SalesCountAdjustedAt.HasValue) return;

        foreach (var oi in order.OrderItems)
        {
            var productId = oi.ItemOrdered.ProductId;
            var variantId = oi.ItemOrdered.ProductVariantId;

            if (!order.RestockedAt.HasValue)
            {
                if (variantId.HasValue)
                {
                    var variant = await context.ProductVariants.FirstOrDefaultAsync(v => v.Id == variantId.Value && v.ProductId == productId, ct);
                    if (variant != null) variant.QuantityInStock += oi.Quantity;
                }

                var product = await context.Products.FirstOrDefaultAsync(p => p.Id == productId, ct);
                if (product != null) product.QuantityInStock += oi.Quantity;
            }

            if (!order.SalesCountAdjustedAt.HasValue)
            {
                var product = await context.Products.FirstOrDefaultAsync(p => p.Id == productId, ct);
                if (product != null)
                {
                    var next = product.SalesCount - oi.Quantity;
                    product.SalesCount = next < 0 ? 0 : next;
                }
            }
        }

        order.RestockedAt ??= DateTime.UtcNow;
        order.SalesCountAdjustedAt ??= DateTime.UtcNow;
    }

    private async Task TrySendRefundRequestInstructionsEmail(Order order)
    {
        try
        {
            var frontendUrl = (emailOptions.Value.FrontendUrl ?? string.Empty).TrimEnd('/');
            var orderUrl = string.IsNullOrWhiteSpace(frontendUrl) ? string.Empty : $"{frontendUrl}/orders/{order.Id}";

            var fullOrder = await context.Orders
                .AsNoTracking()
                .Include(o => o.OrderItems)
                .FirstOrDefaultAsync(o => o.Id == order.Id);

            var descMap = new Dictionary<int, string?>();
            if (fullOrder?.OrderItems?.Count > 0)
            {
                var productIds = fullOrder.OrderItems.Select(x => x.ItemOrdered.ProductId).Distinct().ToList();
                var meta = await context.Products
                    .AsNoTracking()
                    .Where(p => productIds.Contains(p.Id))
                    .Select(p => new { p.Id, Text = p.Subtitle ?? p.Description })
                    .ToListAsync();
                descMap = meta.ToDictionary(x => x.Id, x => x.Text);
            }

            var orderSummary = fullOrder == null ? string.Empty : EmailTemplate.RenderOrderSummary(fullOrder, frontendUrl, descMap);

            var subject = $"Pedido de devolução em avaliação (Encomenda #{order.Id})";

            var contactEmail = string.IsNullOrWhiteSpace(emailOptions.Value.AdminEmail)
                ? (emailOptions.Value.FromEmail ?? string.Empty)
                : emailOptions.Value.AdminEmail;

            var contactLine = string.IsNullOrWhiteSpace(contactEmail)
                ? ""
                : $"<p style=\"margin:0 0 12px\">Se precisar de ajuda, contacte: <strong>{WebUtility.HtmlEncode(contactEmail)}</strong></p>";

                        var orderLinkLine = string.IsNullOrWhiteSpace(orderUrl)
                                ? ""
                                : $"<p style=\"margin:0\">Ver encomenda: <a href=\"{orderUrl}\">{orderUrl}</a></p>";

                        var html = $"""
<div style="font-family:Arial,sans-serif;max-width:640px">
    <h2 style="margin:0 0 12px">Pedido de devolução em avaliação</h2>
    <p style="margin:0 0 12px">Recebemos o seu pedido de devolução para a encomenda <strong>#{order.Id}</strong>.</p>
    <p style="margin:0 0 12px">O pedido está em <strong>avaliação</strong>. A devolução só será processada depois de o produto ser devolvido e confirmado em bom estado pela nossa equipa.</p>
    <h3 style="margin:16px 0 8px">Instruções</h3>
    <ol style="margin:0 0 12px">
        <li>Embale o(s) artigo(s) em segurança e inclua o número da encomenda <strong>#{order.Id}</strong>.</li>
        <li>Envie-nos mensagem para combinar a devolução (morada/etiqueta), indicando o número da encomenda.</li>
        <li>Assim que o produto for recebido e verificado, confirmaremos a aceitação (ou recusa) do pedido.</li>
    </ol>
    {contactLine}
    {orderLinkLine}
    {orderSummary}
</div>
""";

            await emailService.SendEmailAsync(order.BuyerEmail, subject, html);
        }
        catch
        {
            // best-effort
        }
    }

    private async Task TrySendRefundRejectedEmail(Order order)
    {
        try
        {
            var frontendUrl = (emailOptions.Value.FrontendUrl ?? string.Empty).TrimEnd('/');
            var orderUrl = string.IsNullOrWhiteSpace(frontendUrl) ? string.Empty : $"{frontendUrl}/orders/{order.Id}";

            var fullOrder = await context.Orders
                .AsNoTracking()
                .Include(o => o.OrderItems)
                .FirstOrDefaultAsync(o => o.Id == order.Id);

            var descMap = new Dictionary<int, string?>();
            if (fullOrder?.OrderItems?.Count > 0)
            {
                var productIds = fullOrder.OrderItems.Select(x => x.ItemOrdered.ProductId).Distinct().ToList();
                var meta = await context.Products
                    .AsNoTracking()
                    .Where(p => productIds.Contains(p.Id))
                    .Select(p => new { p.Id, Text = p.Subtitle ?? p.Description })
                    .ToListAsync();
                descMap = meta.ToDictionary(x => x.Id, x => x.Text);
            }

            var orderSummary = fullOrder == null ? string.Empty : EmailTemplate.RenderOrderSummary(fullOrder, frontendUrl, descMap);

            var subject = $"Pedido de devolução recusado (Encomenda #{order.Id})";
            var note = string.IsNullOrWhiteSpace(order.RefundReviewNote) ? "" : $"<p style=\"margin:0 0 12px\"><strong>Comentário da Loja:</strong> {WebUtility.HtmlEncode(order.RefundReviewNote)}</p>";

                        var orderLinkLine = string.IsNullOrWhiteSpace(orderUrl)
                                ? ""
                                : $"<p style=\"margin:0\">Ver encomenda: <a href=\"{orderUrl}\">{orderUrl}</a></p>";

                        var html = $"""
<div style="font-family:Arial,sans-serif;max-width:640px">
    <h2 style="margin:0 0 12px">Pedido de devolução recusado</h2>
    <p style="margin:0 0 12px">O pedido de devolução da encomenda <strong>#{order.Id}</strong> foi recusado.</p>
    {note}
    {orderLinkLine}
    {orderSummary}
</div>
""";

            await emailService.SendEmailAsync(order.BuyerEmail, subject, html);
        }
        catch
        {
            // best-effort
        }
    }

    private async Task TryNotifyAdminsRefundRequestedAsync(Order order)
    {
        try
        {
            var adminEmails = await GetAdminEmailsAsync();

            if (adminEmails.Count == 0) return;

            foreach (var adminEmail in adminEmails)
            {
                await notificationService.TryCreateForEmailAsync(
                    adminEmail,
                    "Pedido de devolução",
                    $"Novo pedido de devolução na encomenda #{order.Id} (em avaliação).",
                    $"/admin/sales/{order.Id}");
            }

            var frontendUrl = (emailOptions.Value.FrontendUrl ?? string.Empty).TrimEnd('/');
            var adminUrl = string.IsNullOrWhiteSpace(frontendUrl) ? string.Empty : $"{frontendUrl}/admin/sales/{order.Id}";

            foreach (var to in adminEmails)
            {
                var subject = $"Novo pedido de devolução (Encomenda #{order.Id})";
                var reason = string.IsNullOrWhiteSpace(order.RefundRequestReason) ? "" : $"<p style=\"margin:0 0 12px\"><strong>Motivo:</strong> {WebUtility.HtmlEncode(order.RefundRequestReason)}</p>";
                var method = order.RefundReturnMethod switch
                {
                    RefundReturnMethod.InStore => "Loja",
                    RefundReturnMethod.ByMail => "Correio",
                    _ => "(não indicado)"
                };
                var methodLine = $"<p style=\"margin:0 0 12px\"><strong>Método:</strong> {WebUtility.HtmlEncode(method)}</p>";

                                var adminLinkLine = string.IsNullOrWhiteSpace(adminUrl)
                                        ? ""
                                        : $"<p style=\"margin:0\">Abrir venda: <a href=\"{adminUrl}\">{adminUrl}</a></p>";

                                var html = $"""
<div style="font-family:Arial,sans-serif;max-width:640px">
    <h2 style="margin:0 0 12px">Pedido de devolução em avaliação</h2>
    <p style="margin:0 0 12px">O cliente <strong>{WebUtility.HtmlEncode(order.BuyerEmail)}</strong> pediu devolução da encomenda <strong>#{order.Id}</strong>.</p>
    {methodLine}
    {reason}
    {adminLinkLine}
</div>
""";
                await emailService.SendEmailAsync(to, subject, html);
            }
        }
        catch
        {
            // best-effort
        }
    }

    private async Task TryNotifyAdminsRefundApprovedAsync(Order order)
    {
        try
        {
            var adminEmails = await GetAdminEmailsAsync();
            if (adminEmails.Count == 0) return;

            foreach (var adminEmail in adminEmails)
            {
                await notificationService.TryCreateForEmailAsync(
                    adminEmail,
                    "Devolução aceite",
                    $"A devolução da encomenda #{order.Id} foi aceite.",
                    $"/admin/sales/{order.Id}");
            }

            var frontendUrl = (emailOptions.Value.FrontendUrl ?? string.Empty).TrimEnd('/');
            var adminUrl = string.IsNullOrWhiteSpace(frontendUrl) ? string.Empty : $"{frontendUrl}/admin/sales/{order.Id}";
            var subject = $"[Devolução] Aceite - Encomenda #{order.Id}";

            var storeNote = string.IsNullOrWhiteSpace(order.RefundReviewNote)
                ? string.Empty
                : $"<p style=\"margin:0 0 12px\"><strong>Comentário da Loja:</strong> {WebUtility.HtmlEncode(order.RefundReviewNote)}</p>";

            var html = $"""
<div style="font-family:Arial,sans-serif;max-width:640px">
  <h2 style="margin:0 0 12px">Devolução aceite</h2>
  <p style="margin:0 0 12px">A devolução da encomenda <strong>#{order.Id}</strong> foi aceite.</p>
  <p style="margin:0 0 12px"><strong>Cliente:</strong> {WebUtility.HtmlEncode(order.BuyerEmail)}</p>
  {storeNote}
  {(string.IsNullOrWhiteSpace(adminUrl) ? string.Empty : $"<p style=\"margin:0\">Abrir venda: <a href=\"{adminUrl}\">{adminUrl}</a></p>")}
</div>
""";

            foreach (var to in adminEmails)
            {
                await emailService.SendEmailAsync(to, subject, html);
            }
        }
        catch
        {
            // best-effort
        }
    }

    private async Task TryNotifyAdminsRefundRejectedAsync(Order order)
    {
        try
        {
            var adminEmails = await GetAdminEmailsAsync();
            if (adminEmails.Count == 0) return;

            foreach (var adminEmail in adminEmails)
            {
                await notificationService.TryCreateForEmailAsync(
                    adminEmail,
                    "Devolução recusada",
                    $"A devolução da encomenda #{order.Id} foi recusada.",
                    $"/admin/sales/{order.Id}");
            }

            var frontendUrl = (emailOptions.Value.FrontendUrl ?? string.Empty).TrimEnd('/');
            var adminUrl = string.IsNullOrWhiteSpace(frontendUrl) ? string.Empty : $"{frontendUrl}/admin/sales/{order.Id}";
            var subject = $"[Devolução] Recusada - Encomenda #{order.Id}";

            var storeNote = string.IsNullOrWhiteSpace(order.RefundReviewNote)
                ? string.Empty
                : $"<p style=\"margin:0 0 12px\"><strong>Comentário da Loja:</strong> {WebUtility.HtmlEncode(order.RefundReviewNote)}</p>";

            var html = $"""
<div style="font-family:Arial,sans-serif;max-width:640px">
  <h2 style="margin:0 0 12px">Devolução recusada</h2>
  <p style="margin:0 0 12px">A devolução da encomenda <strong>#{order.Id}</strong> foi recusada.</p>
  <p style="margin:0 0 12px"><strong>Cliente:</strong> {WebUtility.HtmlEncode(order.BuyerEmail)}</p>
  {storeNote}
  {(string.IsNullOrWhiteSpace(adminUrl) ? string.Empty : $"<p style=\"margin:0\">Abrir venda: <a href=\"{adminUrl}\">{adminUrl}</a></p>")}
</div>
""";

            foreach (var to in adminEmails)
            {
                await emailService.SendEmailAsync(to, subject, html);
            }
        }
        catch
        {
            // best-effort
        }
    }

    [HttpGet("{id:int}/invoice")]
    public async Task<IActionResult> DownloadReceipt(int id, CancellationToken ct)
    {
        var order = await context.Orders
            .AsNoTracking()
            .Include(o => o.OrderItems)
            .FirstOrDefaultAsync(o => o.Id == id && o.BuyerEmail == User.GetEmail(), ct);

        if (order == null) return NotFound();

        if (order.OrderStatus == OrderStatus.Pending || order.OrderStatus == OrderStatus.PaymentFailed)
            return BadRequest("Recibo indisponível para esta encomenda");

        var pdfBytes = await invoicePdfService.GenerateReceiptPdfAsync(order, ct);
        return File(pdfBytes, "application/pdf", $"recibo-{order.Id}.pdf");
    }

    [HttpGet("{id:int}/tax-invoice")]
    public async Task<IActionResult> DownloadTaxInvoice(int id, CancellationToken ct)
    {
        var order = await context.Orders
            .AsNoTracking()
            .Include(o => o.OrderItems)
            .FirstOrDefaultAsync(o => o.Id == id && o.BuyerEmail == User.GetEmail(), ct);

        if (order == null) return NotFound();

        if (order.OrderStatus == OrderStatus.Pending || order.OrderStatus == OrderStatus.PaymentFailed)
            return BadRequest("Fatura indisponível para esta encomenda");

        var pdf = await taxInvoiceService.TryGetOrIssueInvoicePdfAsync(order, ct);
        if (pdf == null) return BadRequest("Fatura oficial indisponível (serviço de faturação não configurado ou falhou)");

        return File(pdf.Content, pdf.ContentType, pdf.FileName);
    }

    [Authorize(Roles = "Admin")]
    [HttpGet("all")]
    public async Task<ActionResult<List<OrderDto>>> GetAllSales([FromQuery] OrderAdminParams queryParams)
    {
        var ordersQuery = context.Orders
            .AsNoTracking()
            .Where(o => o.OrderStatus != OrderStatus.Pending && o.OrderStatus != OrderStatus.PaymentFailed)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(queryParams.Status) &&
            Enum.TryParse<OrderStatus>(queryParams.Status.Trim(), ignoreCase: true, out var status))
        {
            ordersQuery = ordersQuery.Where(o => o.OrderStatus == status);
        }

        if (!string.IsNullOrWhiteSpace(queryParams.BuyerEmail))
        {
            var needle = queryParams.BuyerEmail.Trim();
            ordersQuery = ordersQuery.Where(o => o.BuyerEmail.Contains(needle));
        }

        if (queryParams.From.HasValue || queryParams.To.HasValue)
        {
            var start = queryParams.From?.Date ?? DateTime.MinValue;
            var end = queryParams.To?.Date.AddDays(1).AddTicks(-1) ?? DateTime.MaxValue;
            ordersQuery = ordersQuery.Where(o => o.OrderDate >= start && o.OrderDate <= end);
        }

        if (queryParams.CategoryId.HasValue && queryParams.CategoryId.Value > 0)
        {
            var productIds = await FilterProductIdsByCategory(queryParams.CategoryId.Value);
            if (productIds.Count == 0)
            {
                ordersQuery = ordersQuery.Where(_ => false);
            }
            else
            {
                ordersQuery = ordersQuery.Where(o => o.OrderItems.Any(oi => productIds.Contains(oi.ItemOrdered.ProductId)));
            }
        }

        var dtoQuery = ordersQuery
            .OrderByDescending(o => o.OrderDate)
            .ProjectToDto();

        var paged = await PagedList<OrderDto>.ToPagedList(dtoQuery, queryParams.PageNumber, queryParams.PageSize);

        Response.AddPaginationHeader(paged.Metadata);

        return paged;
    }

    private async Task<List<int>> FilterProductIdsByCategory(int categoryId)
    {
        return await context.Set<Dictionary<string, object>>("CategoryProduct")
            .AsNoTracking()
            .Where(cp => EF.Property<int>(cp, "CategoriesId") == categoryId)
            .Select(cp => EF.Property<int>(cp, "ProductsId"))
            .Distinct()
            .ToListAsync();
    }

    [Authorize(Roles = "Admin")]
    [HttpGet("all/{id:int}")]
    public async Task<ActionResult<OrderDto>> GetAnyOrderDetails(int id)
    {
        var order = await context.Orders
            .ProjectToDto()
            .Where(x => id == x.Id)
            .FirstOrDefaultAsync();

        if (order == null) return NotFound();

        return order;
    }

    [Authorize(Roles = "Admin")]
    [HttpGet("all/{id:int}/invoice")]
    public async Task<IActionResult> DownloadAnyReceipt(int id, CancellationToken ct)
    {
        var order = await context.Orders
            .AsNoTracking()
            .Include(o => o.OrderItems)
            .FirstOrDefaultAsync(o => o.Id == id, ct);

        if (order == null) return NotFound();

        if (order.OrderStatus == OrderStatus.Pending || order.OrderStatus == OrderStatus.PaymentFailed)
            return BadRequest("Recibo indisponível para esta encomenda");

        var pdfBytes = await invoicePdfService.GenerateReceiptPdfAsync(order, ct);
        return File(pdfBytes, "application/pdf", $"recibo-{order.Id}.pdf");
    }

    [Authorize(Roles = "Admin")]
    [HttpGet("all/{id:int}/tax-invoice")]
    public async Task<IActionResult> DownloadAnyTaxInvoice(int id, CancellationToken ct)
    {
        var order = await context.Orders
            .AsNoTracking()
            .Include(o => o.OrderItems)
            .FirstOrDefaultAsync(o => o.Id == id, ct);

        if (order == null) return NotFound();

        if (order.OrderStatus == OrderStatus.Pending || order.OrderStatus == OrderStatus.PaymentFailed)
            return BadRequest("Fatura indisponível para esta encomenda");

        var pdf = await taxInvoiceService.TryGetOrIssueInvoicePdfAsync(order, ct);
        if (pdf == null) return BadRequest("Fatura oficial indisponível (serviço de faturação não configurado ou falhou)");

        return File(pdf.Content, pdf.ContentType, pdf.FileName);
    }

    [Authorize(Roles = "Admin")]
    [HttpPut("all/{id:int}/status")]
    public async Task<ActionResult<OrderDto>> UpdateOrderStatus(int id, [FromBody] UpdateOrderStatusDto dto)
    {
        if (dto == null || string.IsNullOrWhiteSpace(dto.Status)) return BadRequest("Invalid status");

        if (!Enum.TryParse<OrderStatus>(dto.Status.Trim(), ignoreCase: true, out var newStatus))
            return BadRequest("Unknown status");

        var allowed = new HashSet<OrderStatus>
        {
            OrderStatus.PaymentReceived,
            OrderStatus.Processing,
            OrderStatus.Processed,
            OrderStatus.Shipped,
            OrderStatus.Delivered,
            OrderStatus.Cancelled,
            OrderStatus.ReviewRequested
        };

        if (!allowed.Contains(newStatus)) return BadRequest("Status not allowed");

        var order = await context.Orders.FirstOrDefaultAsync(o => o.Id == id);
        if (order == null) return NotFound();

        var trackingToSet = (dto.TrackingNumber ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(trackingToSet) && newStatus != OrderStatus.Shipped)
            return BadRequest("Tracking só pode ser definido quando o estado é 'Enviado'.");

        if (newStatus == OrderStatus.Shipped && !string.IsNullOrWhiteSpace(trackingToSet))
        {
            if (trackingToSet.Length < 6) return BadRequest("Número de tracking inválido");
            if (trackingToSet.Length > 64) return BadRequest("Número de tracking demasiado longo");

            // Avoid changing timestamps / re-sending notifications if it's the same tracking
            var isSame = string.Equals(order.TrackingNumber ?? string.Empty, trackingToSet, StringComparison.Ordinal);
            if (!isSame)
            {
                order.TrackingNumber = trackingToSet;
                order.TrackingAddedAt = DateTime.UtcNow;
            }
        }

        // If cancelling, reverse payment in Stripe first (refund or cancel PaymentIntent).
        if (newStatus == OrderStatus.Cancelled)
        {
            var reversal = await stripeReversalService.ReverseAsync(order, CancellationToken.None);
            if (!reversal.Succeeded)
            {
                return BadRequest(reversal.Error ?? "Não foi possível reverter o pagamento no Stripe");
            }

            order.CancelledAt ??= DateTime.UtcNow;
            if (reversal.Kind == StripeReversalKind.Refunded && !string.IsNullOrWhiteSpace(reversal.RefundId))
            {
                order.RefundId ??= reversal.RefundId;
                order.RefundedAt ??= DateTime.UtcNow;
            }
        }

        order.OrderStatus = newStatus;

        var saved = await context.SaveChangesAsync() > 0;
        if (!saved) return BadRequest("Problem updating order status");

        if (newStatus == OrderStatus.ReviewRequested)
        {
            await notificationService.TryCreateForEmailAsync(
                order.BuyerEmail,
                "Pedido de avaliação",
                $"A sua encomenda #{order.Id} está pronta para avaliação (serviço e produtos).",
                $"/orders/{order.Id}?feedback=1");
        }
        else
        {
            await notificationService.TryCreateForEmailAsync(
                order.BuyerEmail,
                "Atualização da encomenda",
                $"A encomenda #{order.Id} foi atualizada para: {newStatus}.",
                $"/orders/{order.Id}");
        }

        if (newStatus == OrderStatus.PaymentReceived)
        {
            await TrySendReceiptEmailIfNeeded(order, CancellationToken.None);
        }

        if (newStatus == OrderStatus.ReviewRequested)
        {
            await TrySendReviewRequestEmail(order.Id, CancellationToken.None);
        }
        else
        {
            await TrySendStatusEmail(order, newStatus);
        }

        if (newStatus == OrderStatus.Shipped && !string.IsNullOrWhiteSpace(trackingToSet))
        {
            await TrySendTrackingNotificationEmail(order, trackingToSet, CancellationToken.None);
        }

        var updated = await context.Orders
            .ProjectToDto()
            .Where(x => x.Id == id)
            .FirstOrDefaultAsync();

        return updated == null ? Ok() : Ok(updated);
    }

    private async Task TrySendReviewRequestEmail(int orderId, CancellationToken ct)
    {
        try
        {
            var fullOrder = await context.Orders
                .AsNoTracking()
                .Include(o => o.OrderItems)
                .FirstOrDefaultAsync(o => o.Id == orderId, ct);

            if (fullOrder == null) return;

            var frontend = emailOptions.Value.FrontendUrl?.TrimEnd('/') ?? string.Empty;
            var orderUrl = string.IsNullOrWhiteSpace(frontend) ? string.Empty : $"{frontend}/orders/{fullOrder.Id}?feedback=1";

            var productIds = fullOrder.OrderItems.Select(x => x.ItemOrdered.ProductId).Distinct().ToList();
            var meta = await context.Products
                .AsNoTracking()
                .Where(p => productIds.Contains(p.Id))
                .Select(p => new { p.Id, Text = p.Subtitle ?? p.Description })
                .ToListAsync(ct);
            var descMap = meta.ToDictionary(x => x.Id, x => x.Text);

            var orderSummary = EmailTemplate.RenderOrderSummary(fullOrder, frontend, descMap, includeTotals: false);

            var productLinks = fullOrder.OrderItems
                .Select(oi => new { oi.ItemOrdered.ProductId, oi.ItemOrdered.Name })
                .GroupBy(x => x.ProductId)
                .Select(g => g.First())
                .Select(x => new
                {
                    x.ProductId,
                    x.Name,
                    Url = string.IsNullOrWhiteSpace(frontend) ? string.Empty : $"{frontend}/catalog/{x.ProductId}?review=1"
                })
                .Where(x => !string.IsNullOrWhiteSpace(x.Url))
                .ToList();

            var productsHtml = productLinks.Count == 0
                ? ""
                : "<ul>" + string.Join("", productLinks.Select(p => $"<li><a href=\"{p.Url}\">{System.Net.WebUtility.HtmlEncode(p.Name)}</a></li>")) + "</ul>";

            var serviceBtn = string.IsNullOrWhiteSpace(orderUrl) ? string.Empty : EmailTemplate.PrimaryButton(orderUrl, "Deixar feedback do serviço");
            var productButtons = productLinks.Count == 0
                ? string.Empty
                : "<div style=\"margin-top:10px\">" + string.Join(" ", productLinks.Select(p => EmailTemplate.PrimaryButton(p.Url, $"Avaliar: {p.Name}"))) + "</div>";

            var subject = $"Encomenda #{fullOrder.Id}: Avalie o serviço e os produtos";
            var html = $"""
                <div style='font-family: Arial, sans-serif; line-height: 1.5'>
                  <h2>Restore</h2>
                  <p>A sua encomenda <strong>#{fullOrder.Id}</strong> está pronta para avaliação.</p>

                  <h3>Feedback do serviço (encomenda)</h3>
                  <p>Este comentário é sobre a experiência/serviço da encomenda (não é a avaliação do produto).</p>
                                    {(string.IsNullOrWhiteSpace(serviceBtn) ? string.Empty : $"<p style=\"margin:0 0 12px\">{serviceBtn}</p>")}

                  <h3>Avaliação dos produtos</h3>
                                    <p>Para avaliar um produto, use a secção <strong>Avaliações</strong> na página do produto:</p>
                                    {orderSummary}
                                    {productButtons}
                                    {(string.IsNullOrWhiteSpace(productButtons) ? productsHtml : string.Empty)}
                </div>
                """;

            await emailService.SendEmailAsync(fullOrder.BuyerEmail, subject, html);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to send review request email for order {OrderId}", orderId);
        }
    }

    private async Task TrySendTrackingNotificationEmail(Order order, string tracking, CancellationToken ct)
    {
        // Avoid duplicate notifications if already saved (e.g., status update saved first then tracking endpoint called)
        var shouldNotify = string.Equals(order.TrackingNumber ?? string.Empty, tracking, StringComparison.Ordinal);
        if (!shouldNotify) return;

        var cttUrl = $"https://www.ctt.pt/feapl_2/app/open/objectSearch/objectSearch.jspx?objects={WebUtility.UrlEncode(tracking)}";

        await notificationService.TryCreateForEmailAsync(
            order.BuyerEmail,
            "Encomenda enviada",
            $"A encomenda #{order.Id} foi enviada. Tracking CTT: {tracking}.",
            $"/orders/{order.Id}",
            ct);

        try
        {
            var frontend = (emailOptions.Value.FrontendUrl ?? string.Empty).TrimEnd('/');
            var orderUrl = string.IsNullOrWhiteSpace(frontend) ? string.Empty : $"{frontend}/orders/{order.Id}";
            var subject = $"Tracking CTT - Encomenda #{order.Id}";

            var fullOrder = await context.Orders
                .AsNoTracking()
                .Include(o => o.OrderItems)
                .FirstOrDefaultAsync(o => o.Id == order.Id, ct);

            var descMap = new Dictionary<int, string?>();
            if (fullOrder?.OrderItems?.Count > 0)
            {
                var productIds = fullOrder.OrderItems.Select(x => x.ItemOrdered.ProductId).Distinct().ToList();
                var meta = await context.Products
                    .AsNoTracking()
                    .Where(p => productIds.Contains(p.Id))
                    .Select(p => new { p.Id, Text = p.Subtitle ?? p.Description })
                    .ToListAsync(ct);
                descMap = meta.ToDictionary(x => x.Id, x => x.Text);
            }

            var orderSummary = fullOrder == null ? string.Empty : EmailTemplate.RenderOrderSummary(fullOrder, frontend, descMap, includeTotals: false);
            var btnTrack = EmailTemplate.PrimaryButton(cttUrl, "Acompanhar CTT");
            var btnOrder = string.IsNullOrWhiteSpace(orderUrl) ? string.Empty : EmailTemplate.PrimaryButton(orderUrl, "Ver encomenda");

            var html = $"""
                <div style='font-family: Arial, sans-serif; line-height: 1.5'>
                  <h2>Restore</h2>
                  <p>A sua encomenda <strong>#{order.Id}</strong> foi enviada.</p>
                  <p><strong>Tracking CTT:</strong> {WebUtility.HtmlEncode(tracking)}</p>
                  <p style='margin:0 0 12px'>{btnTrack} {(string.IsNullOrWhiteSpace(btnOrder) ? string.Empty : btnOrder)}</p>
                  {orderSummary}
                </div>
                """;

            await emailService.SendEmailAsync(order.BuyerEmail, subject, html);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to send tracking email for order {OrderId}", order.Id);
        }
    }

    [Authorize(Roles = "Admin")]
    [HttpPut("all/{id:int}/tracking")]
    public async Task<ActionResult<OrderDto>> UpdateTrackingNumber(int id, [FromBody] UpdateTrackingNumberDto dto, CancellationToken ct)
    {
        var tracking = (dto?.TrackingNumber ?? string.Empty).Trim();
        if (tracking.Length < 6) return BadRequest("Número de tracking inválido");
        if (tracking.Length > 64) return BadRequest("Número de tracking demasiado longo");

        var order = await context.Orders.FirstOrDefaultAsync(o => o.Id == id, ct);
        if (order == null) return NotFound();

        if (order.OrderStatus != OrderStatus.Shipped)
            return BadRequest("Só pode adicionar tracking quando a encomenda está 'Enviado'.");

        order.TrackingNumber = tracking;
        order.TrackingAddedAt = DateTime.UtcNow;

        var saved = await context.SaveChangesAsync(ct) > 0;
        if (!saved) return BadRequest("Problem saving tracking number");

        var cttUrl = $"https://www.ctt.pt/feapl_2/app/open/objectSearch/objectSearch.jspx?objects={WebUtility.UrlEncode(tracking)}";

        await notificationService.TryCreateForEmailAsync(
            order.BuyerEmail,
            "Encomenda enviada",
            $"A encomenda #{order.Id} foi enviada. Tracking CTT: {tracking}.",
            $"/orders/{order.Id}",
            ct);

        try
        {
            var frontend = (emailOptions.Value.FrontendUrl ?? string.Empty).TrimEnd('/');
            var orderUrl = string.IsNullOrWhiteSpace(frontend) ? string.Empty : $"{frontend}/orders/{order.Id}";
            var subject = $"Tracking CTT - Encomenda #{order.Id}";
            var html = $"""
                <div style='font-family: Arial, sans-serif; line-height: 1.5'>
                  <h2>Restore</h2>
                  <p>A sua encomenda <strong>#{order.Id}</strong> foi enviada.</p>
                  <p><strong>Tracking CTT:</strong> {WebUtility.HtmlEncode(tracking)}</p>
                  <p>Acompanhar: <a href=\"{cttUrl}\">{cttUrl}</a></p>
                  {(string.IsNullOrWhiteSpace(orderUrl) ? string.Empty : $"<p>Ver encomenda: <a href=\"{orderUrl}\">{orderUrl}</a></p>")}
                </div>
                """;

            await emailService.SendEmailAsync(order.BuyerEmail, subject, html);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to send tracking email for order {OrderId}", order.Id);
        }

        var updated = await context.Orders
            .ProjectToDto()
            .Where(o => o.Id == id)
            .FirstOrDefaultAsync(ct);

        return updated == null ? Ok() : Ok(updated);
    }

    private async Task TrySendReceiptEmailIfNeeded(Order order, CancellationToken ct)
    {
        try
        {
            if (order.ReceiptEmailedAt.HasValue) return;
            if (order.OrderStatus == OrderStatus.Pending || order.OrderStatus == OrderStatus.PaymentFailed) return;

            var fullOrder = await context.Orders
                .AsNoTracking()
                .Include(o => o.OrderItems)
                .FirstOrDefaultAsync(o => o.Id == order.Id, ct);

            if (fullOrder == null) return;

            var receiptBytes = await invoicePdfService.GenerateReceiptPdfAsync(fullOrder, ct);
            var taxPdf = await taxInvoiceService.TryGetOrIssueInvoicePdfAsync(fullOrder, ct);

            var subject = taxPdf != null
                ? $"Fatura e recibo da encomenda #{order.Id}"
                : $"Recibo da encomenda #{order.Id}";

                        var frontend = (emailOptions.Value.FrontendUrl ?? string.Empty).TrimEnd('/');
                        var orderUrl = string.IsNullOrWhiteSpace(frontend) ? string.Empty : $"{frontend}/orders/{order.Id}";
                        var productIds = fullOrder.OrderItems.Select(x => x.ItemOrdered.ProductId).Distinct().ToList();
                        var meta = await context.Products
                                .AsNoTracking()
                                .Where(p => productIds.Contains(p.Id))
                                .Select(p => new { p.Id, Text = p.Subtitle ?? p.Description })
                                .ToListAsync(ct);
                        var descMap = meta.ToDictionary(x => x.Id, x => x.Text);
                        var orderSummary = EmailTemplate.RenderOrderSummary(fullOrder, frontend, descMap);

                        var btnOrder = string.IsNullOrWhiteSpace(orderUrl) ? string.Empty : EmailTemplate.PrimaryButton(orderUrl, "Ver encomenda");

                        var heading = taxPdf != null ? "Fatura e recibo" : "Recibo";
                        var body = taxPdf != null
                            ? $"Segue em anexo a fatura (oficial) e o recibo (PDF) da sua encomenda <strong>#{order.Id}</strong>."
                            : $"Segue em anexo o recibo (PDF) da sua encomenda <strong>#{order.Id}</strong>.";

                        var html = $"""
                                <div style='font-family: Arial, sans-serif; line-height: 1.5'>
                                    <h2>{heading}</h2>
                                    <p>{body}</p>
                                    {(string.IsNullOrWhiteSpace(btnOrder) ? string.Empty : $"<p style='margin:0 0 12px'>{btnOrder}</p>")}
                                    {orderSummary}
                                </div>
                                """;

            var attachments = new List<EmailAttachment>();
            if (taxPdf != null)
            {
                attachments.Add(new EmailAttachment
                {
                    FileName = taxPdf.FileName,
                    ContentType = taxPdf.ContentType,
                    Content = taxPdf.Content
                });
            }

            attachments.Add(new EmailAttachment
            {
                FileName = $"recibo-{order.Id}.pdf",
                ContentType = "application/pdf",
                Content = receiptBytes
            });

            var sent = await emailService.SendEmailWithAttachmentsAsync(order.BuyerEmail, subject, html, attachments);

            if (!sent) return;

            var toUpdate = await context.Orders.FirstOrDefaultAsync(o => o.Id == order.Id, ct);
            if (toUpdate == null) return;

            toUpdate.ReceiptEmailedAt = DateTime.UtcNow;
            if (taxPdf != null) toUpdate.TaxInvoiceEmailedAt = DateTime.UtcNow;
            await context.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to send receipt email for order {OrderId}", order.Id);
        }
    }

    [HttpPost("{id:int}/comment")]
    public async Task<ActionResult> AddOrderComment(int id, [FromBody] OrderCommentDto dto)
    {
        if (dto == null || string.IsNullOrWhiteSpace(dto.Comment)) return BadRequest("Comentário inválido");

        var comment = dto.Comment.Trim();
        if (comment.Length < 3) return BadRequest("Comentário demasiado curto");
        if (comment.Length > 1000) return BadRequest("Comentário demasiado longo");

        var order = await context.Orders.FirstOrDefaultAsync(o => o.Id == id && o.BuyerEmail == User.GetEmail());
        if (order == null) return NotFound();

        if (order.OrderStatus != OrderStatus.ReviewRequested)
            return BadRequest("Esta encomenda não está disponível para avaliação");

        if (!string.IsNullOrWhiteSpace(order.CustomerComment))
            return BadRequest("Já existe um comentário para esta encomenda");

        order.CustomerComment = comment;
        order.CustomerCommentedAt = DateTime.UtcNow;
        order.OrderStatus = OrderStatus.Completed;

        var saved = await context.SaveChangesAsync() > 0;
        if (!saved) return BadRequest("Problem saving comment");

        await TryNotifyAdminsOrderComment(order);

        await notificationService.TryCreateForEmailAsync(
            order.BuyerEmail,
            "Obrigado pelo feedback",
            $"Recebemos o seu feedback da encomenda #{order.Id}. Se quiser, pode também avaliar os produtos.",
            $"/orders/{order.Id}");

        await TrySendThankYouAfterServiceFeedbackEmail(order.Id, CancellationToken.None);

        return NoContent();
    }

    private async Task TrySendThankYouAfterServiceFeedbackEmail(int orderId, CancellationToken ct)
    {
        try
        {
            var fullOrder = await context.Orders
                .AsNoTracking()
                .Include(o => o.OrderItems)
                .FirstOrDefaultAsync(o => o.Id == orderId, ct);

            if (fullOrder == null) return;

            var frontend = emailOptions.Value.FrontendUrl?.TrimEnd('/') ?? string.Empty;
            var orderUrl = string.IsNullOrWhiteSpace(frontend) ? string.Empty : $"{frontend}/orders/{fullOrder.Id}";

            var productLinks = fullOrder.OrderItems
                .Select(oi => new { oi.ItemOrdered.ProductId, oi.ItemOrdered.Name })
                .GroupBy(x => x.ProductId)
                .Select(g => g.First())
                .Select(x => new
                {
                    x.ProductId,
                    x.Name,
                    Url = string.IsNullOrWhiteSpace(frontend) ? string.Empty : $"{frontend}/catalog/{x.ProductId}?review=1"
                })
                .Where(x => !string.IsNullOrWhiteSpace(x.Url))
                .ToList();

            var productsHtml = productLinks.Count == 0
                ? ""
                : "<ul>" + string.Join("", productLinks.Select(p => $"<li><a href=\"{p.Url}\">{System.Net.WebUtility.HtmlEncode(p.Name)}</a></li>")) + "</ul>";

            var subject = $"Encomenda #{fullOrder.Id}: Obrigado pelo seu feedback";
            var html = $"""
                <div style='font-family: Arial, sans-serif; line-height: 1.5'>
                  <h2>Restore</h2>
                  <p>Obrigado pelo seu feedback na encomenda <strong>#{fullOrder.Id}</strong>.</p>
                  {(string.IsNullOrWhiteSpace(orderUrl) ? string.Empty : $"<p>Ver encomenda: <a href=\"{orderUrl}\">{orderUrl}</a></p>")}
                  {(string.IsNullOrWhiteSpace(productsHtml) ? string.Empty : $"<p>Se ainda não o fez, pode avaliar os produtos:</p>{productsHtml}")}
                </div>
                """;

            await emailService.SendEmailAsync(fullOrder.BuyerEmail, subject, html);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to send thank-you email after service feedback for order {OrderId}", orderId);
        }
    }

    [Authorize(Roles = "Admin")]
    [HttpPut("all/{id:int}/comment/reply")]
    public async Task<ActionResult<OrderDto>> ReplyToOrderComment(int id, [FromBody] ReplyTextDto dto, CancellationToken ct)
    {
        var reply = (dto?.Reply ?? string.Empty).Trim();
        if (reply.Length < 3) return BadRequest("Resposta demasiado curta");
        if (reply.Length > 2000) return BadRequest("Resposta demasiado longa");

        var order = await context.Orders.FirstOrDefaultAsync(o => o.Id == id, ct);
        if (order == null) return NotFound();

        if (string.IsNullOrWhiteSpace(order.CustomerComment))
            return BadRequest("Esta encomenda não tem comentário do cliente");

        order.AdminCommentReply = reply;
        order.AdminCommentRepliedAt = DateTime.UtcNow;

        var saved = await context.SaveChangesAsync(ct) > 0;
        if (!saved) return BadRequest("Problem saving reply");

        await notificationService.TryCreateForEmailAsync(
            order.BuyerEmail,
            "Resposta da loja",
            $"Respondemos ao seu comentário na encomenda #{order.Id}.",
            $"/orders/{order.Id}",
            ct);

        await TrySendOrderCommentReplyEmail(order, reply);

        var updated = await context.Orders
            .ProjectToDto()
            .Where(o => o.Id == id)
            .FirstOrDefaultAsync(ct);

        return updated == null ? Ok() : Ok(updated);
    }

    private async Task<List<string>> GetAdminEmailsAsync()
    {
        try
        {
            var admins = await userManager.GetUsersInRoleAsync("Admin");
            return admins
                .Select(a => (a.Email ?? string.Empty).Trim())
                .Where(e => !string.IsNullOrWhiteSpace(e))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load admin emails");
            return [];
        }
    }

    private async Task TryNotifyAdminsOrderComment(Order order)
    {
        try
        {
            var adminEmails = await GetAdminEmailsAsync();
            if (adminEmails.Count == 0) return;

            var frontend = (emailOptions.Value.FrontendUrl ?? string.Empty).TrimEnd('/');
            var orderUrl = string.IsNullOrWhiteSpace(frontend) ? string.Empty : $"{frontend}/admin/sales/{order.Id}";

            var subject = $"[Avaliação] Encomenda #{order.Id} - Comentário do cliente";
            var html = $"""
                <div style='font-family: Arial, sans-serif; line-height: 1.5'>
                  <h2>Restore</h2>
                  <p><strong>Novo comentário</strong> do cliente na encomenda <strong>#{order.Id}</strong>.</p>
                  <p><strong>Cliente:</strong> {System.Net.WebUtility.HtmlEncode(order.BuyerEmail)}</p>
                  <p><strong>Comentário:</strong></p>
                  <div style='white-space: pre-wrap; border: 1px solid #ddd; padding: 8px; border-radius: 6px'>
                    {System.Net.WebUtility.HtmlEncode(order.CustomerComment ?? string.Empty)}
                  </div>
                  {(string.IsNullOrWhiteSpace(orderUrl) ? string.Empty : $"<p>Ver venda: <a href=\"{orderUrl}\">{orderUrl}</a></p>")}
                </div>
                """;

            foreach (var adminEmail in adminEmails)
            {
                await notificationService.TryCreateForEmailAsync(
                    adminEmail,
                    "Novo comentário do cliente",
                    $"Novo comentário na encomenda #{order.Id}.",
                    $"/admin/sales/{order.Id}");
            }

            foreach (var adminEmail in adminEmails)
            {
                await emailService.SendEmailAsync(adminEmail, subject, html, replyToEmail: order.BuyerEmail);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to notify admins about order comment for order {OrderId}", order.Id);
        }
    }

    [HttpGet("{id:int}/incident")]
    public async Task<ActionResult<OrderIncidentDto>> GetOrderIncident(int id, CancellationToken ct)
    {
        var isAdmin = User.IsInRole("Admin");
        var email = User.GetEmail();
        if (string.IsNullOrWhiteSpace(email)) return Unauthorized();

        var order = await context.Orders
            .AsNoTracking()
            .FirstOrDefaultAsync(o => o.Id == id && (isAdmin || o.BuyerEmail == email), ct);

        if (order == null) return NotFound();

        var incident = await context.OrderIncidents
            .AsNoTracking()
            .Include(i => i.Attachments)
            .FirstOrDefaultAsync(i => i.OrderId == id, ct);

        if (incident == null)
        {
            return new OrderIncidentDto
            {
                Id = null,
                OrderId = order.Id,
                BuyerEmail = order.BuyerEmail,
                Status = IncidentStatus.None,
                Attachments = []
            };
        }

        return new OrderIncidentDto
        {
            Id = incident.Id,
            OrderId = incident.OrderId,
            BuyerEmail = incident.BuyerEmail,
            Status = incident.Status,
            ProductId = incident.ProductId,
            Description = incident.Description,
            AdminReply = incident.AdminReply,
            AdminRepliedAt = incident.AdminRepliedAt,
            CreatedAt = incident.CreatedAt,
            ResolvedAt = incident.ResolvedAt,
            Attachments = incident.Attachments
                .OrderBy(a => a.CreatedAt)
                .Select(a => new OrderIncidentAttachmentDto
                {
                    Id = a.Id,
                    OriginalFileName = a.OriginalFileName,
                    ContentType = a.ContentType,
                    Size = a.Size,
                    CreatedAt = a.CreatedAt
                })
                .ToList()
        };
    }

    [Authorize(Roles = "Admin")]
    [HttpPut("{id:int}/incident/reply")]
    public async Task<ActionResult> ReplyToOrderIncident(int id, [FromBody] ReplyTextDto dto, CancellationToken ct)
    {
        var reply = (dto?.Reply ?? string.Empty).Trim();
        if (reply.Length < 3) return BadRequest("Resposta demasiado curta");
        if (reply.Length > 2000) return BadRequest("Resposta demasiado longa");

        var incident = await context.OrderIncidents
            .FirstOrDefaultAsync(i => i.OrderId == id, ct);

        if (incident == null) return NotFound();

        incident.AdminReply = reply;
        incident.AdminRepliedAt = DateTime.UtcNow;

        var saved = await context.SaveChangesAsync(ct) > 0;
        if (!saved) return BadRequest("Problem saving reply");

        await notificationService.TryCreateForEmailAsync(
            incident.BuyerEmail,
            "Resposta do suporte",
            $"Respondemos ao seu incidente da encomenda #{incident.OrderId}.",
            $"/orders/{incident.OrderId}",
            ct);

        await TrySendIncidentReplyEmail(incident, reply);
        return Ok();
    }

    [HttpPost("{id:int}/incident/open")]
    [RequestSizeLimit(25_000_000)]
    public async Task<ActionResult> OpenOrderIncident(int id, [FromForm] OpenOrderIncidentDto dto, CancellationToken ct)
    {
        if (dto == null) return BadRequest("Dados inválidos");

        var email = User.GetEmail();
        if (string.IsNullOrWhiteSpace(email)) return Unauthorized();

        var description = (dto.Description ?? string.Empty).Trim();
        if (description.Length < 10) return BadRequest("Descrição demasiado curta");
        if (description.Length > 2000) return BadRequest("Descrição demasiado longa");

        var order = await context.Orders
            .Include(o => o.OrderItems)
            .FirstOrDefaultAsync(o => o.Id == id && o.BuyerEmail == email, ct);

        if (order == null) return NotFound();

        if (order.OrderStatus == OrderStatus.Pending || order.OrderStatus == OrderStatus.PaymentFailed)
            return BadRequest("Só pode abrir incidente após a compra estar concluída");

        var existing = await context.OrderIncidents.AnyAsync(i => i.OrderId == id, ct);
        if (existing) return BadRequest("Já existe um incidente para esta encomenda");

        if (dto.ProductId.HasValue)
        {
            var productId = dto.ProductId.Value;
            var existsInOrder = order.OrderItems.Any(oi => oi.ItemOrdered.ProductId == productId);
            if (!existsInOrder) return BadRequest("Produto inválido para esta encomenda");
        }

        var incident = new OrderIncident
        {
            OrderId = order.Id,
            BuyerEmail = order.BuyerEmail,
            Status = IncidentStatus.Open,
            ProductId = dto.ProductId,
            Description = description,
            CreatedAt = DateTime.UtcNow
        };

        context.OrderIncidents.Add(incident);
        await context.SaveChangesAsync(ct);

        await notificationService.TryCreateForEmailAsync(
            order.BuyerEmail,
            "Incidente aberto",
            $"O seu incidente para a encomenda #{order.Id} foi registado.",
            $"/orders/{order.Id}",
            ct);

        var writtenFiles = new List<string>();
        try
        {
            const int maxFiles = 10;
            const long maxFileSize = 10_000_000; // 10MB

            var files = dto.Files ?? [];
            if (files.Count > maxFiles) return BadRequest("Demasiados ficheiros");

            var baseRel = $"Uploads/incidents/{order.Id}/{incident.Id}";
            var baseAbs = Path.Combine(env.ContentRootPath, "Uploads", "incidents", order.Id.ToString(), incident.Id.ToString());
            Directory.CreateDirectory(baseAbs);

            foreach (var file in files)
            {
                if (file == null || file.Length <= 0) continue;
                if (file.Length > maxFileSize) return BadRequest("Ficheiro demasiado grande");

                var original = Path.GetFileName(file.FileName);
                if (string.IsNullOrWhiteSpace(original)) original = "anexo";

                var ext = Path.GetExtension(original);
                var stored = $"{Guid.NewGuid():N}{ext}";
                var relPath = $"{baseRel}/{stored}";
                var absPath = Path.Combine(baseAbs, stored);

                await using (var stream = System.IO.File.Create(absPath))
                {
                    await file.CopyToAsync(stream, ct);
                }

                writtenFiles.Add(absPath);

                context.OrderIncidentAttachments.Add(new OrderIncidentAttachment
                {
                    OrderIncidentId = incident.Id,
                    OriginalFileName = original,
                    StoredFileName = stored,
                    ContentType = string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType,
                    Size = file.Length,
                    RelativePath = relPath,
                    CreatedAt = DateTime.UtcNow
                });
            }

            await context.SaveChangesAsync(ct);
        }
        catch
        {
            // best-effort cleanup
            try
            {
                foreach (var p in writtenFiles)
                {
                    if (System.IO.File.Exists(p)) System.IO.File.Delete(p);
                }
            }
            catch { }

            // remove incident record if attachments failed catastrophically
            try
            {
                var toRemove = await context.OrderIncidents.FirstOrDefaultAsync(i => i.Id == incident.Id, ct);
                if (toRemove != null)
                {
                    context.OrderIncidents.Remove(toRemove);
                    await context.SaveChangesAsync(ct);
                }
            }
            catch { }

            throw;
        }

        await TrySendIncidentOpenedEmail(order, incident);

        await TrySendIncidentOpenedBuyerConfirmationEmail(order, incident);

        return NoContent();
    }

    private async Task TrySendIncidentOpenedBuyerConfirmationEmail(Order order, OrderIncident incident)
    {
        try
        {
            var frontend = (emailOptions.Value.FrontendUrl ?? string.Empty).TrimEnd('/');
            var orderUrl = string.IsNullOrWhiteSpace(frontend) ? string.Empty : $"{frontend}/orders/{order.Id}";

            var subject = $"Incidente registado (Encomenda #{order.Id})";
            var html = $"""
                <div style='font-family: Arial, sans-serif; line-height: 1.5'>
                  <h2>Restore</h2>
                  <p>O seu incidente para a encomenda <strong>#{order.Id}</strong> foi registado com sucesso.</p>
                  <p><strong>Descrição:</strong></p>
                  <div style='white-space: pre-wrap; border: 1px solid #ddd; padding: 8px; border-radius: 6px'>
                    {System.Net.WebUtility.HtmlEncode(incident.Description)}
                  </div>
                  {(string.IsNullOrWhiteSpace(orderUrl) ? string.Empty : $"<p>Ver encomenda: <a href=\"{orderUrl}\">{orderUrl}</a></p>")}
                </div>
                """;

            await emailService.SendEmailAsync(order.BuyerEmail, subject, html);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to send incident opened confirmation to buyer for order {OrderId}", order.Id);
        }
    }

    [Authorize(Roles = "Admin")]
    [HttpPut("{id:int}/incident/resolve")]
    public async Task<ActionResult> ResolveOrderIncident(int id, CancellationToken ct)
    {
        var incident = await context.OrderIncidents
            .Include(i => i.Attachments)
            .FirstOrDefaultAsync(i => i.OrderId == id, ct);

        if (incident == null) return NotFound();
        if (incident.Status == IncidentStatus.Resolved) return NoContent();

        incident.Status = IncidentStatus.Resolved;
        incident.ResolvedAt = DateTime.UtcNow;

        var saved = await context.SaveChangesAsync(ct) > 0;
        if (!saved) return BadRequest("Problem resolving incident");

        await notificationService.TryCreateForEmailAsync(
            incident.BuyerEmail,
            "Incidente resolvido",
            $"O incidente da encomenda #{incident.OrderId} foi marcado como resolvido.",
            $"/orders/{incident.OrderId}",
            ct);

        // Notify buyer
        await TrySendIncidentResolvedEmail(incident);

        return NoContent();
    }

    [HttpGet("{orderId:int}/incident/attachments/{attachmentId:int}")]
    public async Task<IActionResult> DownloadIncidentAttachment(int orderId, int attachmentId, CancellationToken ct)
    {
        var isAdmin = User.IsInRole("Admin");
        var email = User.GetEmail();
        if (string.IsNullOrWhiteSpace(email)) return Unauthorized();

        var order = await context.Orders
            .AsNoTracking()
            .FirstOrDefaultAsync(o => o.Id == orderId && (isAdmin || o.BuyerEmail == email), ct);

        if (order == null) return NotFound();

        var attachment = await context.OrderIncidentAttachments
            .AsNoTracking()
            .Include(a => a.OrderIncident)
            .FirstOrDefaultAsync(a => a.Id == attachmentId && a.OrderIncident != null && a.OrderIncident.OrderId == orderId, ct);

        if (attachment == null) return NotFound();

        var abs = Path.Combine(env.ContentRootPath, attachment.RelativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!System.IO.File.Exists(abs)) return NotFound();

        var contentType = string.IsNullOrWhiteSpace(attachment.ContentType) ? "application/octet-stream" : attachment.ContentType;
        return PhysicalFile(abs, contentType, fileDownloadName: attachment.OriginalFileName);
    }

    private async Task TrySendIncidentOpenedEmail(Order order, OrderIncident incident)
    {
        try
        {
            var adminEmails = await GetAdminEmailsAsync();
            if (adminEmails.Count == 0)
            {
                var fallback = (emailOptions.Value.AdminEmail ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(fallback)) fallback = (emailOptions.Value.FromEmail ?? string.Empty).Trim();
                if (!string.IsNullOrWhiteSpace(fallback)) adminEmails.Add(fallback);
            }

                foreach (var to in adminEmails)
                {
                    await notificationService.TryCreateForEmailAsync(
                        to,
                        "Incidente aberto",
                        $"Incidente aberto na encomenda #{order.Id}.",
                        $"/admin/sales/{order.Id}");
                }

            if (adminEmails.Count == 0)
            {
                logger.LogWarning("Incident email not sent: no admin recipients configured.");
                return;
            }

            var frontend = (emailOptions.Value.FrontendUrl ?? string.Empty).TrimEnd('/');
            var orderUrl = string.IsNullOrWhiteSpace(frontend) ? string.Empty : $"{frontend}/admin/sales/{order.Id}";

            var subject = $"[Incidente] Encomenda #{order.Id} - Incidente aberto";

            var items = string.Join("", order.OrderItems.Select(oi => $"<li>{System.Net.WebUtility.HtmlEncode(oi.ItemOrdered.Name)} x {oi.Quantity}</li>"));
            var productInfo = incident.ProductId.HasValue
                ? $"<p><strong>Produto:</strong> {incident.ProductId.Value}</p>"
                : string.Empty;

            var attachmentsCount = await context.OrderIncidentAttachments
                .AsNoTracking()
                .CountAsync(a => a.OrderIncidentId == incident.Id, CancellationToken.None);

            var html = $"""
                <div style='font-family: Arial, sans-serif; line-height: 1.5'>
                  <h2>Restore</h2>
                  <p><strong>Incidente aberto</strong> na encomenda #{order.Id}</p>
                  <p><strong>Cliente:</strong> {System.Net.WebUtility.HtmlEncode(order.BuyerEmail)}</p>
                  {productInfo}
                  <p><strong>Descrição:</strong></p>
                  <div style='white-space: pre-wrap; border: 1px solid #ddd; padding: 8px; border-radius: 6px'>
                    {System.Net.WebUtility.HtmlEncode(incident.Description)}
                  </div>
                  <p><strong>Artigos:</strong></p>
                  <ul>{items}</ul>
                  <p><strong>Anexos:</strong> {attachmentsCount}</p>
                  {(string.IsNullOrWhiteSpace(orderUrl) ? string.Empty : $"<p>Ver venda: <a href=\"{orderUrl}\">{orderUrl}</a></p>")}
                </div>
                """;

                        foreach (var to in adminEmails)
                        {
                                await emailService.SendEmailAsync(to, subject, html, replyToEmail: order.BuyerEmail);
                        }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to send incident opened email for order {OrderId}", order.Id);
        }
    }

    private async Task TrySendIncidentResolvedEmail(OrderIncident incident)
    {
        try
        {
            var frontend = (emailOptions.Value.FrontendUrl ?? string.Empty).TrimEnd('/');
            var orderUrl = string.IsNullOrWhiteSpace(frontend) ? string.Empty : $"{frontend}/orders/{incident.OrderId}";

            var subject = $"[Incidente] Encomenda #{incident.OrderId} - Incidente resolvido";
            var html = $"""
                <div style='font-family: Arial, sans-serif; line-height: 1.5'>
                  <h2>Restore</h2>
                  <p>O seu incidente na encomenda <strong>#{incident.OrderId}</strong> foi marcado como <strong>Resolvido</strong>.</p>
                  {(string.IsNullOrWhiteSpace(orderUrl) ? string.Empty : $"<p>Ver encomenda: <a href=\"{orderUrl}\">{orderUrl}</a></p>")}
                </div>
                """;

            await emailService.SendEmailAsync(incident.BuyerEmail, subject, html);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to send incident resolved email for order {OrderId}", incident.OrderId);
        }
    }

    private async Task TrySendIncidentReplyEmail(OrderIncident incident, string reply)
    {
        try
        {
            var frontend = (emailOptions.Value.FrontendUrl ?? string.Empty).TrimEnd('/');
            var orderUrl = string.IsNullOrWhiteSpace(frontend) ? string.Empty : $"{frontend}/orders/{incident.OrderId}";

            var subject = $"[Incidente] Encomenda #{incident.OrderId} - Resposta do suporte";
            var html = $"""
                <div style='font-family: Arial, sans-serif; line-height: 1.5'>
                  <h2>Restore</h2>
                  <p>Recebeu uma resposta ao seu incidente na encomenda <strong>#{incident.OrderId}</strong>:</p>
                  <div style='white-space: pre-wrap; border: 1px solid #ddd; padding: 8px; border-radius: 6px'>
                    {System.Net.WebUtility.HtmlEncode(reply)}
                  </div>
                  {(string.IsNullOrWhiteSpace(orderUrl) ? string.Empty : $"<p>Ver encomenda: <a href=\"{orderUrl}\">{orderUrl}</a></p>")}
                </div>
                """;

            await emailService.SendEmailAsync(incident.BuyerEmail, subject, html);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to send incident reply email for order {OrderId}", incident.OrderId);
        }
    }

    private async Task TrySendOrderCommentReplyEmail(Order order, string reply)
    {
        try
        {
            var frontend = (emailOptions.Value.FrontendUrl ?? string.Empty).TrimEnd('/');
            var orderUrl = string.IsNullOrWhiteSpace(frontend) ? string.Empty : $"{frontend}/orders/{order.Id}";

            var subject = $"Encomenda #{order.Id} - Resposta ao seu comentário";
            var html = $"""
                <div style='font-family: Arial, sans-serif; line-height: 1.5'>
                  <h2>Restore</h2>
                  <p>Recebeu uma resposta ao seu comentário da encomenda <strong>#{order.Id}</strong>:</p>
                  <div style='white-space: pre-wrap; border: 1px solid #ddd; padding: 8px; border-radius: 6px'>
                    {System.Net.WebUtility.HtmlEncode(reply)}
                  </div>
                  {(string.IsNullOrWhiteSpace(orderUrl) ? string.Empty : $"<p>Ver encomenda: <a href=\"{orderUrl}\">{orderUrl}</a></p>")}
                </div>
                """;

            await emailService.SendEmailAsync(order.BuyerEmail, subject, html);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to send order comment reply email for order {OrderId}", order.Id);
        }
    }

    private async Task TrySendStatusEmail(Order order, OrderStatus newStatus)
    {
        try
        {
            var frontend = emailOptions.Value.FrontendUrl?.TrimEnd('/') ?? string.Empty;
            var orderUrl = string.IsNullOrWhiteSpace(frontend) ? string.Empty : $"{frontend}/orders/{order.Id}";

            var fullOrder = await context.Orders
                .AsNoTracking()
                .Include(o => o.OrderItems)
                .FirstOrDefaultAsync(o => o.Id == order.Id);

            var descMap = new Dictionary<int, string?>();
            if (fullOrder?.OrderItems?.Count > 0)
            {
                var productIds = fullOrder.OrderItems.Select(x => x.ItemOrdered.ProductId).Distinct().ToList();
                var meta = await context.Products
                    .AsNoTracking()
                    .Where(p => productIds.Contains(p.Id))
                    .Select(p => new { p.Id, Text = p.Subtitle ?? p.Description })
                    .ToListAsync();
                descMap = meta.ToDictionary(x => x.Id, x => x.Text);
            }

            var orderSummary = fullOrder == null ? string.Empty : EmailTemplate.RenderOrderSummary(fullOrder, frontend, descMap, includeTotals: newStatus != OrderStatus.ReviewRequested);

            if (newStatus == OrderStatus.Cancelled)
            {
                var pdfBytes = await invoicePdfService.GenerateCancellationPdfAsync(order, CancellationToken.None);

                var subjectCancelled = $"Encomenda #{order.Id}: Anulada";
                var refundText = order.RefundedAt.HasValue
                    ? "A devolução foi criada no Stripe."
                    : "A devolução foi iniciada e poderá demorar alguns dias úteis.";

                var htmlCancelled = $"""
                    <div style='font-family: Arial, sans-serif; line-height: 1.5'>
                      <h2>Restore</h2>
                      <p>A sua encomenda <strong>#{order.Id}</strong> foi anulada.</p>
                      <p>{refundText}</p>
                      {(string.IsNullOrWhiteSpace(orderUrl) ? string.Empty : $"<p>Ver encomenda: <a href=\"{orderUrl}\">{orderUrl}</a></p>")}
                      <p>Segue em anexo o PDF de confirmação de anulação.</p>
                                            {orderSummary}
                    </div>
                    """;

                await emailService.SendEmailWithAttachmentsAsync(order.BuyerEmail, subjectCancelled, htmlCancelled,
                    new[]
                    {
                        new EmailAttachment
                        {
                            FileName = $"anulacao-{order.Id}.pdf",
                            ContentType = "application/pdf",
                            Content = pdfBytes
                        }
                    });

                return;
            }

            var subject = newStatus switch
            {
                OrderStatus.PaymentReceived => $"Encomenda #{order.Id}: Em processamento",
                OrderStatus.Processing => $"Encomenda #{order.Id}: Em processamento",
                OrderStatus.Processed => $"Encomenda #{order.Id}: Processado",
                OrderStatus.Shipped => $"Encomenda #{order.Id}: Enviado",
                OrderStatus.Delivered => $"Encomenda #{order.Id}: Entregue",
                OrderStatus.Cancelled => $"Encomenda #{order.Id}: Cancelado",
                OrderStatus.ReviewRequested => $"Encomenda #{order.Id}: Para avaliação",
                OrderStatus.Completed => $"Encomenda #{order.Id}: Concluído",
                _ => $"Atualização da encomenda #{order.Id}"
            };

            var headline = newStatus switch
            {
                OrderStatus.PaymentReceived => "A sua encomenda está a ser processada.",
                OrderStatus.Processing => "A sua encomenda está em processamento.",
                OrderStatus.Processed => "A sua encomenda foi processada.",
                OrderStatus.Shipped => "A sua encomenda foi enviada.",
                OrderStatus.Delivered => "A sua encomenda foi entregue.",
                OrderStatus.Cancelled => "A sua encomenda foi cancelada.",
                OrderStatus.ReviewRequested => "Como correu a sua encomenda? Pode deixar um comentário.",
                OrderStatus.Completed => "A sua encomenda foi concluída.",
                _ => "A sua encomenda foi atualizada."
            };

            var extra = newStatus == OrderStatus.ReviewRequested && !string.IsNullOrWhiteSpace(orderUrl)
                ? $"<p>Deixe o seu comentário aqui: <a href=\"{orderUrl}\">{orderUrl}</a></p>"
                : (!string.IsNullOrWhiteSpace(orderUrl) ? $"<p>Ver encomenda: <a href=\"{orderUrl}\">{orderUrl}</a></p>" : string.Empty);

            var btnOrder = string.IsNullOrWhiteSpace(orderUrl)
                ? string.Empty
                : EmailTemplate.PrimaryButton(orderUrl, newStatus == OrderStatus.ReviewRequested ? "Deixar feedback" : "Ver encomenda");

            var html = $"""
                <div style='font-family: Arial, sans-serif; line-height: 1.5'>
                  <h2>Restore</h2>
                  <p>{headline}</p>
                  <p><strong>Encomenda:</strong> #{order.Id}</p>
                                    {(string.IsNullOrWhiteSpace(btnOrder) ? extra : $"<p style='margin:0 0 12px'>{btnOrder}</p>")}
                                    {orderSummary}
                </div>
                """;

            await emailService.SendEmailAsync(order.BuyerEmail, subject, html);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to send order status email for order {OrderId}", order.Id);
        }
    }

    [HttpPost]
    public async Task<ActionResult<Order>> CreateOrder(CreateOrderDto orderDto)
    {
        var basket = await context.Baskets.GetBasketWithItems(Request.Cookies["basketId"]);

        if (basket == null || basket.Items.Count == 0 || string.IsNullOrEmpty(basket.PaymentIntentId))
            return BadRequest("Basket is empty or not found");

        var items = CreateOrderItems(basket.Items);
        if (items == null) return BadRequest("Some items out of stock");
        // items.Price is stored as cents (long) in the OrderItem entity, so subtotal is in cents
        var subtotal = items.Sum(x => x.Price * x.Quantity);
        var deliveryFee = await CalculateDeliveryFeeAsync(subtotal);

        // Product-level discount is already reflected in the item prices/subtotal.
        // We still compute it here for display/analytics purposes, but it must NOT be subtracted again from totals.
        long productDiscount = 0;
        foreach (var bItem in basket.Items)
        {
            var product = bItem.Product;
            var basePrice = bItem.ProductVariant?.PriceOverride ?? product.Price;

            decimal originalUnit = basePrice;
            decimal finalUnit = product.PromotionalPrice ?? basePrice;

            if (product.DiscountPercentage.HasValue && product.PromotionalPrice == null)
            {
                var d = Math.Max(0, Math.Min(100, product.DiscountPercentage.Value));
                finalUnit = finalUnit * (1 - (d / 100M));
            }

            bool priceLikelyInCents = (product.PromotionalPrice ?? basePrice) > 1000M;
            long originalCents = priceLikelyInCents ? (long)Math.Round(originalUnit) : (long)Math.Round(originalUnit * 100M);
            long finalCents = priceLikelyInCents ? (long)Math.Round(finalUnit) : (long)Math.Round(finalUnit * 100M);

            productDiscount += Math.Max(0, (originalCents - finalCents)) * bItem.Quantity;
        }

        long couponDiscount = 0;
        if (basket.Coupon != null)
        {
            couponDiscount = await discountService.CalculateDiscountFromAmount(basket.Coupon, subtotal);
        }

        // NOTE: Product-level discounts are already applied when building OrderItems (see CreateOrderItems)
        // and therefore reflected in `subtotal`. Only coupon discounts should be stored in Order.Discount.
        long discount = couponDiscount;

        // Diagnostic logging to help investigate discount computation mismatches
        try
        {
            logger.LogInformation("Creating order: subtotal={Subtotal}, productDiscount={ProductDiscount}, couponDiscount={CouponDiscount}, delivery={DeliveryFee}, total={Total}",
                subtotal, productDiscount, couponDiscount, deliveryFee, subtotal - couponDiscount + deliveryFee);

            foreach (var bItem in basket.Items)
            {
                logger.LogInformation("Basket item {ProductId} price={Price} promotional={PromotionalPrice} discountPercentage={DiscountPercentage} quantity={Quantity}",
                    bItem.ProductId, bItem.Product.Price, bItem.Product.PromotionalPrice, bItem.Product.DiscountPercentage, bItem.Quantity);
            }
        }
        catch { /* swallow logging errors to avoid impacting order creation */ }

        var isNewOrder = false;

        var order = await context.Orders
            .Include(x => x.OrderItems)
            .FirstOrDefaultAsync(x => x.PaymentIntentId == basket.PaymentIntentId);

        if (order == null)
        {
                order = new Order
                {
                    OrderItems = items,
                    BuyerEmail = User.GetEmail(),
                    ShippingAddress = orderDto.ShippingAddress,
                    BillingName = string.IsNullOrWhiteSpace(orderDto.BillingName) ? null : orderDto.BillingName.Trim(),
                    BillingTaxId = string.IsNullOrWhiteSpace(orderDto.BillingTaxId) ? null : orderDto.BillingTaxId.Trim(),
                    BillingLine1 = string.IsNullOrWhiteSpace(orderDto.BillingLine1) ? null : orderDto.BillingLine1.Trim(),
                    BillingLine2 = string.IsNullOrWhiteSpace(orderDto.BillingLine2) ? null : orderDto.BillingLine2.Trim(),
                    BillingCity = string.IsNullOrWhiteSpace(orderDto.BillingCity) ? null : orderDto.BillingCity.Trim(),
                    BillingState = string.IsNullOrWhiteSpace(orderDto.BillingState) ? null : orderDto.BillingState.Trim(),
                    BillingPostalCode = string.IsNullOrWhiteSpace(orderDto.BillingPostalCode) ? null : orderDto.BillingPostalCode.Trim(),
                    BillingCountry = string.IsNullOrWhiteSpace(orderDto.BillingCountry) ? null : orderDto.BillingCountry.Trim(),
                    DeliveryFee = deliveryFee,
                    Subtotal = subtotal,
                    ProductDiscount = productDiscount,
                    Discount = discount,
                    PaymentSummary = orderDto.PaymentSummary,
                    PaymentIntentId = basket.PaymentIntentId
                };

            context.Orders.Add(order);
            isNewOrder = true;
        }
        else 
        {
            order.OrderItems = items;

            // Keep billing details in sync if the client provided them.
            if (!string.IsNullOrWhiteSpace(orderDto.BillingName)) order.BillingName = orderDto.BillingName.Trim();
            if (!string.IsNullOrWhiteSpace(orderDto.BillingTaxId)) order.BillingTaxId = orderDto.BillingTaxId.Trim();
            if (!string.IsNullOrWhiteSpace(orderDto.BillingLine1)) order.BillingLine1 = orderDto.BillingLine1.Trim();
            if (!string.IsNullOrWhiteSpace(orderDto.BillingLine2)) order.BillingLine2 = orderDto.BillingLine2.Trim();
            if (!string.IsNullOrWhiteSpace(orderDto.BillingCity)) order.BillingCity = orderDto.BillingCity.Trim();
            if (!string.IsNullOrWhiteSpace(orderDto.BillingState)) order.BillingState = orderDto.BillingState.Trim();
            if (!string.IsNullOrWhiteSpace(orderDto.BillingPostalCode)) order.BillingPostalCode = orderDto.BillingPostalCode.Trim();
            if (!string.IsNullOrWhiteSpace(orderDto.BillingCountry)) order.BillingCountry = orderDto.BillingCountry.Trim();
        }

        // If the buyer asked for a fiscal invoice (NIF) but didn't provide explicit billing address,
        // default billing details to the shipping address. This keeps the payload minimal on the client.
        if (!string.IsNullOrWhiteSpace(order.BillingTaxId))
        {
            order.BillingName ??= order.ShippingAddress.Name;
            order.BillingLine1 ??= order.ShippingAddress.Line1;
            order.BillingLine2 ??= order.ShippingAddress.Line2;
            order.BillingCity ??= order.ShippingAddress.City;
            order.BillingState ??= order.ShippingAddress.State;
            order.BillingPostalCode ??= order.ShippingAddress.PostalCode;
            order.BillingCountry ??= order.ShippingAddress.Country;
        }
        
        // verify payment intent status with Stripe so the order reflects payment status
        try
        {
            if (!string.IsNullOrEmpty(basket.PaymentIntentId))
            {
                StripeConfiguration.ApiKey = config["StripeSettings:SecretKey"];
                var paymentService = new PaymentIntentService();
                var intent = await paymentService.GetAsync(basket.PaymentIntentId!);

                if (intent != null)
                {
                    if (intent.Status == "succeeded")
                    {
                        var intentAmount = intent.Amount;
                        if (Math.Abs(order.GetTotal() - intentAmount) > 1)
                        {
                            logger.LogWarning("Payment mismatch for order {OrderId} during creation: orderTotal={OrderTotal}, intentAmount={IntentAmount} - marking as PaymentReceived.", order.Id, order.GetTotal(), intentAmount);
                        }

                        logger.LogInformation("Payment received for order {OrderId} during creation: intent amount {Amount}", order.Id, intentAmount);
                        order.OrderStatus = OrderStatus.PaymentReceived;

                        // Since payment is already confirmed, record sales counts now.
                        await IncrementProductSalesCountsAsync(order);
                    }
                    else if (intent.Status == "requires_payment_method" || intent.Status == "requires_confirmation" || intent.Status == "requires_action")
                    {
                        order.OrderStatus = OrderStatus.Pending;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to verify PaymentIntent for order creation");
            // keep default Pending if verification fails
        }

        var result = await context.SaveChangesAsync() > 0;

        if (!result) return BadRequest("Problem creating order");

        if (isNewOrder)
        {
            await TryNotifyAdminsOrderCreated(order);
        }

        if (order.OrderStatus == OrderStatus.PaymentReceived)
        {
            await TrySendStatusEmail(order, OrderStatus.PaymentReceived);
            await TrySendReceiptEmailIfNeeded(order, CancellationToken.None);

            await notificationService.TryCreateForEmailAsync(
                order.BuyerEmail,
                "Pagamento confirmado",
                $"Pagamento confirmado. A encomenda #{order.Id} está a ser processada.",
                $"/orders/{order.Id}");
        }

        return CreatedAtAction(nameof(GetOrderDetails), new { id = order.Id }, order.ToDto());
    }

    private async Task TryNotifyAdminsOrderCreated(Order order)
    {
        try
        {
            var adminEmails = await GetAdminEmailsAsync();
            if (adminEmails.Count == 0) return;

            foreach (var to in adminEmails)
            {
                await notificationService.TryCreateForEmailAsync(
                    to,
                    "Nova encomenda",
                    $"Nova encomenda #{order.Id} criada ({order.OrderStatus}).",
                    $"/admin/sales/{order.Id}");
            }

            var frontend = (emailOptions.Value.FrontendUrl ?? string.Empty).TrimEnd('/');
            var orderUrl = string.IsNullOrWhiteSpace(frontend) ? string.Empty : $"{frontend}/admin/sales/{order.Id}";

            var subject = $"[Venda] Nova encomenda #{order.Id}";
            var html = $"""
                <div style='font-family: Arial, sans-serif; line-height: 1.5'>
                  <h2>Restore</h2>
                  <p>Foi criada uma nova encomenda <strong>#{order.Id}</strong>.</p>
                  <p><strong>Estado:</strong> {order.OrderStatus}</p>
                  <p><strong>Cliente:</strong> {System.Net.WebUtility.HtmlEncode(order.BuyerEmail)}</p>
                  {(string.IsNullOrWhiteSpace(orderUrl) ? string.Empty : $"<p>Ver venda: <a href=\"{orderUrl}\">{orderUrl}</a></p>")}
                </div>
                """;

            foreach (var to in adminEmails)
            {
                await emailService.SendEmailAsync(to, subject, html, replyToEmail: order.BuyerEmail);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to notify admins about new order {OrderId}", order.Id);
        }
    }

    private async Task IncrementProductSalesCountsAsync(Order order)
    {
        // Best-effort: don't block order creation if this fails.
        try
        {
            foreach (var item in order.OrderItems)
            {
                var productId = item.ItemOrdered.ProductId;
                var product = await context.Products.FindAsync(productId);
                if (product == null) continue;

                product.SalesCount += item.Quantity;
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to increment SalesCount for order {OrderId}", order.Id);
        }
    }

    private async Task<long> CalculateDeliveryFeeAsync(long subtotal)
    {
        try
        {
            var shipping = await context.ShippingRates.AsNoTracking().FirstOrDefaultAsync();

            var rateEuros = shipping?.Rate ?? DefaultRate;
            var thresholdEuros = shipping?.FreeShippingThreshold ?? DefaultFreeShippingThreshold;

            var rateCents = (long)Math.Round(rateEuros * 100m);
            var thresholdCents = (long)Math.Round(thresholdEuros * 100m);

            if (thresholdCents <= 0) return 0;
            return subtotal > thresholdCents ? 0 : Math.Max(0, rateCents);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load ShippingRate while calculating delivery fee. Falling back to defaults.");
            return subtotal > 10000 ? 0 : 500;
        }
    }

    private List<OrderItem>? CreateOrderItems(List<BasketItem> items)
    {
        var orderItems = new List<OrderItem>();

        foreach (var item in items)
        {
            if (item.ProductVariantId.HasValue)
            {
                if (item.ProductVariant == null) return null;
                if (item.ProductVariant.QuantityInStock < item.Quantity) return null;
            }
            else
            {
                if (item.Product.QuantityInStock < item.Quantity) return null;
            }

            // Determine the unit price taking promotional price or product-level discount into account.
            var basePrice = item.ProductVariant?.PriceOverride ?? item.Product.Price;
            decimal unitPrice = item.Product.PromotionalPrice ?? basePrice;

            if (item.Product.DiscountPercentage.HasValue && item.Product.PromotionalPrice == null)
            {
                var d = Math.Max(0, Math.Min(100, item.Product.DiscountPercentage.Value));
                unitPrice = unitPrice * (1 - (d / 100M));
            }

            // Heuristic: if unitPrice looks like cents (very large), treat as cents already; otherwise convert euros -> cents
            bool priceLikelyInCents = unitPrice > 1000M;
            long priceInCents = priceLikelyInCents ? (long)Math.Round(unitPrice) : (long)Math.Round(unitPrice * 100M);

            var orderItem = new OrderItem
            {
                ItemOrdered = new ProductItemOrdered
                {
                    ProductId = item.ProductId,
                    ProductVariantId = item.ProductVariantId,
                    VariantColor = item.ProductVariant?.Color,
                    PictureUrl = item.ProductVariant?.PictureUrl ?? item.Product.PictureUrl ?? string.Empty,
                    Name = item.Product.Name
                },
                // store price in cents for orders (long)
                Price = priceInCents,
                Quantity = item.Quantity
            };
            orderItems.Add(orderItem);

            if (item.ProductVariant != null)
            {
                item.ProductVariant.QuantityInStock -= item.Quantity;
            }

            // Keep the product-level QuantityInStock consistent for existing UIs (total stock).
            item.Product.QuantityInStock -= item.Quantity;
        }

        return orderItems;
    }
}