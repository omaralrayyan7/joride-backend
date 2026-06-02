using JoRideBackend.Models;
using JoRideBackend.Services;
using Microsoft.AspNetCore.Mvc;

[ApiController]
[Route("api/notifications")]
public class NotificationsController : ControllerBase
{
    static readonly List<Notification> _notifications = new();
    static int _nextId = 1;
    internal static FirestoreService? _firestore;

    public static void Initialize(List<Notification> loaded, FirestoreService fs)
    {
        _notifications.Clear();
        _notifications.AddRange(loaded);
        _nextId    = loaded.Count > 0 ? loaded.Max(n => n.Id) + 1 : 1;
        _firestore = fs;
    }

    public static void Push(int userId, string title, string body, string type = "system")
    {
        var n = new Notification
        {
            Id        = _nextId++,
            UserId    = userId,
            Title     = title,
            Body      = body,
            Type      = type,
            IsRead    = false,
            CreatedAt = DateTime.UtcNow,
        };
        _notifications.Add(n);
        _ = _firestore?.SaveNotificationAsync(n);   // fire-and-forget
    }

    [HttpGet]
    public ActionResult<IEnumerable<Notification>> GetForUser([FromQuery] int userId)
    {
        var results = _notifications
            .Where(n => n.UserId == userId)
            .OrderByDescending(n => n.CreatedAt)
            .ToList();
        return Ok(results);
    }

    [HttpGet("unread-count")]
    public IActionResult GetUnreadCount([FromQuery] int userId)
    {
        var count = _notifications.Count(n => n.UserId == userId && !n.IsRead);
        return Ok(new { count });
    }

    [HttpPut("{id:int}/read")]
    public async Task<IActionResult> MarkRead(int id)
    {
        var notification = _notifications.FirstOrDefault(n => n.Id == id);
        if (notification is null) return NotFound();
        notification.IsRead = true;
        await (_firestore?.SaveNotificationAsync(notification) ?? Task.CompletedTask);
        return Ok(notification);
    }

    [HttpPut("read-all")]
    public async Task<IActionResult> MarkAllRead([FromQuery] int userId)
    {
        var unread = _notifications.Where(n => n.UserId == userId && !n.IsRead).ToList();
        foreach (var n in unread) n.IsRead = true;
        if (_firestore is not null)
            await Task.WhenAll(unread.Select(n => _firestore.SaveNotificationAsync(n)));
        return Ok(new { updated = unread.Count });
    }
}
