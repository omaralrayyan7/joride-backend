# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Commands

All commands run from the repo root (where `JoRideBackend.sln` lives).

- Build: `dotnet build JoRideBackend.sln`
- Run (HTTP, http://localhost:5007): `dotnet run --project JoRideBackend --launch-profile http`
- Run (HTTPS, https://localhost:7082): `dotnet run --project JoRideBackend --launch-profile https`
- Watch / hot reload: `dotnet watch --project JoRideBackend run`
- Restore: `dotnet restore`

There is no test project; `dotnet test` is a no-op.

**Build gotcha:** if a previous `dotnet run` was killed without releasing port 5007 (Ctrl+C in a different terminal, debugger crash), an orphaned `JoRideBackend.exe` may keep `bin/Debug/net8.0/JoRideBackend.exe` locked and the next build fails with `MSB3027 / MSB3021`. Kill the process before rebuilding: `Get-Process -Name JoRideBackend | Stop-Process -Force`.

## Architecture

ASP.NET Core 8.0 (`Microsoft.NET.Sdk.Web`, nullable + implicit usings). Mixed MVC + Web API: `HomeController` is conventional MVC under `{controller=Home}/{action=Index}/{id?}`; the API controllers (`Users`, `Vehicles`, `Trips`, `Pricing`, `VehicleLocation`) are `[ApiController]` returning JSON. Both styles coexist because `Program.cs` calls `AddControllersWithViews()`.

**State is in-memory and process-local.** Every API controller owns a `static readonly List<T>` (`UsersController.users`, `VehiclesController.vehicles`, `TripsController.trips`, `PricingController.pricings`, `VehicleLocationController.locations`). There is no DbContext, no DI-registered repository, no persistence. All data is lost on restart, and IDs are assigned by `list.Count + 1` — which means deleting a record then creating another can collide. Any feature needing persistence has to introduce that layer (EF Core registration in `Program.cs`) rather than extending the existing pattern.

**Cross-controller coupling via static helpers.** Because the lists are `static` and private, controllers expose typed static methods for cross-cutting reads/writes instead of sharing the lists directly:

- `UsersController.Exists(int id)` and `VehiclesController.Exists(int id)` — used by `TripsController.Start` to enforce FK validity (returns 400 `"User not found"` / `"Vehicle not found"`).
- `VehiclesController.SetStatus(int id, string status)` — `TripsController.Start` flips the vehicle to `"InUse"`; `TripsController.End` flips it back to `"Available"`. Vehicle status is *only* maintained through the `/api/trips/start` and `/api/trips/{id}/end` endpoints — the legacy `POST /api/trips` and `PUT /api/trips/{id}` paths still exist but do not enforce FKs or touch vehicle status.
- `*.Seed()` static methods (on `VehiclesController`, `PricingController`, `VehicleLocationController`) are called from `Program.cs` immediately after `builder.Build()` to populate test data. Each guards with `if (list.Count > 0) return;` so re-invocation is safe.

**Auth.** JWT bearer auth wired in `Program.cs`. `JwtTokenService` issues HS256 tokens with `Issuer`/`Audience`/`Key`/`ExpireMinutes` from `Jwt:` config. The signing key lives in `appsettings.Development.json` only (a placeholder that must be replaced for any non-dev deployment); other JWT settings are in `appsettings.json`. Passwords are hashed via `IPasswordHasher<User>` (singleton). The auth endpoints `POST /api/auth/register` and `POST /api/auth/login` live inside `UsersController` but are surfaced through absolute `[HttpPost("/api/auth/...")]` routes that bypass the controller's `[Route("api/[controller]")]` prefix. Admin endpoints (`PUT /api/admin/users/{id}/activate`, `/deactivate`) use the same absolute-route trick. No `[Authorize]` attributes are on the API endpoints yet — auth is configured but not enforced.

**Middleware order matters.** `Program.cs` places `UseCors()` between `UseRouting()` and `UseAuthentication()`, with a default policy of `AllowAnyOrigin / AllowAnyHeader / AllowAnyMethod` for development. If you add auth-enforced endpoints that need to be CORS-reachable, preserve this ordering.

**Routing convention exception.** `VehicleLocationController` uses an explicit `[Route("api/locations")]` instead of the `[controller]` token used elsewhere — endpoints are `/api/locations/...`, not `/api/VehicleLocation/...`. Match that explicit-route style if you add resources whose URL doesn't read well from the type name.

**Razor views are partially orphaned.** `Views/Home` and `Views/Shared/_Layout.cshtml` are still served by `HomeController`. The per-resource view folders (`Views/Users`, `Views/Vehicles`, `Views/Trips`) predate the API conversion and are unreferenced — safe to delete.
