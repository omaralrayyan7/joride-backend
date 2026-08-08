using System.Security.Cryptography;
using System.Text;

namespace JoRideBackend.Services.Auth
{
    /// <summary>
    /// Signs and verifies time-limited download links for KYC documents — the
    /// "signed/time-limited access only" requirement, implemented the same way a presigned
    /// S3 URL works: the signature itself is the authorization, valid only until it expires.
    /// </summary>
    public class KycSigningService
    {
        private readonly string? _secret;

        public KycSigningService(IConfiguration configuration)
        {
            _secret = configuration["KYC_DOCUMENT_SIGNING_SECRET"];
        }

        public bool IsConfigured => !string.IsNullOrWhiteSpace(_secret);

        public (string Signature, long ExpiresUnixSeconds) Sign(Guid documentId, TimeSpan validFor)
        {
            EnsureConfigured();
            var expiresUnixSeconds = DateTimeOffset.UtcNow.Add(validFor).ToUnixTimeSeconds();
            return (ComputeSignature(documentId, expiresUnixSeconds), expiresUnixSeconds);
        }

        public bool Verify(Guid documentId, long expiresUnixSeconds, string providedSignature)
        {
            if (!IsConfigured || string.IsNullOrEmpty(providedSignature))
            {
                return false;
            }

            if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() > expiresUnixSeconds)
            {
                return false; // expired
            }

            var expected = ComputeSignature(documentId, expiresUnixSeconds);
            return CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(expected.ToUpperInvariant()),
                Encoding.UTF8.GetBytes(providedSignature.ToUpperInvariant()));
        }

        private string ComputeSignature(Guid documentId, long expiresUnixSeconds)
        {
            EnsureConfigured();
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_secret!));
            var payload = $"{documentId:N}:{expiresUnixSeconds}";
            return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload)));
        }

        private void EnsureConfigured()
        {
            if (!IsConfigured)
            {
                throw new InvalidOperationException("KYC_DOCUMENT_SIGNING_SECRET must be configured to sign/verify KYC document links.");
            }
        }
    }
}
