using System.Security.Claims;
using JoRideBackend.Data;
using JoRideBackend.Models.Auth;
using JoRideBackend.Services.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// KYC document upload and private, signed-only retrieval. Documents are never publicly
/// reachable — see KycDocumentStorage (stored outside wwwroot) and KycSigningService (the
/// only way to actually read one back is a signed, time-limited link). Approve/reject lives
/// in KycAdminController.
/// </summary>
[ApiController]
[Route("api/kyc")]
[Authorize]
public class KycController : ControllerBase
{
    private static readonly TimeSpan SignedUrlLifetime = TimeSpan.FromMinutes(10);
    private static readonly string[] AllowedContentTypes = { "image/jpeg", "image/png", "application/pdf" };
    private const long MaxUploadBytes = 10 * 1024 * 1024; // 10 MB

    private readonly PaymentsDbContext _db;
    private readonly KycDocumentStorage _storage;
    private readonly KycSigningService _signing;

    public KycController(PaymentsDbContext db, KycDocumentStorage storage, KycSigningService signing)
    {
        _db = db;
        _storage = storage;
        _signing = signing;
    }

    [HttpPost("documents")]
    [RequestSizeLimit(MaxUploadBytes)]
    public async Task<IActionResult> Upload([FromForm] KycDocumentType documentType, IFormFile file, CancellationToken ct)
    {
        if (file is null || file.Length == 0)
            return BadRequest(new { error = "A file is required." });

        if (!AllowedContentTypes.Contains(file.ContentType))
            return BadRequest(new { error = $"Unsupported content type '{file.ContentType}'. Allowed: {string.Join(", ", AllowedContentTypes)}." });

        var userId = CurrentUserId();
        if (userId is null) return Unauthorized();

        await using var stream = file.OpenReadStream();
        var storagePath = await _storage.SaveAsync(userId.Value, stream, file.FileName, ct);

        var document = new KycDocument
        {
            Id = Guid.NewGuid(),
            UserId = userId.Value,
            DocumentType = documentType,
            FileName = file.FileName,
            ContentType = file.ContentType,
            StoragePath = storagePath,
            UploadedAt = DateTime.UtcNow,
        };
        _db.KycDocuments.Add(document);
        await _db.SaveChangesAsync(ct);

        return Ok(new { id = document.Id, documentType = document.DocumentType.ToString(), uploadedAt = document.UploadedAt });
    }

    [HttpGet("documents")]
    public async Task<IActionResult> MyDocuments(CancellationToken ct)
    {
        var userId = CurrentUserId();
        if (userId is null) return Unauthorized();

        var docs = await _db.KycDocuments
            .Where(d => d.UserId == userId.Value)
            .OrderByDescending(d => d.UploadedAt)
            .Select(d => new { d.Id, documentType = d.DocumentType.ToString(), d.FileName, d.UploadedAt })
            .ToListAsync(ct);

        return Ok(docs);
    }

    /// <summary>Returns a signed, time-limited download URL — never the file itself, and
    /// never a stable/public link. Owner or admin only.</summary>
    [HttpGet("documents/{id:guid}/sign")]
    public async Task<IActionResult> SignDownloadUrl(Guid id, CancellationToken ct)
    {
        var document = await _db.KycDocuments.FindAsync(new object[] { id }, ct);
        if (document is null) return NotFound();

        var userId = CurrentUserId();
        var isAdmin = User.HasClaim("role", "admin");
        if (userId != document.UserId && !isAdmin) return Forbid();

        if (!_signing.IsConfigured)
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "KYC document signing is not configured." });

        var (signature, expires) = _signing.Sign(id, SignedUrlLifetime);
        var url = Url.Action(nameof(Download), "Kyc", new { id, expires, sig = signature }, Request.Scheme);

        return Ok(new { url, expiresAt = DateTimeOffset.FromUnixTimeSeconds(expires) });
    }

    /// <summary>
    /// The actual file, gated ONLY by a valid signature — deliberately not [Authorize]:
    /// the signed link IS the authorization, exactly like a presigned cloud-storage URL.
    /// An unsigned or expired request is rejected before the file is ever touched.
    /// </summary>
    [AllowAnonymous]
    [HttpGet("documents/{id:guid}/download")]
    public async Task<IActionResult> Download(Guid id, [FromQuery] long expires, [FromQuery] string sig, CancellationToken ct)
    {
        if (!_signing.Verify(id, expires, sig))
            return Unauthorized(new { error = "Invalid or expired download link." });

        var document = await _db.KycDocuments.FindAsync(new object[] { id }, ct);
        if (document is null) return NotFound();

        var stream = _storage.OpenRead(document.StoragePath);
        return File(stream, document.ContentType, document.FileName);
    }

    private int? CurrentUserId()
    {
        var sub = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");
        return int.TryParse(sub, out var id) ? id : null;
    }
}
