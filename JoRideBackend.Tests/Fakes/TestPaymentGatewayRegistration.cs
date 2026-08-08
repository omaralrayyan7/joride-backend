using JoRideBackend.Services.Payments;
using Microsoft.Extensions.DependencyInjection;

namespace JoRideBackend.Tests.Fakes;

/// <summary>
/// The explicit, guarded way to wire <see cref="FakePaymentGateway"/> into a DI
/// container. Any future integration-test host (e.g. a WebApplicationFactory-based test)
/// must go through this — never register the fake as a plain service or a
/// Development/Production fallback. See <see cref="Tests.PaymentGatewayRegistrationTests"/>
/// for proof this refuses to register outside a "Testing" environment.
/// </summary>
public static class TestPaymentGatewayRegistration
{
    public static void AddFakePaymentGatewayForTesting(IServiceCollection services, string? environmentName)
    {
        if (environmentName != "Testing")
        {
            throw new InvalidOperationException(
                $"Refusing to register FakePaymentGateway: environment is '{environmentName}', not 'Testing'.");
        }

        services.AddScoped<IPaymentGateway, FakePaymentGateway>();
    }
}
