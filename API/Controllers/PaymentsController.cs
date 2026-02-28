using System;
using API.Data;
using API.DTOs;
using API.Entities.OrderAggregate;
using API.Extensions;
using API.RequestHelpers;
using API.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Stripe;
using Microsoft.AspNetCore.Identity;
using API.Entities;
using System.Linq;
using System.Collections.Generic;
using API.Services.Invoicing;

namespace API.Controllers;

public class PaymentsController(
    PaymentsService paymentsService,
    StoreContext context,
    IConfiguration config,
    ILogger<PaymentsController> logger,
    IEmailService emailService,
    IInvoicePdfService invoicePdfService,
    ITaxInvoiceService taxInvoiceService,
    IOptions<EmailSettings> emailOptions,
    INotificationService notificationService,
    UserManager<User> userManager)
        : BaseApiController
{
    [Authorize]
    [HttpPost]
    public async Task<ActionResult<BasketDto>> CreateOrUpdatePaymentIntent()
    {
        var basket = await context.Baskets.GetBasketWithItems(Request.Cookies["basketId"]);

        if (basket == null) return BadRequest("Problem with the basket");

        var intent = await paymentsService.CreateOrUpdatePaymentIntent(basket);

        if (intent == null) return BadRequest("Problem creating payment intent");

        // Always set the basket PaymentIntentId and ClientSecret to the returned intent
        // (the service may have created a fresh PaymentIntent when the previous one was terminal).
        basket.PaymentIntentId = intent.Id;
        basket.ClientSecret = intent.ClientSecret;

        if (context.ChangeTracker.HasChanges())
        {
            var result = await context.SaveChangesAsync() > 0;

            if (!result) return BadRequest("Problem updating basket with intent");
        }

        return basket.ToDto();
    }

    [HttpPost("webhook")]
    public async Task<IActionResult> StripeWebhook()
    {
        var json = await new StreamReader(Request.Body).ReadToEndAsync();

        try
        {
            var stripeEvent = ConstructStripeEvent(json);

            if (stripeEvent.Data.Object is not PaymentIntent intent)
            {
                return BadRequest("Invalid event data");
            }

            if (intent.Status == "succeeded") await HandlePaymentIntentSucceeded(intent);
            else await HandlePaymentIntentFailed(intent);

            return Ok();
        }
        catch (StripeException ex)
        {
            logger.LogError(ex, "Stripe webhook error");
            return StatusCode(StatusCodes.Status500InternalServerError, "Webhook error");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "An expected error has occurred");
            return StatusCode(StatusCodes.Status500InternalServerError, "Unexpected error");
        }
    }

    private async Task HandlePaymentIntentFailed(PaymentIntent intent)
    {
        var order = await context.Orders
            .Include(x => x.OrderItems)
            .FirstOrDefaultAsync(x => x.PaymentIntentId == intent.Id)
                ?? throw new Exception("Order not found");

        foreach (var item in order.OrderItems)
        {
            var productItem = await context.Products
                .FindAsync(item.ItemOrdered.ProductId)
                    ?? throw new Exception("Problem updating order stock");

            productItem.QuantityInStock += item.Quantity;
        }

        order.OrderStatus = OrderStatus.PaymentFailed;

        await context.SaveChangesAsync();
    }

    private async Task HandlePaymentIntentSucceeded(PaymentIntent intent)
    {
        var order = await context.Orders
           .Include(x => x.OrderItems)
           .FirstOrDefaultAsync(x => x.PaymentIntentId == intent.Id)
               ?? throw new Exception("Order not found");
        var intentAmount = intent.Amount;
        // Log amounts to help diagnose mismatches
        logger.LogInformation("Stripe webhook succeeded for intent {IntentId}: intentAmount={IntentAmount}. Order {OrderId} totals: subtotal={Subtotal}, delivery={DeliveryFee}, discount={Discount}, computedTotal={OrderTotal}.",
            intent.Id, intentAmount, order.Id, order.Subtotal, order.DeliveryFee, order.Discount, order.GetTotal());

        // Mark the order as received when Stripe reports success. Keep a warning log
        // if amounts differ so admins can investigate, but don't treat Stripe success
        // as a mismatch that prevents the order from being fulfilled.
        if (Math.Abs(order.GetTotal() - intentAmount) > 1)
        {
            logger.LogWarning("Payment mismatch for order {OrderId}: orderTotal={OrderTotal}, intentAmount={IntentAmount} - marking as PaymentReceived due to Stripe success.", order.Id, order.GetTotal(), intentAmount);
        }

        logger.LogInformation("Payment received for order {OrderId}: intent amount {Amount}", order.Id, intentAmount);
        // Only transition to PaymentReceived from payment-related states.
        // Avoid overwriting a fulfillment state if it was set before the webhook arrived.
        var transitionedToPaymentReceived = false;
        if (order.OrderStatus == OrderStatus.Pending || order.OrderStatus == OrderStatus.PaymentMismatch || order.OrderStatus == OrderStatus.PaymentFailed)
        {
            await IncrementProductSalesCountsAsync(order);
            order.OrderStatus = OrderStatus.PaymentReceived;
            transitionedToPaymentReceived = true;
        }

        var basket = await context.Baskets.FirstOrDefaultAsync(x => 
            x.PaymentIntentId == intent.Id);
            
        if (basket != null) context.Baskets.Remove(basket);

        await context.SaveChangesAsync();

        if (transitionedToPaymentReceived)
        {
            await TrySendProcessingConfirmationEmail(order);

            await notificationService.TryCreateForEmailAsync(
                order.BuyerEmail,
                "Pagamento confirmado",
                $"Pagamento confirmado. A encomenda #{order.Id} está a ser processada.",
                $"/orders/{order.Id}");

            await TryNotifyAdminsPaymentReceivedAsync(order);
        }

        // Send receipt PDF once after payment is received
        await TrySendReceiptEmailIfNeeded(order);
    }

    private async Task TryNotifyAdminsPaymentReceivedAsync(Order order)
    {
        try
        {
            var admins = await userManager.GetUsersInRoleAsync("Admin");
            var adminEmails = admins
                .Select(a => (a.Email ?? string.Empty).Trim())
                .Where(e => !string.IsNullOrWhiteSpace(e))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (adminEmails.Count == 0) return;

            foreach (var to in adminEmails)
            {
                await notificationService.TryCreateForEmailAsync(
                    to,
                    "Pagamento recebido",
                    $"Pagamento recebido para a encomenda #{order.Id}.",
                    $"/admin/sales/{order.Id}");
            }

            var frontend = (emailOptions.Value.FrontendUrl ?? string.Empty).TrimEnd('/');
            var url = string.IsNullOrWhiteSpace(frontend) ? string.Empty : $"{frontend}/admin/sales/{order.Id}";
            var subject = $"[Venda] Pagamento recebido (Encomenda #{order.Id})";

                        var productIds = order.OrderItems.Select(x => x.ItemOrdered.ProductId).Distinct().ToList();
                        var meta = await context.Products
                                .AsNoTracking()
                                .Where(p => productIds.Contains(p.Id))
                                .Select(p => new { p.Id, Text = p.Subtitle ?? p.Description })
                                .ToListAsync();
                        var descMap = meta.ToDictionary(x => x.Id, x => x.Text);
                        var orderSummary = EmailTemplate.RenderOrderSummary(order, frontend, descMap);

                        var btn = string.IsNullOrWhiteSpace(url) ? string.Empty : EmailTemplate.PrimaryButton(url, "Abrir venda");

            var html = $"""
                <div style='font-family: Arial, sans-serif; line-height: 1.5'>
                  <h2>Restore</h2>
                  <p>Pagamento confirmado para a encomenda <strong>#{order.Id}</strong>.</p>
                  <p><strong>Cliente:</strong> {System.Net.WebUtility.HtmlEncode(order.BuyerEmail)}</p>
                                    {(string.IsNullOrWhiteSpace(btn) ? string.Empty : $"<p style='margin:0 0 12px'>{btn}</p>")}
                                    {orderSummary}
                </div>
                """;

            foreach (var to in adminEmails)
            {
                await emailService.SendEmailAsync(to, subject, html, replyToEmail: order.BuyerEmail);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to notify admins about payment received for order {OrderId}", order.Id);
        }
    }

    private async Task TrySendProcessingConfirmationEmail(Order order)
    {
        try
        {
            if (order.OrderStatus != OrderStatus.PaymentReceived) return;

            var frontend = (emailOptions.Value.FrontendUrl ?? string.Empty).TrimEnd('/');
            var orderUrl = string.IsNullOrWhiteSpace(frontend) ? string.Empty : $"{frontend}/orders/{order.Id}";

            var productIds = order.OrderItems.Select(x => x.ItemOrdered.ProductId).Distinct().ToList();
            var meta = await context.Products
                .AsNoTracking()
                .Where(p => productIds.Contains(p.Id))
                .Select(p => new { p.Id, Text = p.Subtitle ?? p.Description })
                .ToListAsync();
            var descMap = meta.ToDictionary(x => x.Id, x => x.Text);
            var orderSummary = EmailTemplate.RenderOrderSummary(order, frontend, descMap);

            var btn = string.IsNullOrWhiteSpace(orderUrl) ? string.Empty : EmailTemplate.PrimaryButton(orderUrl, "Ver encomenda");

            var subject = $"Encomenda #{order.Id}: Em processamento";
            var html = $"""
                <div style='font-family: Arial, sans-serif; line-height: 1.5'>
                  <h2>Restore</h2>
                  <p>Pagamento confirmado. A sua encomenda <strong>#{order.Id}</strong> está a ser processada.</p>
                  {(string.IsNullOrWhiteSpace(btn) ? string.Empty : $"<p style='margin:0 0 12px'>{btn}</p>")}
                  {orderSummary}
                </div>
                """;

            await emailService.SendEmailAsync(order.BuyerEmail, subject, html);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to send processing confirmation email for order {OrderId}", order.Id);
        }
    }

    private async Task TrySendReceiptEmailIfNeeded(Order order)
    {
        try
        {
            if (order.ReceiptEmailedAt.HasValue) return;
            if (order.OrderStatus != OrderStatus.PaymentReceived) return;

            var receiptBytes = await invoicePdfService.GenerateReceiptPdfAsync(order, CancellationToken.None);
            var taxPdf = await taxInvoiceService.TryGetOrIssueInvoicePdfAsync(order, CancellationToken.None);

            var subject = taxPdf != null
                ? $"Fatura e recibo da encomenda #{order.Id}"
                : $"Recibo da encomenda #{order.Id}";
            var frontend = (emailOptions.Value.FrontendUrl ?? string.Empty).TrimEnd('/');
            var orderUrl = string.IsNullOrWhiteSpace(frontend) ? string.Empty : $"{frontend}/orders/{order.Id}";

            var productIds = order.OrderItems.Select(x => x.ItemOrdered.ProductId).Distinct().ToList();
            var meta = await context.Products
                .AsNoTracking()
                .Where(p => productIds.Contains(p.Id))
                .Select(p => new { p.Id, Text = p.Subtitle ?? p.Description })
                .ToListAsync();
            var descMap = meta.ToDictionary(x => x.Id, x => x.Text);
            var orderSummary = EmailTemplate.RenderOrderSummary(order, frontend, descMap);

            var btn = string.IsNullOrWhiteSpace(orderUrl) ? string.Empty : EmailTemplate.PrimaryButton(orderUrl, "Ver encomenda");

            var heading = taxPdf != null ? "Fatura e recibo" : "Recibo";
            var body = taxPdf != null
                ? $"Pagamento confirmado. Segue em anexo a fatura (oficial) e o recibo (PDF) da sua encomenda <strong>#{order.Id}</strong>."
                : $"Pagamento confirmado. Segue em anexo o recibo (PDF) da sua encomenda <strong>#{order.Id}</strong>.";

            var html = $"""
                <div style='font-family: Arial, sans-serif; line-height: 1.5'>
                  <h2>{heading}</h2>
                  <p>{body}</p>
                  {(string.IsNullOrWhiteSpace(btn) ? string.Empty : $"<p style='margin:0 0 12px'>{btn}</p>")}
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

            var toUpdate = await context.Orders.FirstOrDefaultAsync(o => o.Id == order.Id);
            if (toUpdate == null) return;

            toUpdate.ReceiptEmailedAt = DateTime.UtcNow;
            if (taxPdf != null) toUpdate.TaxInvoiceEmailedAt = DateTime.UtcNow;
            await context.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to send receipt email for order {OrderId}", order.Id);
        }
    }

    private async Task IncrementProductSalesCountsAsync(Order order)
    {
        foreach (var item in order.OrderItems)
        {
            var productId = item.ItemOrdered.ProductId;
            var product = await context.Products.FindAsync(productId)
                ?? throw new Exception("Problem updating product sales count");

            product.SalesCount += item.Quantity;
        }
    }

    private Event ConstructStripeEvent(string json)
    {
        try
        {
            return EventUtility.ConstructEvent(json,
                Request.Headers["Stripe-Signature"], config["StripeSettings:WhSecret"]);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to construct stripe event");
            throw new StripeException("Invalid signature");
        }
    }
}