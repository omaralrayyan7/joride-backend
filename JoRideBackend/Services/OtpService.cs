using System.Security.Cryptography;
using Microsoft.Extensions.Caching.Memory;

namespace JoRideBackend.Services;

public interface IOtpService
{
    string GenerateAndStoreCode(string key);
    bool VerifyCode(string key, string code);
}

public class OtpService : IOtpService
{
    private readonly IMemoryCache _cache;
    private static readonly TimeSpan OtpLifetime = TimeSpan.FromMinutes(5);

    public OtpService(IMemoryCache cache) => _cache = cache;

    public string GenerateAndStoreCode(string key)
    {
        var code = RandomNumberGenerator.GetInt32(100000, 1000000).ToString();
        _cache.Set("otp:" + key.Trim(), code, OtpLifetime);
        return code;
    }

    public bool VerifyCode(string key, string code)
    {
        var cacheKey = "otp:" + key.Trim();
        if (!_cache.TryGetValue(cacheKey, out string? saved)) return false;
        if (!string.Equals(saved, code?.Trim(), StringComparison.Ordinal)) return false;
        _cache.Remove(cacheKey);
        return true;
    }
}   