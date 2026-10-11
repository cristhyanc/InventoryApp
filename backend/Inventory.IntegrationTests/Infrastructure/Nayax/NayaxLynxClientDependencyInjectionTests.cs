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

    /// <summary>
    /// The base URL is the one global Nayax setting left (issue #520), and it reaches the client as
    /// the typed <c>HttpClient</c>'s base address rather than through any configuration the client
    /// holds itself.
    /// </summary>
    [Fact]
    public void AddNayaxLynxClient_configures_the_operational_api_base_address()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddNayaxLynxClient(new NayaxLynxOptions { BaseUrl = "https://qa-lynx.nayax.com" });

        using var provider = services.BuildServiceProvider();
        var http = provider.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(INayaxLynxClient));

        Assert.Equal(new Uri("https://qa-lynx.nayax.com/operational/v1/"), http.BaseAddress);
    }

    /// <summary>
    /// Registration must not need a global operator id or token any more, and must not put one in
    /// the container: the credentials are per business, resolved per call, so there is nothing here
    /// a later consumer could fall back to.
    /// </summary>
    [Fact]
    public void AddNayaxLynxClient_needs_no_operator_id_or_token_and_registers_neither()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddNayaxLynxClient(new NayaxLynxOptions());

        using var provider = services.BuildServiceProvider();
        Assert.Null(provider.GetService<NayaxLynxOptions>());
    }

    /// <summary>
    /// The one registered Nayax client resolves its credentials from the Application port, so a
    /// container without that port cannot produce a client at all - there is no constructor left
    /// that takes a baked-in operator id and token.
    /// </summary>
    [Fact]
    public void The_registered_client_is_constructed_from_the_per_business_credential_provider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNayaxLynxClient(new NayaxLynxOptions { BaseUrl = "https://lynx.nayax.com" });
        services.AddScoped<INayaxRequestCredentialProvider>(
            _ => new FakeNayaxRequestCredentialProvider("op-1", "fake-token-not-a-real-credential"));

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        Assert.IsType<NayaxLynxClient>(scope.ServiceProvider.GetRequiredService<INayaxLynxClient>());
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
