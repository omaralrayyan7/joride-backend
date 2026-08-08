using JoRideBackend.Services.Payments;
using JoRideBackend.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;

namespace JoRideBackend.Tests;

/// <summary>
/// Proves FakePaymentGateway can only ever be wired up under an explicit "Testing"
/// environment name — never Development, Production, or anything else. This is on top
/// of (not instead of) the structural guarantee: JoRideBackend (the shipped app) has no
/// reference to JoRideBackend.Tests at all, so FakePaymentGateway's type doesn't even
/// exist in the production assembly.
/// </summary>
public class PaymentGatewayRegistrationTests
{
    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData(null)]
    public void Refuses_to_register_outside_Testing_environment(string? environmentName)
    {
        var services = new ServiceCollection();

        Assert.Throws<InvalidOperationException>(
            () => TestPaymentGatewayRegistration.AddFakePaymentGatewayForTesting(services, environmentName));

        // And critically: nothing got registered by the attempt.
        using var provider = services.BuildServiceProvider();
        Assert.Null(provider.GetService<IPaymentGateway>());
    }

    [Fact]
    public void Registers_fake_gateway_only_when_environment_is_exactly_Testing()
    {
        var services = new ServiceCollection();

        TestPaymentGatewayRegistration.AddFakePaymentGatewayForTesting(services, "Testing");

        using var provider = services.BuildServiceProvider();
        var gateway = provider.GetRequiredService<IPaymentGateway>();
        Assert.IsType<FakePaymentGateway>(gateway);
    }
}
