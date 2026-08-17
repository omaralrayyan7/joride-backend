using JoRideBackend.Models;
using JoRideBackend.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

[ApiController]
[Route("api/referrals")]
public class ReferralsController : ControllerBase
{
    static readonly List<Referral> _referrals = new();
    static int _nextId = 1;
    internal static FirestoreService? _firestore;

    public static void Initialize(List<Referral> loaded, FirestoreService fs)
    {
        _referrals.Clear();
        _referrals.AddRange(loaded);
        _nextId = loaded.Count > 0 ? loaded.Max(r => r.Id) + 1 : 1;
        _firestore = fs;
    }

    public static string CodeForUser(int userId) => $"JO{userId:D5}";

    public static int? FindReferrer(string code)
    {
        if (!code.StartsWith("JO", StringComparison.OrdinalIgnoreCase)) return null;
        if (!int.TryParse(code[2..], out var id)) return null;
        return UsersController.Exists(id) ? id : null;
    }

    // Called from UsersController.Register — fire-and-forget the Firestore write
    // because Register itself is already awaiting other async work; reward is
    // recorded in-memory synchronously before the async continuation.
    public static async Task ApplyReferralAsync(int referrerId, int referredUserId, string code)
    {
        // Each new user may only generate one referral reward
        if (_referrals.Any(r => r.ReferredUserId == referredUserId)) return;

        var referral = new Referral
        {
            Id             = _nextId++,
            ReferrerId     = referrerId,
            ReferredUserId = referredUserId,
            Code           = code.ToUpperInvariant(),
            RewardAmount   = 1.00m,
            CreatedAt      = DateTime.UtcNow,
        };
        _referrals.Add(referral);

        await WalletController.RefundAsync(
            referrerId, 1.00m,
            $"Referral reward — user #{referredUserId} signed up with your code {referral.Code}");

        NotificationsController.Push(
            referrerId,
            "Referral Reward",
            "You earned 1.00 JOD! A new user signed up using your referral code.",
            "wallet");

        await (_firestore?.SaveReferralAsync(referral) ?? Task.CompletedTask);
    }

    [Authorize]
    [HttpGet("my-code")]
    public IActionResult GetMyCode()
    {
        var callerStr = User.FindFirst("sub")?.Value;
        if (callerStr is null || !int.TryParse(callerStr, out var callerId)) return Unauthorized();

        var code = CodeForUser(callerId);
        var usages = _referrals.Where(r => r.ReferrerId == callerId).ToList();

        return Ok(new
        {
            code,
            totalReferrals = usages.Count,
            totalEarned    = usages.Sum(r => r.RewardAmount),
            referrals      = usages.Select(r => new
            {
                r.ReferredUserId,
                r.RewardAmount,
                r.CreatedAt,
            }),
        });
    }

    [Authorize(Policy = "AdminOnly")]
    [HttpGet]
    public IActionResult GetAll() => Ok(_referrals);
}
