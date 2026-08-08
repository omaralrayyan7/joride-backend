namespace JoRideBackend.Models
{
    public record RegisterRequest(
        string Name,
        string Email,
        string Password,
        string? ConfirmPassword = null,
        string? Phone = null,
        string? IdNumber = null,
        string? DrivingLicenseNumber = null);

    public record LoginRequest(string Email, string Password);

    public record AuthUser(int Id, string? Name, string? Email, decimal WalletBalance, bool IsAdmin = false);

    public record AuthResponse(string Token, DateTime ExpiresAt, AuthUser User, string RefreshToken);

    public record RefreshTokenRequest(string RefreshToken);

    public record RefreshTokenResponse(string Token, DateTime ExpiresAt, string RefreshToken);

    public record StartTripRequest(
        int UserId,
        int VehicleId,
        int Duration,
        string DurationType,
        decimal BaseFare,
        decimal BookingFee,
        decimal Tax,
        decimal TotalFare,
        string PaymentMethod);

    public record EndTripRequest(DateTime EndTime);

    public record UpdateProfileRequest(string? Name, string? Phone, string? ProfileImageUrl);

    public record TopUpRequest(decimal Amount, string? PaymentMethod, string? Reference = null);

    public record KeyRequest(int TripId);
}
