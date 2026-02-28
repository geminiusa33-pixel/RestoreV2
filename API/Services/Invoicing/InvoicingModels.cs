using System;

namespace API.Services.Invoicing;

public record InvoiceIssueRequest(
    int OrderId,
    string BuyerEmail,
    string? BillingName,
    string? BillingTaxId,
    string? BillingLine1,
    string? BillingLine2,
    string? BillingCity,
    string? BillingState,
    string? BillingPostalCode,
    string? BillingCountry,
    long SubtotalCents,
    long DeliveryFeeCents,
    long DiscountCents,
    long TotalCents,
    DateTime OrderDateUtc);

public record IssuedInvoice(
    string Provider,
    string ProviderInvoiceId,
    string? InvoiceNumber,
    DateTime IssuedAtUtc);

public record InvoicePdf(string FileName, string ContentType, byte[] Content);

public record IssueInvoiceResult(
    bool Succeeded,
    IssuedInvoice? Issued,
    InvoicePdf? Pdf,
    string? Error);

public record GetInvoicePdfResult(
    bool Succeeded,
    InvoicePdf? Pdf,
    string? Error);
