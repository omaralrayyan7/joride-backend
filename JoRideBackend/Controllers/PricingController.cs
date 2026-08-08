using JoRideBackend.Models;
using JoRideBackend.Services;
using Microsoft.AspNetCore.Mvc;

[ApiController]
[Route("api/[controller]")]
public class PricingController : ControllerBase
{
    static readonly List<Pricing> pricings = new();
    static int _nextId = 1;
    static FirestoreService? _firestore;

    public static void Initialize(List<Pricing> loaded, FirestoreService fs)
    {
        pricings.Clear();
        pricings.AddRange(loaded);
        _nextId = loaded.Count > 0 ? loaded.Max(p => p.Id) + 1 : 1;
        _firestore = fs;
    }

    public static IReadOnlyList<Pricing> AllPricings() => pricings;

    public static void SetFirestore(FirestoreService fs) => _firestore = fs;

    public static void Seed()
    {
        if (pricings.Count > 0) return;

        AddSeed("Economy", 0.15m, 8m, 45m);
        AddSeed("Luxury", 0.30m, 16m, 90m);
        AddSeed("SUV", 0.25m, 12m, 70m);
        AddSeed("Electric", 0.20m, 10m, 60m);
    }

    private static void AddSeed(string category, decimal minuteRate, decimal hourlyRate, decimal dailyRate)
    {
        pricings.Add(new Pricing
        {
            Id = _nextId++,
            Category = category,
            MinuteRate = minuteRate,
            HourlyRate = hourlyRate,
            DailyRate = dailyRate,
            IsActive = true,
        });
    }

    public static Pricing GetRatesForCategory(string? category)
    {
        var pricing = pricings.FirstOrDefault(p => p.IsActive &&
            string.Equals(p.Category, category, StringComparison.OrdinalIgnoreCase));

        return pricing ?? pricings.FirstOrDefault(p => p.IsActive && p.Category == "Economy") ?? new Pricing
        {
            Category = "Economy",
            MinuteRate = 0.15m,
            HourlyRate = 8m,
            DailyRate = 45m,
            IsActive = true,
        };
    }

    // E4.1: graduated (tax-bracket-style) billing. The previous version picked a SINGLE tier
    // for the whole overtime duration (>=1440min -> all-days, >=60min -> all-hours, else
    // all-minutes), which put a cliff right at each boundary: 59 minutes of overtime cost
    // 59*MinuteRate while 60 minutes cost only 1*HourlyRate — i.e. running over for LESS time
    // could cost MORE. Decomposing into whole-days + whole-hours + remaining-minutes, each
    // billed at its own rate, is monotonic by construction (more time never costs less) and
    // correctly blends a trip that crosses from the hourly into the daily tier instead of
    // rounding the whole overage up to extra whole days.
    public static (int billedMinutes, decimal fare, string rateApplied) CalculateOvertimeFare(string? category, DateTime scheduledEndUtc, DateTime actualEndUtc)
    {
        var overtimeSeconds = (actualEndUtc - scheduledEndUtc).TotalSeconds;
        if (overtimeSeconds <= 0) return (0, 0m, "none");

        var overtimeMinutes = Math.Max(1, (int)Math.Ceiling(overtimeSeconds / 60d));
        var rates = GetRatesForCategory(category);

        var days = overtimeMinutes / 1440;
        var remainderAfterDays = overtimeMinutes % 1440;
        var hours = remainderAfterDays / 60;
        var minutes = remainderAfterDays % 60;

        var fare = days * rates.DailyRate + hours * rates.HourlyRate + minutes * rates.MinuteRate;

        var rateApplied = days > 0 ? "day" : hours > 0 ? "hour" : "min";
        return (overtimeMinutes, fare, rateApplied);
    }

    [HttpGet]
    public ActionResult<IEnumerable<Pricing>> GetAll() => pricings;

    [HttpGet("{id:int}")]
    public ActionResult<Pricing> Get(int id)
    {
        var pricing = pricings.FirstOrDefault(p => p.Id == id);
        return pricing is null ? NotFound() : pricing;
    }

    [HttpGet("category/{category}")]
    public ActionResult<Pricing> GetByCategory(string category) => GetRatesForCategory(category);

    [HttpPost]
    public ActionResult<Pricing> Create(Pricing pricing)
    {
        pricing.Id = _nextId++;
        pricings.Add(pricing);
        _ = _firestore?.SavePricingAsync(pricing);
        return CreatedAtAction(nameof(Get), new { id = pricing.Id }, pricing);
    }

    [HttpPut("{id:int}")]
    public IActionResult Update(int id, Pricing update)
    {
        var pricing = pricings.FirstOrDefault(p => p.Id == id);
        if (pricing is null) return NotFound();

        pricing.Category = update.Category;
        pricing.MinuteRate = update.MinuteRate;
        pricing.HourlyRate = update.HourlyRate;
        pricing.DailyRate = update.DailyRate;
        pricing.IsActive = update.IsActive;
        _ = _firestore?.SavePricingAsync(pricing);
        return NoContent();
    }

    [HttpDelete("{id:int}")]
    public IActionResult Delete(int id)
    {
        var pricing = pricings.FirstOrDefault(p => p.Id == id);
        if (pricing is null) return NotFound();

        pricings.Remove(pricing);
        _ = _firestore?.DeletePricingAsync(id);
        return NoContent();
    }
}
