using Inventory.Application.Imports;
using Inventory.Application.Tenancy;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Imports;
using Inventory.Infrastructure.Persistence;
using InventoryApi.Adapters.ReimbursementImport;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace InventoryApi.Tests.Controllers;

/// <summary>
/// The composition-root half of issue #1149's reimbursement report import: where its work gets its
/// business from, and that everything it needs really is registered.
///
/// <para>It is not a request, so nothing resolves an actor. The discovery read runs in a scope that
/// has resolved <em>no</em> business - the state its reviewed <c>IgnoreQueryFilters</c> statement is
/// written for - and each job happens in a scope resolved to that job's own business, where the
/// ordinary tenant filters and <c>BusinessOwnershipEnforcer</c> are fully in force. It is
/// <see cref="NayaxSalesReconciliationCompositionTests"/>'s arrangement exactly.</para>
///
/// <para>Resolving the use case here is the composition check a background worker would otherwise
/// only get in production: a missing registration would be found after a deployment, in a log, rather
/// than in this file.</para>
/// </summary>
public sealed class ReimbursementImportCompositionTests
    : IClassFixture<ReimbursementImportCompositionTests.ApiFactory>
{
    /// <summary>A business the fixture database does not need to hold: nothing here reads a row.</summary>
    private const int ImportingBusinessId = 1;

    private readonly ApiFactory _factory;

    public ReimbursementImportCompositionTests(ApiFactory factory)
    {
        _factory = factory;
    }

    /// <summary>
    /// The runner, its schedule, its log sink and its scopes are one per process, because the
    /// schedule is a fact about this process rather than about one business.
    /// </summary>
    [Fact]
    public void The_runner_and_its_ports_are_one_per_process()
    {
        using var first = _factory.Services.CreateScope();
        using var second = _factory.Services.CreateScope();

        Assert.Same(
            first.ServiceProvider.GetRequiredService<ReimbursementImportJobRunner>(),
            second.ServiceProvider.GetRequiredService<ReimbursementImportJobRunner>());

        Assert.IsType<SystemReimbursementImportJobSchedule>(
            _factory.Services.GetRequiredService<IReimbursementImportJobSchedule>());
        Assert.IsType<LoggingReimbursementImportJobLog>(
            _factory.Services.GetRequiredService<IReimbursementImportJobLog>());
        Assert.IsType<ServiceScopeReimbursementImportJobScopes>(
            _factory.Services.GetRequiredService<IReimbursementImportJobScopes>());
    }

    /// <summary>
    /// The discovery read happens in a scope that has resolved <strong>no business</strong>, which is
    /// what makes its <c>IgnoreQueryFilters</c> statement necessary rather than a shortcut: in that
    /// state the central filter matches none of the job rows.
    /// </summary>
    [Fact]
    public void The_scope_the_discovery_read_runs_in_has_resolved_no_business()
    {
        using var scope = _factory.Services.CreateScope();

        var businessScope = scope.ServiceProvider.GetRequiredService<IBusinessScope>();
        Assert.Equal(BusinessScopeState.Denied, businessScope.State);
        Assert.Null(businessScope.BusinessId);

        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.True(db.TenantFilteringEnabled);
        Assert.Null(db.CurrentBusinessId);
        Assert.Empty(db.ReimbursementImportJobs.ToList());

        // And the adapter that reads across businesses is satisfied by the reviewed one.
        Assert.IsType<EfReimbursementImportJobQueue>(
            scope.ServiceProvider.GetRequiredService<IReimbursementImportJobQueue>());
    }

    /// <summary>
    /// A job's work scope publishes exactly one business, once, and the use case resolved in it is
    /// the real one with the real adapters behind it. The business id comes from the discovery read's
    /// answer, and <c>BusinessScope</c> itself refuses a second resolution, so nothing later in the
    /// job can repoint it.
    /// </summary>
    [Fact]
    public async Task A_job_scope_resolves_one_business_once_and_composes_the_use_case()
    {
        var scopes = _factory.Services.GetRequiredService<IReimbursementImportJobScopes>();

        await using var work = scopes.CreateForBusiness(ImportingBusinessId);
        Assert.NotNull(work);

        using var scope = _factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<BusinessScope>()
            .Resolve(BusinessId.From(ImportingBusinessId));

        Assert.NotNull(scope.ServiceProvider.GetRequiredService<ProcessReimbursementImportJob>());
        Assert.IsType<EfReimbursementImportJobStore>(
            scope.ServiceProvider.GetRequiredService<IReimbursementImportJobStore>());
        Assert.IsType<EfReimbursementImportFileTransactions>(
            scope.ServiceProvider.GetRequiredService<IReimbursementImportFileTransactions>());
        Assert.IsType<ZipReimbursementArchiveReader>(
            scope.ServiceProvider.GetRequiredService<IReimbursementArchiveReader>());

        // The scope's own context is scoped to that one business, exactly as a member's request is.
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.True(db.TenantFilteringEnabled);
        Assert.Equal(ImportingBusinessId, db.CurrentBusinessId);
    }

    /// <summary>
    /// The upload and the two status reads are composed for a request too, so the endpoint half of
    /// the feature cannot be missing a registration either.
    /// </summary>
    [Fact]
    public void The_request_half_of_the_feature_is_composed()
    {
        using var scope = _factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<BusinessScope>()
            .Resolve(BusinessId.From(ImportingBusinessId));

        Assert.NotNull(scope.ServiceProvider.GetRequiredService<UploadReimbursementReportArchive>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<ListReimbursementImportJobs>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<GetReimbursementImportJob>());

        // And the per-file steps the import itself is made of.
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<ImportReimbursementXmlFile>());
    }

    /// <summary>
    /// No registration anywhere hands this import an unscoped context: the opt-out of
    /// <c>UnscopedBusinessScope</c> stays unresolvable, exactly as
    /// <see cref="BusinessScopeCompositionTests"/> asserts for every request path.
    /// </summary>
    [Fact]
    public void No_registration_gives_the_import_unrestricted_access()
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
    public void The_worker_depends_on_nothing_but_the_application_runner() =>
        Assert.Equal(
            [typeof(ReimbursementImportJobRunner)],
            typeof(ReimbursementImportJobWorker)
                .GetConstructors()
                .SelectMany(constructor => constructor.GetParameters())
                .Select(parameter => parameter.ParameterType));

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
