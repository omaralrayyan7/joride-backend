using JoRideBackend.Models;
using JoRideBackend.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

[ApiController]
[Route("api/[controller]")]
public class VehiclesController : ControllerBase
{
    static readonly List<Vehicle> vehicles = new();
    static int _nextId = 1;
    static FirestoreService? _firestore;

    public static void Initialize(List<Vehicle> loaded, FirestoreService fs)
    {
        vehicles.Clear();
        vehicles.AddRange(loaded);
        _nextId = loaded.Count > 0 ? loaded.Max(v => v.Id) + 1 : 1;
        _firestore = fs;
    }

    public static void SetFirestore(FirestoreService fs) => _firestore = fs;

    public static bool Exists(int id) => vehicles.Any(v => v.Id == id);

    public static Vehicle? GetVehicleById(int id) => vehicles.FirstOrDefault(v => v.Id == id);

    // Only Available AND visible vehicles can be booked
    public static bool IsAvailable(int id) =>
        vehicles.Any(v => v.Id == id && v.Status == "Available" && v.IsVisible);

    public static void SetStatus(int id, string status)
    {
        var vehicle = vehicles.FirstOrDefault(v => v.Id == id);
        if (vehicle is not null)
        {
            vehicle.Status = status;
            _ = _firestore?.SaveVehicleAsync(vehicle);
        }
    }

    public static IReadOnlyList<Vehicle> AllVehicles() => vehicles;

    public static void Seed()
    {
        if (vehicles.Count > 0) return;

        // ── 5 vehicles in Amman only, identified JO-AMM01 to JO-AMM05 ──────────
        // Locations spread across key Amman districts:
        //   JO-AMM01 – Downtown (Al-Balad)           31.9510, 35.9300
        //   JO-AMM02 – Abdali / 1st Circle            31.9665, 35.9130
        //   JO-AMM03 – Sweifieh / 3rd Circle          31.9558, 35.8850
        //   JO-AMM04 – Zahran / 4th Circle            31.9620, 35.9050
        //   JO-AMM05 – Mecca Mall area / 7th Circle   31.9480, 35.8700

        // Toyota Corolla — Economy (Downtown Amman)
        vehicles.Add(new Vehicle
        {
            Id           = 1,
            LicensePlate = "JO-AMM01",
            Model        = "Toyota Corolla",
            Category     = "Economy",
            Color        = "White",
            Status       = "Available",
            Latitude     = 31.9510,
            Longitude    = 35.9300,
            FuelLevel    = 85,
            IsVisible    = true,
            ImageUrl     = "https://img.icons8.com/color/144/toyota-corolla.png",
            BrandLogoUrl = "https://logo.clearbit.com/toyota.com",
        });

        // BMW 320i — Luxury (Abdali / 1st Circle)
        vehicles.Add(new Vehicle
        {
            Id           = 2,
            LicensePlate = "JO-AMM02",
            Model        = "BMW 320i",
            Category     = "Luxury",
            Color        = "Black",
            Status       = "Available",
            Latitude     = 31.9665,
            Longitude    = 35.9130,
            FuelLevel    = 92,
            IsVisible    = true,
            ImageUrl     = "https://img.icons8.com/color/144/bmw-e46.png",
            BrandLogoUrl = "https://logo.clearbit.com/bmw.com",
        });

        // Toyota Land Cruiser — SUV (Sweifieh)
        vehicles.Add(new Vehicle
        {
            Id           = 3,
            LicensePlate = "JO-AMM03",
            Model        = "Toyota Land Cruiser",
            Category     = "SUV",
            Color        = "Silver",
            Status       = "Available",
            Latitude     = 31.9558,
            Longitude    = 35.8850,
            FuelLevel    = 78,
            IsVisible    = true,
            ImageUrl     = "https://img.icons8.com/color/144/jeep.png",
            BrandLogoUrl = "https://logo.clearbit.com/toyota.com",
        });

        // Tesla Model 3 — Electric (Zahran)
        vehicles.Add(new Vehicle
        {
            Id           = 4,
            LicensePlate = "JO-AMM04",
            Model        = "Tesla Model 3",
            Category     = "Electric",
            Color        = "Blue",
            Status       = "Available",
            Latitude     = 31.9620,
            Longitude    = 35.9050,
            FuelLevel    = 95,
            IsVisible    = true,
            ImageUrl     = "https://img.icons8.com/color/144/tesla-model-3.png",
            BrandLogoUrl = "https://logo.clearbit.com/tesla.com",
        });

        // Hyundai Elantra — Economy (Mecca Mall / 7th Circle)
        vehicles.Add(new Vehicle
        {
            Id           = 5,
            LicensePlate = "JO-AMM05",
            Model        = "Hyundai Elantra",
            Category     = "Economy",
            Color        = "Red",
            Status       = "Available",
            Latitude     = 31.9480,
            Longitude    = 35.8700,
            FuelLevel    = 70,
            IsVisible    = true,
            ImageUrl     = "https://img.icons8.com/color/144/car--v1.png",
            BrandLogoUrl = "https://logo.clearbit.com/hyundai.com",
        });
        // ─────────────────────────────────────────────────────────────────────
    }

    [HttpGet]
    public ActionResult<IEnumerable<Vehicle>> GetAll([FromQuery] string? search)
    {
        var q = vehicles.AsEnumerable();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim();
            bool C(string? v) => v is not null && v.Contains(s, StringComparison.OrdinalIgnoreCase);
            q = q.Where(v => C(v.LicensePlate) || C(v.Model) || C(v.Category) || C(v.Color) || C(v.Status));
        }
        return q.ToList();
    }

    // Returns only Available AND IsVisible vehicles — shown on the user map
    [HttpGet("available")]
    public ActionResult<IEnumerable<Vehicle>> GetAvailable() =>
        vehicles.Where(v => v.Status == "Available" && v.IsVisible).ToList();

    // Admin: hide a vehicle from the user map and booking
    [Authorize(Policy = "AdminOnly")]
    [HttpPut("{id:int}/hide")]
    public IActionResult Hide(int id)
    {
        var vehicle = vehicles.FirstOrDefault(v => v.Id == id);
        if (vehicle is null) return NotFound();
        vehicle.IsVisible = false;
        _ = _firestore?.SaveVehicleAsync(vehicle);
        AuditController.Log("VehicleHidden", "Vehicle", id, ActorLabel(), "Admin",
            $"Vehicle {vehicle.LicensePlate} hidden from map.");
        return Ok(new { vehicleId = id, isVisible = false });
    }

    // Admin: restore a vehicle to the user map
    [Authorize(Policy = "AdminOnly")]
    [HttpPut("{id:int}/show")]
    public IActionResult Show(int id)
    {
        var vehicle = vehicles.FirstOrDefault(v => v.Id == id);
        if (vehicle is null) return NotFound();
        vehicle.IsVisible = true;
        _ = _firestore?.SaveVehicleAsync(vehicle);
        AuditController.Log("VehicleRestored", "Vehicle", id, ActorLabel(), "Admin",
            $"Vehicle {vehicle.LicensePlate} restored to map.");
        return Ok(new { vehicleId = id, isVisible = true });
    }

    [HttpGet("{id:int}")]
    public ActionResult<Vehicle> Get(int id)
    {
        var vehicle = vehicles.FirstOrDefault(v => v.Id == id);
        return vehicle is null ? NotFound() : vehicle;
    }

    [Authorize(Policy = "AdminOnly")]
    [HttpPost]
    public ActionResult<Vehicle> Create(Vehicle vehicle)
    {
        vehicle.Id = _nextId++;
        vehicle.Status ??= "Available";
        vehicles.Add(vehicle);
        _ = _firestore?.SaveVehicleAsync(vehicle);
        var actor = ActorLabel();
        AuditController.Log("VehicleCreated", "Vehicle", vehicle.Id, actor, "Admin",
            $"Created {vehicle.LicensePlate} ({vehicle.Model}, {vehicle.Category}).");
        return CreatedAtAction(nameof(Get), new { id = vehicle.Id }, vehicle);
    }

    [Authorize(Policy = "AdminOnly")]
    [HttpPut("{id:int}")]
    public IActionResult Update(int id, Vehicle update)
    {
        var vehicle = vehicles.FirstOrDefault(v => v.Id == id);
        if (vehicle is null) return NotFound();

        vehicle.LicensePlate = update.LicensePlate;
        vehicle.Model        = update.Model;
        vehicle.Category     = update.Category;
        vehicle.Color        = update.Color;
        vehicle.Status       = update.Status;
        vehicle.Latitude     = update.Latitude;
        vehicle.Longitude    = update.Longitude;
        vehicle.FuelLevel    = update.FuelLevel;
        vehicle.ImageUrl     = update.ImageUrl;
        _ = _firestore?.SaveVehicleAsync(vehicle);
        AuditController.Log("VehicleUpdated", "Vehicle", vehicle.Id, ActorLabel(), "Admin",
            $"Updated {vehicle.LicensePlate}.");
        return NoContent();
    }

    [Authorize(Policy = "AdminOnly")]
    [HttpDelete("{id:int}")]
    public IActionResult Delete(int id)
    {
        var vehicle = vehicles.FirstOrDefault(v => v.Id == id);
        if (vehicle is null) return NotFound();

        vehicles.Remove(vehicle);
        _ = _firestore?.DeleteVehicleAsync(id);
        AuditController.Log("VehicleDeleted", "Vehicle", id, ActorLabel(), "Admin",
            $"Deleted {vehicle.LicensePlate} ({vehicle.Model}).");
        return NoContent();
    }

    private string ActorLabel()
    {
        var actorId   = HttpContext.User.FindFirst("sub")?.Value ?? "?";
        var actorName = HttpContext.User.FindFirst("name")?.Value ?? "admin";
        return $"Admin: {actorName} (#{actorId})";
    }
}
