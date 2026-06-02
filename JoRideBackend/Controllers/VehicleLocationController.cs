using JoRideBackend.Models;
using Microsoft.AspNetCore.Mvc;

[ApiController]
[Route("api/locations")]
public class VehicleLocationController : ControllerBase
{
    static readonly List<VehicleLocation> locations = new();

    public static void Seed()
    {
        if (locations.Count > 0) return;

        var now = DateTime.UtcNow;
        locations.Add(new VehicleLocation
        {
            Id = locations.Count + 1,
            VehicleId = 1,
            Latitude = 32.0252,
            Longitude = 35.8850,
            Speed = 0,
            Fuel = 87.5,
            Timestamp = now.AddMinutes(-2),
        });
        locations.Add(new VehicleLocation
        {
            Id = locations.Count + 1,
            VehicleId = 2,
            Latitude = 32.0287,
            Longitude = 35.8912,
            Speed = 42.3,
            Fuel = 64.0,
            Timestamp = now.AddMinutes(-1),
        });
        locations.Add(new VehicleLocation
        {
            Id = locations.Count + 1,
            VehicleId = 3,
            Latitude = 32.0211,
            Longitude = 35.8794,
            Speed = 18.7,
            Fuel = 92.1,
            Timestamp = now,
        });
    }

    [HttpGet("vehicle/{vehicleId:int}")]
    public ActionResult<IEnumerable<VehicleLocation>> GetHistory(int vehicleId) =>
        locations.Where(l => l.VehicleId == vehicleId)
                 .OrderByDescending(l => l.Timestamp)
                 .ToList();

    [HttpGet("latest/{vehicleId:int}")]
    public ActionResult<VehicleLocation> GetLatest(int vehicleId)
    {
        var latest = locations.Where(l => l.VehicleId == vehicleId)
                              .OrderByDescending(l => l.Timestamp)
                              .FirstOrDefault();
        return latest is null ? NotFound() : latest;
    }

    [HttpPost]
    public ActionResult<VehicleLocation> Create(VehicleLocationRequest request)
    {
        var location = new VehicleLocation
        {
            Id = locations.Count + 1,
            VehicleId = request.VehicleId,
            Latitude = request.Latitude,
            Longitude = request.Longitude,
            Speed = request.Speed,
            Fuel = request.Fuel,
            Timestamp = DateTime.UtcNow,
        };
        locations.Add(location);
        return CreatedAtAction(nameof(GetLatest), new { vehicleId = location.VehicleId }, location);
    }
}
