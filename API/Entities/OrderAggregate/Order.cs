using System;

namespace API.Entities.OrderAggregate;

public class Order
{
    public int Id { get; set; }
    public required string BuyerEmail { get; set; }
    public required ShippingAddress ShippingAddress { get; set; }

    // Optional billing details (for official invoicing). Keep separate from ShippingAddress.
    public string? BillingName { get; set; }
    public string? BillingTaxId { get; set; } // NIF
    public string? BillingLine1 { get; set; }
    public string? BillingLine2 { get; set; }
    public string? BillingCity { get; set; }
    public string? BillingState { get; set; }
    public string? BillingPostalCode { get; set; }
    public string? BillingCountry { get; set; }
    public DateTime OrderDate { get; set; } = DateTime.UtcNow;
    public List<OrderItem> OrderItems { get; set; } = [];
    public long Subtotal { get; set; }
    public long DeliveryFee { get; set; }
    public long ProductDiscount { get; set; }
    public long Discount { get; set; }
    public required string PaymentIntentId { get; set; }
    public OrderStatus OrderStatus { get; set; } = OrderStatus.Pending;
    public required PaymentSummary PaymentSummary { get; set; }

    public string? CustomerComment { get; set; }
    public DateTime? CustomerCommentedAt { get; set; }

    public string? AdminCommentReply { get; set; }
    public DateTime? AdminCommentRepliedAt { get; set; }

    public OrderIncident? Incident { get; set; }

    public DateTime? ReceiptEmailedAt { get; set; }

    // Official (AT-certified provider) invoice metadata
    public string? TaxInvoiceProvider { get; set; }
    public string? TaxInvoiceProviderId { get; set; }
    public string? TaxInvoiceNumber { get; set; }
    public DateTime? TaxInvoiceIssuedAt { get; set; }
    public DateTime? TaxInvoiceLastAttemptAt { get; set; }
    public string? TaxInvoiceLastError { get; set; }
    public DateTime? TaxInvoiceEmailedAt { get; set; }

    public string? TrackingNumber { get; set; }
    public DateTime? TrackingAddedAt { get; set; }

    public DateTime? CancelledAt { get; set; }
    public string? RefundId { get; set; }
    public DateTime? RefundedAt { get; set; }

    public RefundRequestStatus RefundRequestStatus { get; set; } = RefundRequestStatus.None;
    public DateTime? RefundRequestedAt { get; set; }
    public DateTime? RefundReviewedAt { get; set; }
    public RefundReturnMethod RefundReturnMethod { get; set; } = RefundReturnMethod.None;
    public string? RefundRequestReason { get; set; }
    public string? RefundReviewNote { get; set; }

    public DateTime? RestockedAt { get; set; }
    public DateTime? SalesCountAdjustedAt { get; set; }

    public long GetTotal()
    {
        return Subtotal + DeliveryFee - Discount;
    }
}
