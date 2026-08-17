using JoRideBackend.Models;
using JoRideBackend.Services;
using JoRideBackend.Services.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

[ApiController]
[Route("api/[controller]")]
public class UsersController : ControllerBase
{
    static readonly List<User> users = new();
    static int _nextId = 1;
    static FirestoreService? _firestore;

    public static void Initialize(List<User> loaded, FirestoreService fs)
    {
        users.Clear();
        users.AddRange(loaded);
        _nextId = loaded.Count > 0 ? loaded.Max(u => u.Id) + 1 : 1;
        _firestore = fs;
    }

    public static bool Exists(int id) => users.Any(u => u.Id == id);
    public static IReadOnlyList<User> AllUsers() => users;

    /// <summary>
    /// Ensures a default admin account exists (for first-run access to the Admin Dashboard).
    /// Default credentials: admin@joride.com / Admin@123 — change these in production.
    /// </summary>
    public static async Task EnsureSeedAdminAsync(IPasswordHasher<User> hasher)
    {
        if (users.Any(u => u.IsAdmin)) return;

        var admin = new User
        {
            Id                   = _nextId++,
            Name                 = "Administrator",
            Email                = "admin@joride.com",
            Phone                = "+962790000000",
            IdNumber             = "9901012345",
            DrivingLicenseNumber = "JO-123456",
            IsAdmin              = true,
            IsActive             = true,
            IsLicenseVerified    = true,
            IsEmailVerified      = true,
            IsPhoneVerified      = true,
            CreatedAt            = DateTime.UtcNow,
            WalletBalance        = 0m,
        };
        admin.PasswordHash = hasher.HashPassword(admin, "Admin@123");
        users.Add(admin);
        await (_firestore?.SaveUserAsync(admin) ?? Task.CompletedTask);
    }
    public static User? GetUser(int id) => users.FirstOrDefault(u => u.Id == id);
    public static User? GetUser(string email) =>
        users.FirstOrDefault(u => string.Equals(u.Email, email, StringComparison.OrdinalIgnoreCase));
    public static User? GetUserByPhone(string phone) =>
        users.FirstOrDefault(u => !string.IsNullOrWhiteSpace(u.Phone) && string.Equals(u.Phone, phone, StringComparison.OrdinalIgnoreCase));
    public static Task SaveUser(User user) => _firestore?.SaveUserAsync(user) ?? Task.CompletedTask;

    private readonly IPasswordHasher<User> hasher;
    private readonly JwtTokenService tokens;
    private readonly ILicenseVerification licenseVerifier;
    private readonly IConfiguration config;
    private readonly RefreshTokenService refreshTokens;
    private readonly PasswordResetTokenService passwordResetTokens;
    private readonly ILogger<UsersController> logger;

    public UsersController(
        IPasswordHasher<User> hasher,
        JwtTokenService tokens,
        ILicenseVerification licenseVerifier,
        IConfiguration config,
        RefreshTokenService refreshTokens,
        PasswordResetTokenService passwordResetTokens,
        ILogger<UsersController> logger)
    {
        this.hasher = hasher;
        this.tokens = tokens;
        this.licenseVerifier = licenseVerifier;
        this.config = config;
        this.refreshTokens = refreshTokens;
        this.passwordResetTokens = passwordResetTokens;
        this.logger = logger;
    }

    private string? ClientIp() => HttpContext.Connection.RemoteIpAddress?.ToString();

    [Authorize(Policy = "AdminOnly")]
    [HttpGet]
    public ActionResult<IEnumerable<object>> GetAll([FromQuery] string? search)
    {
        var q = users.AsEnumerable();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim();
            bool C(string? v) => v is not null && v.Contains(s, StringComparison.OrdinalIgnoreCase);
            q = q.Where(u => C(u.Name) || C(u.Email) || C(u.Phone) || C(u.IdNumber) || C(u.DrivingLicenseNumber));
        }
        return q.Select(BuildProfileResponse).ToList();
    }

    [Authorize]
    [HttpGet("{id:int}")]
    public ActionResult<object> Get(int id)
    {
        var user = users.FirstOrDefault(u => u.Id == id);
        return user is null ? NotFound() : Ok(BuildProfileResponse(user));
    }

    [Authorize]
    [HttpGet("{id:int}/profile")]
    public IActionResult GetProfile(int id)
    {
        var user = users.FirstOrDefault(u => u.Id == id);
        if (user is null) return NotFound();
        return Ok(BuildProfileResponse(user));
    }

    [Authorize]
    [HttpPut("{id:int}/profile")]
    public async Task<IActionResult> UpdateProfile(int id, UpdateProfileRequest request)
    {
        var user = users.FirstOrDefault(u => u.Id == id);
        if (user is null) return NotFound();

        if (request.Name is not null) user.Name = request.Name;
        if (request.Phone is not null) user.Phone = request.Phone;
        if (request.ProfileImageUrl is not null) user.ProfileImageUrl = request.ProfileImageUrl;

        await (_firestore?.SaveUserAsync(user) ?? Task.CompletedTask);
        return Ok(BuildProfileResponse(user));
    }

    [Authorize(Policy = "AdminOnly")]
    [HttpPost]
    public async Task<ActionResult<object>> Create(User user)
    {
        user.Id = _nextId++;
        user.IsActive = true;
        user.CreatedAt = DateTime.UtcNow;
        if (!string.IsNullOrWhiteSpace(user.PasswordHash) && !user.PasswordHash.StartsWith("AQAAAA", StringComparison.Ordinal))
        {
            user.PasswordHash = hasher.HashPassword(user, user.PasswordHash);
        }
        users.Add(user);
        await (_firestore?.SaveUserAsync(user) ?? Task.CompletedTask);
        return CreatedAtAction(nameof(Get), new { id = user.Id }, BuildProfileResponse(user));
    }

    // Admin-only, matching its siblings Create/Delete/Activate/Deactivate below — this is a
    // full CRUD action that can set IsAdmin/IsActive/IsLicenseVerified/PasswordHash, unlike
    // the narrowly-scoped, genuinely self-service UpdateProfile (Name/Phone/ProfileImageUrl
    // only) above. It was previously just [Authorize], letting any authenticated user edit
    // any other user's profile — including granting themselves admin.
    [Authorize(Policy = "AdminOnly")]
    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, User update)
    {
        var user = users.FirstOrDefault(u => u.Id == id);
        if (user is null) return NotFound();

        user.Name = update.Name ?? user.Name;
        user.Email = update.Email ?? user.Email;
        user.Phone = update.Phone ?? user.Phone;
        user.IdNumber = update.IdNumber ?? user.IdNumber;
        user.DrivingLicenseNumber = update.DrivingLicenseNumber ?? user.DrivingLicenseNumber;
        user.ProfileImageUrl = update.ProfileImageUrl ?? user.ProfileImageUrl;
        user.IsActive = update.IsActive;
        user.IsAdmin = update.IsAdmin;
        user.IsLicenseVerified = update.IsLicenseVerified;
        user.IsEmailVerified = update.IsEmailVerified;
        user.IsPhoneVerified = update.IsPhoneVerified;

        if (!string.IsNullOrWhiteSpace(update.PasswordHash))
            user.PasswordHash = hasher.HashPassword(user, update.PasswordHash);

        await (_firestore?.SaveUserAsync(user) ?? Task.CompletedTask);
        return NoContent();
    }

    [Authorize(Policy = "AdminOnly")]
    [HttpDelete("{id:int}")]
    public IActionResult Delete(int id)
    {
        var user = users.FirstOrDefault(u => u.Id == id);
        if (user is null) return NotFound();
        users.Remove(user);
        _ = _firestore?.DeleteUserAsync(id);
        AuditController.Log("UserDeleted", "User", id,
            GetActorLabel(), "Admin", $"Deleted user '{user.Name}' ({user.Email}).");
        return NoContent();
    }

    private string GetActorLabel()
    {
        // HttpContext is available in controller actions.
        var actorId   = HttpContext.User.FindFirst("sub")?.Value ?? "?";
        var actorName = HttpContext.User.FindFirst("name")?.Value ?? "admin";
        return $"Admin: {actorName} (#{actorId})";
    }

    [Authorize(Policy = "AdminOnly")]
    [HttpPut("/api/admin/users/{id:int}/activate")]
    public async Task<ActionResult<object>> Activate(int id)
    {
        var user = users.FirstOrDefault(u => u.Id == id);
        if (user is null) return NotFound();
        user.IsActive = true;
        await (_firestore?.SaveUserAsync(user) ?? Task.CompletedTask);
        AuditController.Log("UserActivated", "User", id, GetActorLabel(), "Admin",
            $"Activated '{user.Name}' ({user.Email}).");
        return Ok(BuildProfileResponse(user));
    }

    [Authorize(Policy = "AdminOnly")]
    [HttpPut("/api/admin/users/{id:int}/deactivate")]
    public async Task<ActionResult<object>> Deactivate(int id)
    {
        var user = users.FirstOrDefault(u => u.Id == id);
        if (user is null) return NotFound();
        user.IsActive = false;
        await (_firestore?.SaveUserAsync(user) ?? Task.CompletedTask);
        AuditController.Log("UserDeactivated", "User", id, GetActorLabel(), "Admin",
            $"Deactivated '{user.Name}' ({user.Email}).");
        return Ok(BuildProfileResponse(user));
    }

    // Brute-force protection settings
    private const int MaxFailedAttempts = 5;
    private static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);

    [AllowAnonymous]
    [EnableRateLimiting("auth-login")]
    [HttpPost("/api/auth/register")]
    public async Task<ActionResult<AuthResponse>> Register(RegisterRequest request)
    {
        // ── Required fields ───────────────────────────────────────────────────
        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequest("Full name is required.");
        if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Password))
            return BadRequest("Email and password are required.");
        if (string.IsNullOrWhiteSpace(request.IdNumber))
            return BadRequest("ID Number is required.");
        if (string.IsNullOrWhiteSpace(request.DrivingLicenseNumber))
            return BadRequest("Driving License Number is required.");

        // ── Email format ──────────────────────────────────────────────────────
        if (!PasswordPolicy.IsValidEmail(request.Email))
            return BadRequest("Please enter a valid email address.");

        // ── Confirm password ──────────────────────────────────────────────────
        if (request.ConfirmPassword is not null && request.Password != request.ConfirmPassword)
            return BadRequest("Password and Confirm Password do not match.");

        // ── Password policy ───────────────────────────────────────────────────
        var pwdError = PasswordPolicy.Validate(request.Password);
        if (pwdError is not null) return BadRequest(pwdError);

        // ── Uniqueness ────────────────────────────────────────────────────────
        if (users.Any(u => string.Equals(u.Email, request.Email, StringComparison.OrdinalIgnoreCase)))
            return Conflict("A user with that email already exists.");

        // ── ID / License validated against seeded data ────────────────────────
        if (!licenseVerifier.IsValidCombination(request.DrivingLicenseNumber, request.IdNumber))
            return BadRequest("Not Valid: the ID Number and Driving License Number do not match our records.");

        var user = new User
        {
            Id = _nextId++,
            Name = request.Name,
            Email = request.Email.Trim(),
            Phone = request.Phone,
            IdNumber = request.IdNumber!.Trim(),
            DrivingLicenseNumber = request.DrivingLicenseNumber!.Trim(),
            IsLicenseVerified = true,
            IsEmailVerified = false,
            IsPhoneVerified = false,
            IsAdmin = false,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            WalletBalance = 0m,
        };
        user.PasswordHash = hasher.HashPassword(user, request.Password);
        users.Add(user);

        await (_firestore?.SaveUserAsync(user) ?? Task.CompletedTask);

        if (!string.IsNullOrWhiteSpace(request.ReferralCode))
        {
            var referrerId = ReferralsController.FindReferrer(request.ReferralCode.Trim());
            if (referrerId.HasValue && referrerId.Value != user.Id)
                _ = ReferralsController.ApplyReferralAsync(referrerId.Value, user.Id, request.ReferralCode.Trim());
        }

        var (token, expiresAt) = tokens.IssueToken(user);
        var refreshToken = await refreshTokens.IssueAsync(user.Id, ClientIp());
        return new AuthResponse(
            token, expiresAt, new AuthUser(user.Id, user.Name, user.Email, user.WalletBalance, user.IsAdmin), refreshToken);
    }

    [AllowAnonymous]
    [EnableRateLimiting("auth-login")]
    [HttpPost("/api/auth/login")]
    public async Task<ActionResult<AuthResponse>> Login(LoginRequest request)
    {
        var user = users.FirstOrDefault(u =>
            string.Equals(u.Email, request.Email, StringComparison.OrdinalIgnoreCase));
        if (user is null || string.IsNullOrEmpty(user.PasswordHash))
            return Unauthorized("Invalid email or password.");

        if (!user.IsActive)
            return Unauthorized("Account is deactivated.");

        // ── Lockout check ─────────────────────────────────────────────────────
        if (user.LockoutEndUtc.HasValue && user.LockoutEndUtc.Value > DateTime.UtcNow)
        {
            var minutesLeft = Math.Ceiling((user.LockoutEndUtc.Value - DateTime.UtcNow).TotalMinutes);
            return StatusCode(423, $"Account locked due to too many failed attempts. Try again in {minutesLeft} minute(s).");
        }

        var result = hasher.VerifyHashedPassword(user, user.PasswordHash, request.Password);
        if (result == PasswordVerificationResult.Failed)
        {
            user.FailedLoginAttempts++;
            if (user.FailedLoginAttempts >= MaxFailedAttempts)
            {
                user.LockoutEndUtc = DateTime.UtcNow.Add(LockoutDuration);
                user.FailedLoginAttempts = 0;
                await (_firestore?.SaveUserAsync(user) ?? Task.CompletedTask);
                AuditController.Log("AccountLockedOut", "User", user.Id, $"User #{user.Id} ({user.Email})", "System",
                    $"Locked for {LockoutDuration.TotalMinutes} minutes after {MaxFailedAttempts} failed login attempts. " +
                    $"Request IP: {HttpContext.Connection.RemoteIpAddress}.");
                return StatusCode(423, $"Account locked due to too many failed attempts. Try again in {LockoutDuration.TotalMinutes} minutes.");
            }
            await (_firestore?.SaveUserAsync(user) ?? Task.CompletedTask);
            var remaining = MaxFailedAttempts - user.FailedLoginAttempts;
            return Unauthorized($"Invalid email or password. {remaining} attempt(s) remaining before lockout.");
        }

        // ── Success — reset lockout counters ──────────────────────────────────
        if (user.FailedLoginAttempts != 0 || user.LockoutEndUtc is not null)
        {
            user.FailedLoginAttempts = 0;
            user.LockoutEndUtc = null;
            await (_firestore?.SaveUserAsync(user) ?? Task.CompletedTask);
        }

        var (token, expiresAt) = tokens.IssueToken(user);
        var refreshToken = await refreshTokens.IssueAsync(user.Id, ClientIp());
        return new AuthResponse(
            token, expiresAt, new AuthUser(user.Id, user.Name, user.Email, user.WalletBalance, user.IsAdmin), refreshToken);
    }

    /// <summary>
    /// Rotation, not just refresh: the presented token is always revoked here and replaced
    /// with a new one — there's no path back to Ok() without RefreshTokenService having
    /// revoked what was just used. See RefreshTokenService for reuse-detection.
    /// </summary>
    [AllowAnonymous]
    [EnableRateLimiting("auth-login")]
    [HttpPost("/api/auth/refresh")]
    public async Task<ActionResult<RefreshTokenResponse>> Refresh(RefreshTokenRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.RefreshToken))
            return BadRequest("Refresh token is required.");

        var result = await refreshTokens.RotateAsync(request.RefreshToken, ClientIp());

        switch (result.Outcome)
        {
            case RefreshOutcome.ReusedRevoked:
                AuditController.Log("RefreshTokenReuseDetected", "User", result.UserId ?? 0,
                    $"User #{result.UserId}", "System",
                    $"A revoked refresh token was presented again — entire token family revoked. Request IP: {ClientIp()}.");
                return Unauthorized("This refresh token has already been used. Please log in again.");

            case RefreshOutcome.Expired:
                return Unauthorized("Refresh token expired. Please log in again.");

            case RefreshOutcome.InvalidOrUnknown:
                return Unauthorized("Invalid refresh token.");
        }

        var user = users.FirstOrDefault(u => u.Id == result.UserId);
        if (user is null || !user.IsActive)
            return Unauthorized("Account not found or deactivated.");

        var (token, expiresAt) = tokens.IssueToken(user);
        return new RefreshTokenResponse(token, expiresAt, result.RawToken!);
    }

    /// <summary>
    /// KNOWN LIMITATION (demo): no SMTP/SendGrid (or similar) email service is configured in
    /// this project, so the reset token/link cannot actually be emailed to the user. It is
    /// logged instead so it can be picked up manually for testing. Before this endpoint is
    /// used against real users, wire up a real email provider and send the link there instead
    /// of logging it.
    /// </summary>
    [AllowAnonymous]
    [EnableRateLimiting("auth-login")]
    [HttpPost("/api/auth/forgot-password")]
    public async Task<IActionResult> ForgotPassword(ForgotPasswordRequest request)
    {
        var user = users.FirstOrDefault(u =>
            string.Equals(u.Email, request.Email, StringComparison.OrdinalIgnoreCase));

        if (user is not null && user.IsActive)
        {
            var rawToken = await passwordResetTokens.IssueAsync(user.Id, ClientIp());

            logger.LogWarning(
                "[ForgotPassword] DEMO MODE — no email service is configured, so this reset " +
                "token is being logged instead of emailed. User #{UserId} ({Email}) reset token: {Token}",
                user.Id, user.Email, rawToken);
        }

        // Same response whether or not the email is registered, so this endpoint can't be
        // used to enumerate which emails have accounts.
        return Ok(new { message = "If an account with that email exists, a password reset link has been sent." });
    }

    [AllowAnonymous]
    [EnableRateLimiting("auth-login")]
    [HttpPost("/api/auth/reset-password")]
    public async Task<IActionResult> ResetPassword(ResetPasswordRequest request)
    {
        var validation = await passwordResetTokens.ValidateAsync(request.Token);
        if (validation.Outcome != PasswordResetOutcome.Valid)
            return BadRequest("This password reset link is invalid or has expired.");

        var pwdError = PasswordPolicy.Validate(request.NewPassword);
        if (pwdError is not null) return BadRequest(pwdError);

        var user = users.FirstOrDefault(u => u.Id == validation.Entity!.UserId);
        if (user is null)
            return BadRequest("This password reset link is invalid or has expired.");

        user.PasswordHash = hasher.HashPassword(user, request.NewPassword);
        user.FailedLoginAttempts = 0;
        user.LockoutEndUtc = null;
        await (_firestore?.SaveUserAsync(user) ?? Task.CompletedTask);

        await passwordResetTokens.ConsumeAsync(validation.Entity!);
        await refreshTokens.RevokeAllForUserAsync(user.Id);

        AuditController.Log("PasswordReset", "User", user.Id, $"User #{user.Id} ({user.Email})", "System",
            $"Password reset via forgot-password token. Request IP: {ClientIp()}.");

        return Ok(new { message = "Password has been reset successfully." });
    }

    private static object BuildProfileResponse(User u) => new
    {
        u.Id,
        u.Name,
        u.Email,
        u.Phone,
        u.IdNumber,
        u.DrivingLicenseNumber,
        u.IsActive,
        u.IsAdmin,
        u.IsLicenseVerified,
        u.IsEmailVerified,
        u.IsPhoneVerified,
        u.CreatedAt,
        u.WalletBalance,
        u.ProfileImageUrl,
        kycStatus = u.KycStatus.ToString(),
    };
}
