using Microsoft.AspNetCore.Mvc;

namespace JoRideBackend.Controllers
{
    /// <summary>
    /// E7: serves the admin dashboard shell (Views/Dashboard/Index.cshtml) at /Dashboard.
    /// Named "Dashboard" rather than "Admin" only to avoid colliding with the existing
    /// api/admin AdminController class name — this is unrelated to and does not touch that
    /// controller (or any other existing one).
    ///
    /// Plain MVC controller, not an API controller, and deliberately does no authorization
    /// or data fetching itself: the app has only JWT bearer auth configured (see
    /// Program.cs), so a normal browser page request carries no Authorization header and
    /// HttpContext.User would always be anonymous here — there's no ambient session to gate
    /// on server-side. The page it serves is just a shell (login form + empty sections);
    /// every real action (KYC review, device commands, payments, overdue trips) is a
    /// client-side fetch() call straight to the existing, already-auth-enforced JSON APIs
    /// using a JWT obtained from POST /api/auth/login and kept in sessionStorage. Nothing
    /// sensitive is rendered server-side, so the shell needing no auth of its own doesn't
    /// leak anything.
    /// </summary>
    public class DashboardController : Controller
    {
        public IActionResult Index()
        {
            return View();
        }
    }
}
