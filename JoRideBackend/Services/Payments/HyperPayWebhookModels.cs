namespace JoRideBackend.Services.Payments
{
    /// <summary>
    /// Decrypted OPPWA webhook body shape: { "type": "PAYMENT", "payload": {...} } — the
    /// payload mirrors the same object HyperPayGateway.AuthorizeAsync already parses from
    /// GET /v1/checkouts/{id}/payment (result.code, resourcePath, etc.), plus paymentType
    /// so we know which operation (PA/DB/CP/RV/RF) this notification is about.
    /// </summary>
    public class HyperPayWebhookEnvelope
    {
        public string? Type { get; set; }
        public HyperPayWebhookPayload? Payload { get; set; }
    }

    public class HyperPayWebhookPayload
    {
        public string? Id { get; set; }
        public string? ResourcePath { get; set; }
        public string? PaymentType { get; set; }
        public string? MerchantTransactionId { get; set; }
        public HyperPayWebhookResult? Result { get; set; }
    }

    public class HyperPayWebhookResult
    {
        public string? Code { get; set; }
    }
}
