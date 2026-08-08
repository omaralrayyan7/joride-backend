using System.Security.Claims;
using JoRideBackend.Services.Auth;
using Microsoft.AspNetCore.Authorization;

namespace JoRideBackend.Tests.Auth;

public class KycApprovedHandlerTests
{
    private static async Task<bool> EvaluateAsync(ClaimsPrincipal user)
    {
        var handler = new KycApprovedHandler();
        var requirement = new KycApprovedRequirement();
        var context = new AuthorizationHandlerContext(new[] { requirement }, user, resource: null);

        await handler.HandleAsync(context);

        return context.HasSucceeded;
    }

    private static ClaimsPrincipal UserWith(params Claim[] claims) =>
        new(new ClaimsIdentity(claims, "TestAuth"));

    [Fact]
    public async Task Succeeds_when_kycStatus_claim_is_Approved()
    {
        var user = UserWith(new Claim("kycStatus", "Approved"));

        Assert.True(await EvaluateAsync(user));
    }

    [Theory]
    [InlineData("Pending")]
    [InlineData("Rejected")]
    public async Task Fails_when_kycStatus_claim_is_not_Approved(string status)
    {
        var user = UserWith(new Claim("kycStatus", status));

        Assert.False(await EvaluateAsync(user));
    }

    [Fact]
    public async Task Fails_when_kycStatus_claim_is_missing_entirely()
    {
        var user = UserWith(new Claim("role", "user"));

        Assert.False(await EvaluateAsync(user));
    }
}
