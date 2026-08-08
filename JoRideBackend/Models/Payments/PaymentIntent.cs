namespace JoRideBackend.Models.Payments
{
    public enum PaymentIntentState
    {
        Created,
        Authorized,
        Captured,
        Voided,
        Refunded,
        Failed
    }

    public class PaymentIntent
    {
        public Guid Id { get; set; }
        public decimal Amount { get; set; }
        public string Currency { get; set; } = "USD";
        public PaymentIntentState State { get; set; } = PaymentIntentState.Created;
        public string? ProviderRef { get; set; }
        public int? TripId { get; set; }
        public int UserId { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }

        // ── State machine ────────────────────────────────────────────────
        //
        // Created   -> Authorized | Failed        (authorization hold succeeds/fails)
        // Authorized-> Captured | Voided | Failed  (settle, release the hold, or capture fails)
        // Captured  -> Refunded                    (money can only be refunded once actually captured —
        //                                            there is nothing to reverse from a mere authorization)
        // Voided, Refunded, Failed are terminal.
        //
        // Every gateway operation (IPaymentGateway) drives this via TransitionTo; illegal
        // calls throw before any network call is made, so a caller can never observe a
        // PaymentIntent in a state the money didn't actually reach.
        private static readonly Dictionary<PaymentIntentState, PaymentIntentState[]> LegalTransitions = new()
        {
            [PaymentIntentState.Created] = new[] { PaymentIntentState.Authorized, PaymentIntentState.Failed },
            [PaymentIntentState.Authorized] = new[]
            {
                PaymentIntentState.Captured, PaymentIntentState.Voided, PaymentIntentState.Failed
            },
            [PaymentIntentState.Captured] = new[] { PaymentIntentState.Refunded },
            [PaymentIntentState.Voided] = Array.Empty<PaymentIntentState>(),
            [PaymentIntentState.Refunded] = Array.Empty<PaymentIntentState>(),
            [PaymentIntentState.Failed] = Array.Empty<PaymentIntentState>(),
        };

        /// <summary>Throws if State -> newState is not a legal transition. Does not mutate.</summary>
        public void EnsureCanTransitionTo(PaymentIntentState newState)
        {
            if (!LegalTransitions.TryGetValue(State, out var allowed) || !allowed.Contains(newState))
            {
                throw new InvalidOperationException(
                    $"Illegal PaymentIntent transition: {State} -> {newState} (id={Id}).");
            }
        }

        /// <summary>Validates then applies the transition, bumping UpdatedAt. Throws on an illegal transition.</summary>
        public void TransitionTo(PaymentIntentState newState)
        {
            EnsureCanTransitionTo(newState);
            State = newState;
            UpdatedAt = DateTime.UtcNow;
        }
    }
}
