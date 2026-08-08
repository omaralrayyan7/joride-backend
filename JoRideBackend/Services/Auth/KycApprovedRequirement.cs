using Microsoft.AspNetCore.Authorization;

namespace JoRideBackend.Services.Auth
{
    /// <summary>Policy "KycApproved" — the middleware/policy gate for booking-related
    /// endpoints. Reads the "kycStatus" claim set at token issuance (see JwtTokenService);
    /// checking a claim rather than the database keeps this a pure, fast authorization
    /// check with no per-request DB round-trip, at the cost of it reflecting KYC status as
    /// of the caller's last login/refresh — see that claim's doc comment for the bound.</summary>
    public class KycApprovedRequirement : IAuthorizationRequirement
    {
    }

    public class KycApprovedHandler : AuthorizationHandler<KycApprovedRequirement>
    {
        protected override Task HandleRequirementAsync(
            AuthorizationHandlerContext context, KycApprovedRequirement requirement)
        {
            if (context.User.HasClaim("kycStatus", nameof(Models.KycStatus.Approved)))
            {
                context.Succeed(requirement);
            }

            return Task.CompletedTask;
        }
    }
}
