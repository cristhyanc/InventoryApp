using Inventory.Application.Nayax;
using Inventory.Infrastructure;
using Inventory.Infrastructure.Nayax;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace InventoryApi.Tests.Infrastructure.Nayax;

// Guards the DI wiring itself: AddNayaxLynxClient must attach NayaxResilienceHandler to the
// HttpClient it registers, not just compile against it. A future edit that drops the
// AddHttpMessageHandler<T>() call would otherwise silently ship with no timeout/retry/circuit
// breaker at all, and every other Nayax test (which builds NayaxLynxClient directly against a
// fake handler, bypassing this composition) would keep passing.
public sealed class NayaxLynxClientDependencyInjectionTests
{
    [Fact]
    public void AddNayaxLynxClient_attaches_the_resilience_handler_to_the_registered_HttpClient()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var options = new NayaxLynxOptions { BaseUrl = "https://lynx.nayax.com", OperatorId = "op-1" };

        services.AddNayaxLynxClient(options);

        using var provider = services.BuildServiceProvider();
        var handlerFactory = provider.GetRequiredService<IHttpMessageHandlerFactory>();
        var outermostHandler = handlerFactory.CreateHandler(nameof(INayaxLynxClient));

        Assert.True(PipelineContains<NayaxResilienceHandler>(outermostHandler));
    }

    private static bool PipelineContains<THandler>(HttpMessageHandler handler)
        where THandler : DelegatingHandler
    {
        var current = handler;
        while (current is DelegatingHandler delegating)
        {
            if (delegating is THandler)
            {
                return true;
            }

            current = delegating.InnerHandler;
        }

        return false;
    }
}
