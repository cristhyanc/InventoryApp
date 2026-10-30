using Inventory.Application.ConnectedMachines;
using Inventory.Application.Nayax;
using Inventory.Application.Tenancy;
using Inventory.Application.Time;
using Inventory.Domain.ConnectedMachines;
using Inventory.Infrastructure;
using Inventory.Infrastructure.Nayax;
using InventoryApi.Tests.Application.Time;
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
    ///
    /// <para>Since issues #840 and #842 the port resolves to the caching decorator, which still
    /// takes that same credential provider (its refusals are what run before every cache hit) and
    /// still wraps the one HTTP client, which also takes it. Since issue #978 the connected-machine
    /// counter sits outside both, so the port resolves to <em>it</em>; it changes no refusal, because
    /// every refusal belongs to the credential provider below it.</para>
    /// </summary>
    [Fact]
    public void The_registered_client_is_constructed_from_the_per_business_credential_provider()
    {
        using var provider = ContainerWithTheRegisteredClient();
        using var scope = provider.CreateScope();

        Assert.IsType<ConnectedMachineCountingNayaxLynxClient>(
            scope.ServiceProvider.GetRequiredService<INayaxLynxClient>());
        Assert.IsType<NayaxLynxClient>(scope.ServiceProvider.GetRequiredService<NayaxLynxClient>());
    }

    /// <summary>
    /// And without that port there is no client at all: the registration has no constructor left
    /// that takes a baked-in operator id and token, so a container missing the per-business
    /// credential provider fails to produce one rather than falling back to something global.
    /// </summary>
    [Fact]
    public void Without_the_per_business_credential_provider_no_client_can_be_produced()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNayaxLynxClient(new NayaxLynxOptions { BaseUrl = "https://lynx.nayax.com" });
        services.AddScoped<IBusinessScope>(_ => new BusinessScope());
        services.AddScoped<IConnectedMachineSnapshotRecorder>(_ => new NeverCalledSnapshotRecorder());

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        Assert.Throws<InvalidOperationException>(
            () => scope.ServiceProvider.GetRequiredService<INayaxLynxClient>());
    }

    /// <summary>
    /// The connected-machine counter of issue #978 is the outermost registration, and the fleet read
    /// the daily job uses is registered beside it over the <em>inner</em> typed client - never over
    /// the port, which is the decorator itself.
    /// </summary>
    [Fact]
    public void The_connected_machine_fleet_reader_is_registered_over_the_inner_client()
    {
        using var provider = ContainerWithTheRegisteredClient();
        using var scope = provider.CreateScope();

        Assert.IsType<NayaxConnectedMachineFleetReader>(
            scope.ServiceProvider.GetRequiredService<IConnectedMachineFleetReader>());
    }

    /// <summary>
    /// Both caches - the machine lists of issue #840 and the machine products of issue #842 - are one
    /// object each for the whole process, because a per-request cache would answer nothing a second
    /// page load, or a simultaneous one, asked for, which is the entire point of both issues. The
    /// decorator in front of them stays per resolution, like the HTTP client it wraps, so it reads the
    /// current request's business scope and credentials.
    /// </summary>
    [Fact]
    public void Both_caches_are_shared_by_every_request_and_the_decorator_is_not()
    {
        using var provider = ContainerWithTheRegisteredClient();
        using var first = provider.CreateScope();
        using var second = provider.CreateScope();

        Assert.Same(
            first.ServiceProvider.GetRequiredService<NayaxMachineListCache>(),
            second.ServiceProvider.GetRequiredService<NayaxMachineListCache>());
        Assert.Same(
            first.ServiceProvider.GetRequiredService<NayaxMachineProductsCache>(),
            second.ServiceProvider.GetRequiredService<NayaxMachineProductsCache>());
        Assert.NotSame(
            first.ServiceProvider.GetRequiredService<INayaxLynxClient>(),
            second.ServiceProvider.GetRequiredService<INayaxLynxClient>());
    }

    private static ServiceProvider ContainerWithTheRegisteredClient()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNayaxLynxClient(new NayaxLynxOptions { BaseUrl = "https://lynx.nayax.com" });
        services.AddScoped<INayaxRequestCredentialProvider>(
            _ => new FakeNayaxRequestCredentialProvider("op-1", "fake-token-not-a-real-credential"));
        services.AddScoped<IBusinessScope>(_ => new BusinessScope());
        services.AddSingleton<IClock>(new FakeClock(new DateTime(2026, 10, 23, 0, 0, 0, DateTimeKind.Utc)));
        // The connected-machine counter of issue #978 is the outermost decorator, so the port cannot
        // be produced without its recorder. Nothing here reads the fleet, so the recorder is never
        // called.
        services.AddScoped<IConnectedMachineSnapshotRecorder>(_ => new NeverCalledSnapshotRecorder());

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// The connected-machine recorder as a composition test needs it: present, and never called,
    /// because nothing here performs a fleet read.
    /// </summary>
    private sealed class NeverCalledSnapshotRecorder : IConnectedMachineSnapshotRecorder
    {
        public Task<ConnectedMachineSnapshotOutcome> RecordFromFleetReadAsync(
            ConnectedMachineFleetRead read,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("No fleet is read in a composition test.");

        public Task<ConnectedMachineSnapshotAttempt> ReadFleetAndRecordAsync(
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("No fleet is read in a composition test.");
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
