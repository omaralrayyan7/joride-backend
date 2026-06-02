using JoRideBackend.Models;
using JoRideBackend.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

[ApiController]
[Route("api/admin/audit")]
[Authorize(Policy = "AdminOnly")]
public class AuditController : ControllerBase
{
    static readonly List<AuditLog> _logs = new();
    static int _nextId = 1;
    static FirestoreService? _firestore;
    static readonly object _lock = new();

    public static void Initialize(List<AuditLog> loaded, FirestoreService fs)
    {
        lock (_lock)
        {
            _logs.Clear();
            _logs.AddRange(loaded);
            _nextId = loaded.Count > 0 ? loaded.Max(l => l.Id) + 1 : 1;
            _firestore = fs;
        }
    }

    /// <summary>
    /// Records an audit entry (fire-and-forget Firestore persist). Safe to call from anywhere.
    /// </summary>
    public static void Log(string action, string entityType, int entityId, string actor, string actorRole, string? details = null)
    {
        AuditLog entry;
        lock (_lock)
        {
            entry = new AuditLog
            {
                Id         = _nextId++,
                Timestamp  = DateTime.UtcNow,
                Action     = action,
                EntityType = entityType,
                EntityId   = entityId,
                Actor      = actor,
                ActorRole  = actorRole,
                Details    = details,
            };
            _logs.Add(entry);
        }
        _ = _firestore?.SaveAuditLogAsync(entry);
    }

    [HttpGet]
    public ActionResult<IEnumerable<AuditLog>> Get(
        [FromQuery] string? entityType,
        [FromQuery] int? entityId,
        [FromQuery] string? action,
        [FromQuery] int limit = 200)
    {
        IEnumerable<AuditLog> q;
        lock (_lock) { q = _logs.ToList(); }

        if (!string.IsNullOrWhiteSpace(entityType))
            q = q.Where(l => string.Equals(l.EntityType, entityType, StringComparison.OrdinalIgnoreCase));
        if (entityId.HasValue)
            q = q.Where(l => l.EntityId == entityId.Value);
        if (!string.IsNullOrWhiteSpace(action))
            q = q.Where(l => (l.Action ?? "").Contains(action, StringComparison.OrdinalIgnoreCase));

        return q.OrderByDescending(l => l.Timestamp).Take(Math.Clamp(limit, 1, 1000)).ToList();
    }
}
