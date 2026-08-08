using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using JoRideBackend.Models;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace JoRideBackend.Services
{
    public class JwtOptions
    {
        public string Issuer { get; set; } = "";
        public string Audience { get; set; } = "";
        public string Key { get; set; } = "";
        public int ExpireMinutes { get; set; } = 60;
    }

    public class JwtTokenService
    {
        private readonly JwtOptions options;

        public JwtTokenService(IOptions<JwtOptions> options)
        {
            this.options = options.Value;
        }

        public (string Token, DateTime ExpiresAt) IssueToken(User user)
        {
            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.Key));
            var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
            var expiresAt = DateTime.UtcNow.AddMinutes(options.ExpireMinutes);

            var claims = new List<Claim>
            {
                new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
                new(JwtRegisteredClaimNames.Email, user.Email ?? ""),
                new(JwtRegisteredClaimNames.Name, user.Name ?? ""),
                new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
                new("role", user.IsAdmin ? "admin" : "user"),
                // Snapshot at issuance, not a live lookup — the "KycApproved" policy reads
                // this claim rather than hitting Firestore on every authorized request. A
                // change in KYC status is visible on the token's next issuance (login or
                // refresh), bounded by Jwt:ExpireMinutes.
                new("kycStatus", user.KycStatus.ToString()),
            };

            var jwt = new JwtSecurityToken(
                issuer: options.Issuer,
                audience: options.Audience,
                claims: claims,
                expires: expiresAt,
                signingCredentials: creds);

            return (new JwtSecurityTokenHandler().WriteToken(jwt), expiresAt);
        }
    }
}
