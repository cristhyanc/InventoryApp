using Inventory.Application.NayaxReconciliation;
using Inventory.Application.NayaxSqs;
using Inventory.Application.SalesSync;
using Inventory.Application.Tenancy;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.NayaxReconciliation;
using Inventory.Infrastructure.Persistence;
using InventoryApi.Adapters.NayaxReconciliation;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace InventoryApi.Tests.Controllers;

/// <summary>
/// The composition-root half of issue #1104's hourly reconciliation: where its work gets its
/// business from, and that everything it needs really is registered.
///
/// <para>It is not a request, so nothing resolves an actor. The eligibility read runs in a scope that
/// has resolved <em>no</em> business - the state its reviewed <c>IgnoreQueryFilters</c> statement is
/// written for - and each business's run happens in a scope resolved to that one business, where the
/// ordinary tenant filters and <c>BusinessOwnershipEnforcer</c> are fully in force. That is the same
/// arrangement <c>NayaxSqsReaderCompositionTests</c> asserts for the queue reader (issue #1103) and
/// <c>ConnectedMachineSnapshotCommand</c> uses for the daily count (issue #978).</para>
///
/// <para>Resolving the use case here is the composition check a background worker would otherwise
/// only get in production: a missing registration would be found an hour after a deployment, in a
/// log, rather than in this file.</para>
/// </summary>
public sealed class NayaxSalesReconciliationCompositionTests
    : IClassFixture<NayaxSalesReconciliationCompositionTests.ApiFactory>
{
    /// <summary>A business the fixture database does not need to hold: nothing here reads a row.</summary>
    private const int ReconciledBusinessId = 1;

    private readonly ApiFactory _factory;

    public NayaxSalesReconciliationCompositionTests(ApiFactory factory)
    {
        _factory = factory;
    }

    /// <summary>
    /// The runner, its schedule and its log sink are one per process, because the schedule is a fact
    /// about this process rather than about one business.
    /// </summary>
    [Fact]
    public void The_runner_and_its_ports_are_one_per_process()
    {
        using var first = _factory.Services.CreateScope();
        using var second = _factory.Services.CreateScope();

        Assert.Same(
            first.ServiceProvider.GetRequiredService<NayaxSalesReconciliationRunner>(),
            second.ServiceProvider.GetRequiredService<NayaxSalesReconciliationRunner>());

        Assert.IsType<SystemNayaxSalesReconciliationSchedule>(
            _factory.Services.GetRequiredService<INayaxSalesReconciliationSchedule>());
        Assert.IsType<LoggingNayaxSalesReconciliationLog>(
            _factory.Services.GetRequiredService<INayaxSalesReconciliationLog>());
        Assert.IsType<ServiceScopeNayaxSalesReconciliationScopes>(
            _factory.Services.GetRequiredService<INayaxSalesReconciliationScopes>());
    }

    /// <summary>
    /// The synchronization lock is one per process too, and it is the <strong>same</strong> object
    /// the request-driven sync takes. That is the whole point of it: a lock that was not shared would
    /// not exclude anything, and an hourly run could then start beside a Dashboard refresh for the
    /// same business.
    /// </summary>
    [Fact]
    public void The_synchronization_lock_is_one_per_process_and_shared_with_the_request_path()
    {
        using var first = _factory.Services.CreateScope();
        using var second = _factory.Services.CreateScope();

        var shared = _factory.Services.GetRequiredService<NayaxSalesSyncLock>();

        Assert.Same(shared, first.ServiceProvider.GetRequiredService<NayaxSalesSyncLock>());
        Assert.Same(shared, second.ServiceProvider.GetRequiredService<NayaxSalesSyncLock>());
        Assert.Equal(NayaxSalesSyncLock.DefaultRequestWait, shared.RequestWait);
    }

    /// <summary>
    /// The eligibility read happens in a scope that has resolved <strong>no business</strong>, which
    /// is what makes its <c>IgnoreQueryFilters</c> statement necessary rather than a shortcut: in
    /// that state the central filter matches none of the sender rows.
    /// </summary>
    [Fact]
    public void The_scope_the_eligibility_read_runs_in_has_resolved_no_business()
    {
        using var scope = _factory.Services.CreateScope();

        var businessScope = scope.ServiceProvider.GetRequiredService<IBusinessScope>();
        Assert.Equal(BusinessScopeState.Denied, businessScope.State);
        Assert.Null(businessScope.BusinessId);

        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.True(db.TenantFilteringEnabled);
        Assert.Null(db.CurrentBusinessId);
        Assert.Empty(db.BusinessNayaxSqsSenders.ToList());

        // And the adapter that reads across businesses is satisfied by the reviewed one.
        Assert.IsType<EfNayaxSqsSenderDirectory>(
            scope.ServiceProvider.GetRequiredService<INayaxSqsSenderDirectory>());
    }

    /// <summary>
    /// A business's run scope publishes exactly one business, once, and the use case resolved in it
    /// is the real one with the real adapters behind it. The business id comes from the eligibility
    /// read's answer, and <c>BusinessScope</c> itself refuses a second resolution, so nothing later
    /// in the run can repoint it.
    /// </summary>
    [Fact]
    public async Task A_business_run_scope_resolves_one_business_once_and_composes_the_use_case()
    {
        var scopes = _factory.Services.GetRequiredService<INayaxSalesReconciliationScopes>();

        await using var work = scopes.CreateForBusiness(ReconciledBusinessId);
        Assert.NotNull(work);

        using var scope = _factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<BusinessScope>()
            .Resolve(BusinessId.From(ReconciledBusinessId));

        Assert.NotNull(scope.ServiceProvider.GetRequiredService<ReconcileNayaxSalesWithProvider>());
        Assert.IsType<EfNayaxSaleProviderEvidenceStore>(
            scope.ServiceProvider.GetRequiredService<INayaxSaleProviderEvidenceStore>());
        Assert.IsType<EfNayaxReconciliationSuccessStore>(
            scope.ServiceProvider.GetRequiredService<INayaxReconciliationSuccessStore>());

        // The scope's own context is scoped to that one business, exactly as a member's request is.
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.True(db.TenantFilteringEnabled);
        Assert.Equal(ReconciledBusinessId, db.CurrentBusinessId);
    }

    /// <summary>
    /// No registration anywhere hands the reconciliation an unscoped context: the opt-out of
    /// <c>UnscopedBusinessScope</c> stays unresolvable, exactly as
    /// <see cref="BusinessScopeCompositionTests"/> asserts for every request path.
    /// </summary>
    [Fact]
    public void No_registration_gives_the_reconciliation_unrestricted_access()
    {
        using var scope = _factory.Services.CreateScope();

        Assert.Null(scope.ServiceProvider.GetService<UnscopedBusinessScope>());
        Assert.IsNotType<UnscopedBusinessScope>(
            scope.ServiceProvider.GetRequiredService<IBusinessScope>());
    }

    /// <summary>
    /// The worker is hosting glue and nothing else: its only dependency is the Application-layer
    /// runner, so no rule, no scope and no context can live in it.
    /// </summary>
    [Fact]
    public void The_worker_depends_on_nothing_but_the_application_runner()
    {
        Assert.Equal(
            [typeof(NayaxSalesReconciliationRunner)],
            typeof(NayaxSalesReconciliationWorker)
                .GetConstructors()
                .SelectMany(constructor => constructor.GetParameters())
                .Select(parameter => parameter.ParameterType));
    }

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
