namespace API.RequestHelpers;

public class InvoicingSettings
{
    // Supported values: Disabled | Fake | <ProviderName>
    public string Mode { get; set; } = "Disabled";

    // When true, include the store-generated receipt PDF as an additional attachment
    // even when an official invoice PDF is available.
    public bool AttachStoreReceiptPdf { get; set; } = true;
}
