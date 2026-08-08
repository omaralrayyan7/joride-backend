namespace JoRideBackend.Models.Payments
{
    /// <summary>
    /// Idempotency log for HyperPay webhook notifications. Keyed on the provider's own
    /// unique reference for the event (HyperPay's payment resourcePath, e.g.
    /// "/v1/payments/8ac7a4a1..."), which is stable across redeliveries of the same
    /// notification — so a replay is detected before we ever touch a PaymentIntent.
    /// </summary>
    public class ProcessedPaymentEvent
    {
        public Guid Id { get; set; }
        public string ProviderEventId { get; set; } = string.Empty;
        public DateTime ProcessedAt { get; set; }
    }
}
