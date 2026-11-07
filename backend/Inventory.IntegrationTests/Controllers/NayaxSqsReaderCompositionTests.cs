using System.Net;
using Inventory.Application.NayaxSqs;
using Inventory.Application.Tenancy;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.NayaxSqs;
using Inventory.Infrastructure.Persistence;
using InventoryApi.Adapters.NayaxSqs;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace InventoryApi.Tests.Controllers;

/// <summary>
/// Issue #1103's required startup case, plus the composition-root half of the Nayax SQS sales reader.
///
/// <para><strong>The API starts with no <c>NayaxSqs</c> settings and the reader stays off.</strong>
/// That is the shipped state, every test host's state and the hosted testing environment's state, and
/// it has to be asserted rather than assumed: a background service that threw on the way up, or one
/// that started polling a queue nobody configured, would both be found in production rather than
/// here. This factory configures no queue at all, so everything below is that state.</para>
///
/// <para>The other half is where the reader's work gets its business from. It is not a request, so
/// nothing resolves an actor: the sender lookup runs in a scope that has resolved <em>no</em>
/// business - the state its one reviewed <c>IgnoreQueryFilters</c> statement is written for - and
/// everything a message then causes runs in a scope resolved to the one business that lookup named,
/// where the ordinary filters and <c>BusinessOwnershipEnforcer</c> are fully in force.</para>
/// </summary>
public sealed class NayaxSqsReaderCompositionTests
    : IClassFixture<NayaxSqsReaderCompositionTests.ApiFactory>
{
    private readonly ApiFactory _factory;

    public NayaxSqsReaderCompositionTests(ApiFactory factory)
    {
        _factory = factory;
    }

    /// <summary>
    /// The API starts and answers, with no queue configured. Making a request is what proves the host
    /// came up rather than merely that a service could be resolved.
    /// </summary>
    [Fact]
    public async Task The_api_starts_and_answers_with_no_nayax_sqs_settings()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/products");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// With no queue URL the reader is registered and switched off, and the queue client it holds
    /// refuses every operation rather than answering "no messages" - which would make an unconfigured
    /// deployment indistinguishable from a working one with a quiet queue.
    /// </summary>
    [Fact]
    public void With_no_queue_configured_the_reader_is_off_and_holds_no_queue_client()
    {
        var settings = _factory.Services.GetRequiredService<INayaxSqsReaderSettings>();

        Assert.False(settings.IsConfigured);
        Assert.False(settings.HasDeadLetterQueue);
        Assert.IsType<UnconfiguredNayaxSqsQueueClient>(
            _factory.Services.GetRequiredService<INayaxSqsQueueClient>());
    }

    /// <summary>
    /// And it has polled nothing: the health record is empty, which after a start with no queue is the
    /// true statement rather than a reassuring one.
    /// </summary>
    [Fact]
    public void A_reader_that_never_started_has_an_empty_health_record()
    {
        var health = _factory.Services.GetRequiredService<NayaxSqsReaderHealth>().Current();

        Assert.Null(health.LastSuccessfulPollUtc);
        Assert.Equal(0, health.ConsecutivePollFailures);
        Assert.Null(health.LastMessageProcessedUtc);
        Assert.Null(health.DeadLetterQueueDepth);
    }

    /// <summary>
    /// The health record is a singleton, because there is one reader in one process and its state is a
    /// fact about that process (docs/architecture.md § SQLite operating assumptions - a single
    /// instance). Two instances would be two different answers to "are sales arriving?".
    /// </summary>
    [Fact]
    public void The_health_record_is_one_per_process()
    {
        using var first = _factory.Services.CreateScope();
        using var second = _factory.Services.CreateScope();

        Assert.Same(
            first.ServiceProvider.GetRequiredService<NayaxSqsReaderHealth>(),
            second.ServiceProvider.GetRequiredService<NayaxSqsReaderHealth>());
    }

    /// <summary>
    /// The reader is started by a hosted service, and it is the second of the three this application
    /// runs - beside the member display-details drain of issue #524 and the hourly Nayax sales
    /// reconciliation of issue #1104. Asserted as a set, because "what background work does this
    /// application run?" has to be answerable from the composition root and a fourth one appearing is
    /// a decision rather than a detail.
    /// </summary>
    [Fact]
    public void The_application_runs_exactly_three_hosted_background_services()
    {
        var hosted = _factory.Services.GetServices<IHostedService>()
            .Select(service => service.GetType().Name)
            .Where(name => name.EndsWith("Worker", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            [
                "MemberProfileRefreshWorker",
                "NayaxSalesReconciliationWorker",
                "NayaxSqsSalesReaderWorker",
            ],
            hosted);
    }

    /// <summary>
    /// The worker is hosting glue and nothing else: its only dependency is the Application-layer
    /// reader, so no rule, no queue client and no context can live in it.
    /// </summary>
    [Fact]
    public void The_worker_depends_on_nothing_but_the_application_reader()
    {
        Assert.Equal(
            [typeof(NayaxSqsSalesReader)],
            typeof(NayaxSqsSalesReaderWorker)
                .GetConstructors()
                .SelectMany(constructor => constructor.GetParameters())
                .Select(parameter => parameter.ParameterType));
    }

    /// <summary>
    /// The sender lookup happens in a scope that has resolved <strong>no business</strong>, which is
    /// what makes its one <c>IgnoreQueryFilters</c> statement necessary rather than a shortcut: in
    /// that state the central filter matches none of the sender rows.
    /// </summary>
    [Fact]
    public void The_scope_the_sender_lookup_runs_in_has_resolved_no_business()
    {
        using var scope = _factory.Services.CreateScope();

        var businessScope = scope.ServiceProvider.GetRequiredService<IBusinessScope>();
        Assert.Equal(BusinessScopeState.Denied, businessScope.State);
        Assert.Null(businessScope.BusinessId);

        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.True(db.TenantFilteringEnabled);
        Assert.Null(db.CurrentBusinessId);
        Assert.Empty(db.Products.ToList());

        // And the adapter that reads across businesses is satisfied by the reviewed one, built with
        // nothing but that scope's own context.
        Assert.IsType<EfNayaxSqsSenderDirectory>(
            scope.ServiceProvider.GetRequiredService<INayaxSqsSenderDirectory>());
    }

    /// <summary>
    /// The work scope publishes exactly one business, once, and the use case resolved in it is the
    /// real one. The business id comes from the sender directory's answer: this is the only place a
    /// business becomes current for a queued message, and <c>BusinessScope</c> itself refuses a second
    /// resolution, so nothing later in the message can repoint it.
    /// </summary>
    [Fact]
    public async Task The_work_scope_resolves_one_business_once_and_composes_the_use_case()
    {
        var scopes = _factory.Services.GetRequiredService<INayaxSqsReaderScopes>();
        Assert.IsType<ServiceScopeNayaxSqsReaderScopes>(scopes);

        // A scope per message, and the one thing it exposes is processing that message.
        await using var work = scopes.CreateForBusiness(NayaxSqsBusinessId);

        Assert.NotNull(work);

        // Everything the per-message use case needs really is registered: resolving it is the
        // composition check a reader that only ran in production would otherwise be.
        using var scope = _factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<BusinessScope>().Resolve(
            BusinessId.From(NayaxSqsBusinessId));

        Assert.NotNull(scope.ServiceProvider.GetRequiredService<ProcessNayaxSqsSale>());
        Assert.IsType<EfNayaxSqsSenderActivityStore>(
            scope.ServiceProvider.GetRequiredService<INayaxSqsSenderActivityStore>());
    }

    /// <summary>
    /// No registration anywhere hands the reader an unscoped context: the opt-out of
    /// <c>UnscopedBusinessScope</c> stays unresolvable, exactly as
    /// <see cref="BusinessScopeCompositionTests"/> asserts for every request path.
    /// </summary>
    [Fact]
    public void No_registration_gives_the_reader_unrestricted_access()
    {
        using var scope = _factory.Services.CreateScope();

        Assert.Null(scope.ServiceProvider.GetService<UnscopedBusinessScope>());
        Assert.IsNotType<UnscopedBusinessScope>(
            scope.ServiceProvider.GetRequiredService<IBusinessScope>());
    }

    /// <summary>A business the fixture database does not need to hold: nothing here reads a row.</summary>
    private const int NayaxSqsBusinessId = 1;

    public sealed class ApiFactory : WebApplicationFactory<Program>
    {
        private readonly SqliteConnection _connection = new("Data Source=:memory:");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            _connection.Open();

            builder.ConfigureServices(services =>
            {
                var dbContextOptions = services.SingleOrDefault(
                    descriptor => descriptor.ServiceType == typeof(DbContextOptions<AppDbContext>));
                if (dbContextOptions is not null)
                {
                    services.Remove(dbContextOptions);
                }

                services.AddDbContext<AppDbContext>(options => options.UseSqlite(_connection));
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
            {
                _connection.Dispose();
            }
        }
    }
}
