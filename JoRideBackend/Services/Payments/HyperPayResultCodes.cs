using System.Text.RegularExpressions;

namespace JoRideBackend.Services.Payments
{
    /// <summary>
    /// HyperPay/OPP result codes are matched by regex per their integration docs
    /// (successful transaction codes match roughly ^(000\.000\.|000\.100\.1|000\.[36]),
    /// with 000.200 meaning "pending"). Groundwork-scope: covers the commonly documented
    /// success prefixes — revisit against HyperPay's exact code list once live credentials
    /// exist and real responses can be observed. Shared between HyperPayGateway (status
    /// polling) and HyperPayWebhookService (push notifications) so both agree on success.
    /// </summary>
    public static class HyperPayResultCodes
    {
        public static bool IsSuccess(string? code) =>
            !string.IsNullOrEmpty(code) &&
            (code.StartsWith("000.000.", StringComparison.Ordinal) ||
             code.StartsWith("000.100.1", StringComparison.Ordinal) ||
             Regex.IsMatch(code, @"^000\.[36]"));
    }
}
