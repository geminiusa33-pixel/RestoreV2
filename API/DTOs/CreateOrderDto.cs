using System;
using API.Entities.OrderAggregate;

namespace API.DTOs;

public class CreateOrderDto
{
    public required ShippingAddress ShippingAddress { get; set; }
    public required PaymentSummary PaymentSummary { get; set; }

    // Optional billing data (Portugal - NIF/fiscal invoice)
    public string? BillingName { get; set; }
    public string? BillingTaxId { get; set; } // NIF
    public string? BillingLine1 { get; set; }
    public string? BillingLine2 { get; set; }
    public string? BillingCity { get; set; }
    public string? BillingState { get; set; }
    public string? BillingPostalCode { get; set; }
    public string? BillingCountry { get; set; }
}
