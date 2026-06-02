namespace JoRideBackend.Services;

public interface ILicenseVerification
{
    Task<bool> VerifyAsync(string licenseNumber, string idNumber);

    /// <summary>
    /// Strict check against the seeded ID/License combinations.
    /// Returns true only when the license exists AND the supplied ID matches it exactly.
    /// </summary>
    bool IsValidCombination(string? licenseNumber, string? idNumber);

    IReadOnlyDictionary<string, string> SeededCombinations { get; }
}

public class LicenseVerification : ILicenseVerification
{
    private readonly IConfiguration _config;

    // Seeded ID/License combinations (License -> National ID).
    // Replace with a real Jordan license provider integration when available.
    private static readonly Dictionary<string, string> ValidLicenses = new(StringComparer.OrdinalIgnoreCase)
    {
        { "JO-123456", "9901012345" },
        { "JO-234567", "9801023456" },
        { "JO-345678", "9701034567" },
        { "JO-456789", "9601045678" },
        { "JO-567890", "9501056789" },
        { "JO-678901", "9401067890" },
        { "JO-789012", "9301078901" },
        { "JO-890123", "9201089012" },
    };

    public LicenseVerification(IConfiguration config)
    {
        _config = config;
    }

    public IReadOnlyDictionary<string, string> SeededCombinations => ValidLicenses;

    public bool IsValidCombination(string? licenseNumber, string? idNumber)
    {
        if (string.IsNullOrWhiteSpace(licenseNumber) || string.IsNullOrWhiteSpace(idNumber))
            return false;

        var license = licenseNumber.Trim();
        var id = idNumber.Trim();

        if (!ValidLicenses.TryGetValue(license, out var expectedId))
            return false;

        return string.Equals(expectedId, id, StringComparison.Ordinal);
    }

    public Task<bool> VerifyAsync(string licenseNumber, string idNumber)
        => Task.FromResult(IsValidCombination(licenseNumber, idNumber));
}
