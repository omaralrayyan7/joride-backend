using System.ComponentModel.DataAnnotations;

namespace JoRideBackend.Models
{
    // E8.1: DataAnnotations added as a second, structural layer of validation on top of the
    // existing manual `if` checks already in each controller action — [ApiController]
    // auto-rejects (400) invalid ModelState before an action body ever runs, so these close
    // gaps the manual checks didn't cover (e.g. negative BaseFare/BookingFee/Tax, unbounded
    // free-text reasons) without touching the existing business-logic checks.
    public record RegisterRequest(
        [Required, StringLength(200, MinimumLength = 1)] string Name,
        [Required, EmailAddress, StringLength(320)] string Email,
        [Required, StringLength(200, MinimumLength = 1)] string Password,
        string? ConfirmPassword = null,
        [StringLength(30)] string? Phone = null,
        [StringLength(50)] string? IdNumber = null,
        [StringLength(50)] string? DrivingLicenseNumber = null,
        [StringLength(20)] string? ReferralCode = null);

    public record LoginRequest(
        [Required, StringLength(320)] string Email,
        [Required, StringLength(200)] string Password);

    public record AuthUser(int Id, string? Name, string? Email, decimal WalletBalance, bool IsAdmin = false);

    public record AuthResponse(string Token, DateTime ExpiresAt, AuthUser User, string RefreshToken);

    public record RefreshTokenRequest([Required] string RefreshToken);

    public record RefreshTokenResponse(string Token, DateTime ExpiresAt, string RefreshToken);

    public record StartTripRequest(
        int UserId,
        int VehicleId,
        [Range(1, int.MaxValue)] int Duration,
        [Required, StringLength(10)] string DurationType,
        [Range(0, 100000)] decimal BaseFare,
        [Range(0, 100000)] decimal BookingFee,
        [Range(0, 100000)] decimal Tax,
        [Range(0.01, 100000)] decimal TotalFare,
        [Required, StringLength(50)] string PaymentMethod);

    public record EndTripRequest(DateTime EndTime);

    public record CancelTripRequest([StringLength(1000)] string? Reason = null);

    public record UpdateProfileRequest(
        [StringLength(200)] string? Name,
        [StringLength(30)] string? Phone,
        [StringLength(2000)] string? ProfileImageUrl);

    public record TopUpRequest(
        [Range(0.01, 100000)] decimal Amount,
        [StringLength(50)] string? PaymentMethod,
        [StringLength(200)] string? Reference = null);

    public record KeyRequest(int TripId);

    public record SubmitRatingRequest(
        [Range(1, 5)] int Stars,
        [StringLength(1000)] string? Comment = null,
        [StringLength(2000)] string? ConditionPhotoUrl = null);
}
