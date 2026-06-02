using JoRideBackend.Models;
using JoRideBackend.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

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

    public UsersController(
        IPasswordHasher<User> hasher,
        JwtTokenService tokens,
        ILicenseVerification licenseVerifier,
        IConfiguration config)
    {
        this.hasher = hasher;
        this.tokens = tokens;
        this.licenseVerifier = licenseVerifier;
        this.config = config;
    }

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

    [Authorize]
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

        var (token, expiresAt) = tokens.IssueToken(user);
        return new AuthResponse(token, expiresAt, new AuthUser(user.Id, user.Name, user.Email, user.WalletBalance, user.IsAdmin));
    }

    [AllowAnonymous]
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
        return new AuthResponse(token, expiresAt, new AuthUser(user.Id, user.Name, user.Email, user.WalletBalance, user.IsAdmin));
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
    };
}
