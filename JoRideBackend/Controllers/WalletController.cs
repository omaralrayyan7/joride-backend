using JoRideBackend.Data;
using JoRideBackend.Models;
using JoRideBackend.Models.Payments;
using JoRideBackend.Services;
using JoRideBackend.Services.Payments;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

[ApiController]
[Route("api/wallet")]
public class WalletController : ControllerBase
{
    static readonly List<WalletTransaction> _transactions = new();
    static int _nextId = 1;
    internal static FirestoreService? _firestore;

    // LedgerService is a scoped service (it holds a scoped PaymentsDbContext), so it can't
    // be captured directly into a static field the way the singleton TraccarService is
    // elsewhere in this codebase — that would pin one DbContext instance across every
    // request forever. Instead we hold the (singleton) scope factory and open a fresh
    // scope per call, exactly like TraccarPollingService does for the same reason.
    internal static IServiceScopeFactory? _scopeFactory;

    public static void Initialize(List<WalletTransaction> loaded, FirestoreService fs)
    {
        _transactions.Clear();
        _transactions.AddRange(loaded);
        _nextId    = loaded.Count > 0 ? loaded.Max(t => t.Id) + 1 : 1;
        _firestore = fs;
    }

    public static void SetServiceScopeFactory(IServiceScopeFactory scopeFactory) => _scopeFactory = scopeFactory;

    private static string WalletAccount(int userId) => $"wallet:{userId}";

    /// <summary>
    /// Records the ledger effect of a wallet balance change. This — plus TopUp's own direct
    /// call — are the ONLY places allowed to write to a wallet:{userId} ledger account;
    /// User.WalletBalance itself is just a cache of the ledger balance (see its doc comment).
    /// </summary>
    private static async Task RecordWalletLedgerEntryAsync(string debitAccount, string creditAccount, decimal amount, string reference)
    {
        if (_scopeFactory is null) return; // not wired (e.g. some test contexts) — cache-only fallback
        // Must be `await`ed, not returned directly: a bare `using var scope` disposes the
        // scope (and its scoped PaymentsDbContext) as soon as this method returns control to
        // the caller, which — for a non-async method just returning the inner Task — happens
        // before that Task's body has actually run. Awaiting keeps the scope alive until the
        // ledger write really completes.
        using var scope = _scopeFactory.CreateScope();
        var ledger = scope.ServiceProvider.GetRequiredService<LedgerService>();
        await ledger.RecordTransactionAsync(debitAccount, creditAccount, amount, reference);
    }


    /// <summary>
    /// Charges the user. For the internal joRide Wallet, a charge normally requires
    /// sufficient balance. When <paramref name="allowNegative"/> is true (e.g. unavoidable
    /// overtime charges), the wallet is allowed to go negative — putting the user into debt.
    /// External card/wallet providers are simulated as always approved.
    /// </summary>
    public static async Task<bool> TryChargeAsync(int userId, decimal amount, string description, string paymentMethod, bool allowNegative = false)
    {
        if (amount <= 0) return false;
        var user = UsersController.GetUser(userId);
        if (user is null) return false;

        var normalized = (paymentMethod ?? string.Empty).Trim().ToLowerInvariant();
        var usesInternalWallet = normalized == "wallet" || normalized == "joride wallet";

        // Block only when paying up-front from the wallet with insufficient funds.
        // Overtime debt (allowNegative) is permitted to drive the balance below zero.
        if (usesInternalWallet && !allowNegative && user.WalletBalance < amount) return false;

        if (usesInternalWallet)
        {
            // Ledger first: it's the source of truth. WalletBalance is only ever written
            // here, in RefundAsync, in RecordPayment, and in TopUp — nowhere else — so it
            // stays a faithful cache of the ledger balance for wallet:{userId}.
            await RecordWalletLedgerEntryAsync(WalletAccount(userId), "revenue:payments", amount, description);
            user.WalletBalance -= amount;   // may go negative when allowNegative == true
            await (_firestore?.SaveUserAsync(user) ?? Task.CompletedTask);
        }

        var t = new WalletTransaction
        {
            Id          = _nextId++,
            UserId      = userId,
            Type        = "payment",
            Amount      = -amount,
            Description = description + $" via {paymentMethod}",
            CreatedAt   = DateTime.UtcNow,
        };
        _transactions.Add(t);
        await (_firestore?.SaveTransactionAsync(t) ?? Task.CompletedTask);
        return true;
    }
    /// <summary>
    /// Credits a refund to the user's JoWallet regardless of the original payment method.
    /// </summary>
    public static async Task RefundAsync(int userId, decimal amount, string description)
    {
        if (amount <= 0) return;
        var user = UsersController.GetUser(userId);
        if (user is null) return;

        await RecordWalletLedgerEntryAsync("revenue:refunds", WalletAccount(userId), amount, description);
        user.WalletBalance += amount;
        await (_firestore?.SaveUserAsync(user) ?? Task.CompletedTask);

        var t = new WalletTransaction
        {
            Id          = _nextId++,
            UserId      = userId,
            Type        = "refund",
            Amount      = amount,   // positive = credit
            Description = description,
            CreatedAt   = DateTime.UtcNow,
        };
        _transactions.Add(t);
        await (_firestore?.SaveTransactionAsync(t) ?? Task.CompletedTask);
    }

    public static void RecordPayment(int userId, decimal amount, string description)
    {
        var user = UsersController.GetUser(userId);
        if (user is not null)
        {
            _ = RecordWalletLedgerEntryAsync(WalletAccount(userId), "revenue:payments", amount, description);
            user.WalletBalance -= amount;
            _ = _firestore?.SaveUserAsync(user);   // persist wallet balance
        }

        var t = new WalletTransaction
        {
            Id          = _nextId++,
            UserId      = userId,
            Type        = "payment",
            Amount      = -amount,
            Description = description,
            CreatedAt   = DateTime.UtcNow,
        };
        _transactions.Add(t);
        _ = _firestore?.SaveTransactionAsync(t);   // fire-and-forget
    }

    // Payment rails that aren't confirmed automatically — a Zain Cash/CliQ transfer has to
    // be manually verified against the provider's own dashboard before the money is real to
    // us, unlike a HyperPay card checkout (webhook-confirmed) or the simulated "always
    // approved" methods below. See PendingTopUp's doc comment.
    private static readonly HashSet<string> ManualReconciliationMethods =
        new(StringComparer.OrdinalIgnoreCase) { "zain cash", "zaincash", "cliq" };

    private readonly PaymentsDbContext _db;
    private readonly LedgerService _ledger;

    public WalletController(PaymentsDbContext db, LedgerService ledger)
    {
        _db = db;
        _ledger = ledger;
    }

    [HttpGet]
    public async Task<IActionResult> GetWallet([FromQuery] int userId)
    {
        var user = UsersController.GetUser(userId);
        if (user is null) return NotFound("User not found");

        var transactions = _transactions
            .Where(t => t.UserId == userId)
            .OrderByDescending(t => t.CreatedAt)
            .ToList();

        // Authoritative balance: computed from the ledger, not read from the WalletBalance
        // cache. (WalletBalance is kept in sync as a cache for the other places that still
        // read it directly — see its doc comment on User — but this endpoint is the one
        // place that should reflect the ledger even if that cache ever drifts.)
        var balance = await _ledger.GetAccountBalanceAsync(WalletAccount(userId));

        return Ok(new { balance, transactions });
    }

    [HttpPost("topup")]
    public async Task<IActionResult> TopUp([FromQuery] int userId, [FromBody] TopUpRequest request)
    {
        if (request.Amount <= 0)
            return BadRequest("Amount must be greater than zero.");

        var user = UsersController.GetUser(userId);
        if (user is null) return NotFound("User not found");

        var method = string.IsNullOrWhiteSpace(request.PaymentMethod) ? "unknown" : request.PaymentMethod.Trim();

        if (ManualReconciliationMethods.Contains(method))
        {
            // No ledger credit yet, no WalletBalance change yet — see PendingTopUp's doc
            // comment. An admin has to confirm the transfer actually arrived first
            // (POST /api/admin/topups/{id}/confirm).
            var pending = new PendingTopUp
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                Amount = request.Amount,
                PaymentMethod = method,
                Reference = request.Reference,
                Status = PendingTopUpStatus.Pending,
                CreatedAt = DateTime.UtcNow,
            };
            _db.PendingTopUps.Add(pending);
            await _db.SaveChangesAsync();

            return Ok(new
            {
                status = "pending",
                pendingTopUpId = pending.Id,
                message = $"Submitted via {method} — awaiting admin confirmation before your balance updates.",
            });
        }

        await _ledger.RecordTransactionAsync(
            "external:topup_provider", WalletAccount(userId), request.Amount, $"Top-up via {request.PaymentMethod ?? "unknown"}");
        user.WalletBalance += request.Amount;
        await (_firestore?.SaveUserAsync(user) ?? Task.CompletedTask);

        var t = new WalletTransaction
        {
            Id          = _nextId++,
            UserId      = userId,
            Type        = "topup",
            Amount      = request.Amount,
            Description = $"Top-up via {request.PaymentMethod ?? "unknown"}",
            CreatedAt   = DateTime.UtcNow,
        };
        _transactions.Add(t);
        await (_firestore?.SaveTransactionAsync(t) ?? Task.CompletedTask);

        NotificationsController.Push(
            userId,
            "Wallet Top-Up",
            $"Your wallet has been topped up with {request.Amount:F2} JOD.",
            "payment");

        var balance = await _ledger.GetAccountBalanceAsync(WalletAccount(userId));
        return Ok(new { balance, transaction = t });
    }
}
