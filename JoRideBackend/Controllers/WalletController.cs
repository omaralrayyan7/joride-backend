using JoRideBackend.Models;
using JoRideBackend.Services;
using Microsoft.AspNetCore.Mvc;

[ApiController]
[Route("api/wallet")]
public class WalletController : ControllerBase
{
    static readonly List<WalletTransaction> _transactions = new();
    static int _nextId = 1;
    internal static FirestoreService? _firestore;

    public static void Initialize(List<WalletTransaction> loaded, FirestoreService fs)
    {
        _transactions.Clear();
        _transactions.AddRange(loaded);
        _nextId    = loaded.Count > 0 ? loaded.Max(t => t.Id) + 1 : 1;
        _firestore = fs;
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

    [HttpGet]
    public IActionResult GetWallet([FromQuery] int userId)
    {
        var user = UsersController.GetUser(userId);
        if (user is null) return NotFound("User not found");

        var transactions = _transactions
            .Where(t => t.UserId == userId)
            .OrderByDescending(t => t.CreatedAt)
            .ToList();

        return Ok(new { balance = user.WalletBalance, transactions });
    }

    [HttpPost("topup")]
    public async Task<IActionResult> TopUp([FromQuery] int userId, [FromBody] TopUpRequest request)
    {
        if (request.Amount <= 0)
            return BadRequest("Amount must be greater than zero.");

        var user = UsersController.GetUser(userId);
        if (user is null) return NotFound("User not found");

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

        return Ok(new { balance = user.WalletBalance, transaction = t });
    }
}
