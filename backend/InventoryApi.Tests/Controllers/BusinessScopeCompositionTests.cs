using System.Net;
using Inventory.Application.Tenancy;
using InventoryApi.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace InventoryApi.Tests.Controllers;

/// <summary>
/// Pins down the composition root's half of tenant scoping, and the invariant underneath it
/// (issue #64):
///
/// <list type="bullet">
///   <item>an ordinary context is denied or scoped;</item>
///   <item>unrestricted, all-business access happens only when a caller explicitly asks for
///   <see cref="UnscopedBusinessScope.Instance"/>.</item>
/// </list>
///
/// Both halves matter. The default has to fail closed so that forgetting to think about tenancy
/// cannot produce an unfiltered context, and the application has to resolve a request-backed
/// scope so a dependency-injection mistake cannot silently unscope production while every other
/// test still passes.
/// </summary>
public sealed class BusinessScopeCompositionTests : IClassFixture<BusinessScopeCompositionTests.ApiFactory>
{
    private readonly ApiFactory _factory;

    public BusinessScopeCompositionTests(ApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public void The_application_resolves_a_request_backed_business_scope_not_the_unscoped_one()
    {
        using var scope = _factory.Services.CreateScope();

        var businessScope = scope.ServiceProvider.GetRequiredService<IBusinessScope>();

        Assert.IsType<BusinessScope>(businessScope);
        Assert.IsNotType<UnscopedBusinessScope>(businessScope);
    }

    /// <summary>
    /// A freshly created request scope has resolved no business yet, so persistence must be in
    /// its fail-closed state rather than reading everything.
    /// </summary>
    [Fact]
    public void A_request_scoped_DbContext_starts_denied_and_filters_tenant_owned_reads()
    {
        using var scope = _factory.Services.CreateScope();

        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        Assert.True(db.TenantFilteringEnabled);
        Assert.Null(db.CurrentBusinessId);
        Assert.Empty(db.Products.ToList());
    }

    /// <summary>
    /// The scope the middleware resolves must be the very same instance the DbContext filters
    /// by; two instances in one request would mean the endpoint and the database disagreed.
    /// </summary>
    [Fact]
    public void The_middleware_and_the_DbContext_share_one_scope_instance_per_request()
    {
        using var scope = _factory.Services.CreateScope();

        var writable = scope.ServiceProvider.GetRequiredService<BusinessScope>();
        var readable = scope.ServiceProvider.GetRequiredService<IBusinessScope>();

        Assert.Same(writable, readable);
    }

    /// <summary>
    /// The security invariant at the constructor itself: building a context without saying
    /// anything about tenancy must fail closed, not open. This is the regression guard for the
    /// earlier design, where the plain constructor meant "see everything".
    /// </summary>
    [Fact]
    public void A_context_built_without_a_scope_is_denied_rather_than_unrestricted()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;

        using var db = new AppDbContext(options);

        Assert.True(db.TenantFilteringEnabled);
        Assert.Null(db.CurrentBusinessId);
    }

    /// <summary>
    /// The deliberate opt-out still works, and is the only way to get all-business access.
    /// </summary>
    [Fact]
    public void Unrestricted_access_requires_explicitly_passing_the_unscoped_scope()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;

        using var unrestricted = new AppDbContext(options, UnscopedBusinessScope.Instance);

        Assert.False(unrestricted.TenantFilteringEnabled);
    }

    /// <summary>
    /// Business scoping must not have weakened the authentication boundary from issue #38: an
    /// anonymous request is still refused with 401 by authentication, not turned into a 403 by
    /// the business-scope middleware.
    /// </summary>
    [Fact]
    public async Task An_unauthenticated_request_is_still_401_not_403()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/products");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    public sealed class ApiFactory : WebApplicationFactory<Program>
    {
        private readonly SqliteConnection _connection = new("Data Source=:memory:");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            _connection.Open();

            builder.ConfigureServices(services =>
            {
                var dbContextOptions = services.SingleOrDefault(d => d.ServiceType == typeof(DbContextOptions<AppDbContext>));
                if (dbContextOptions is not null)
                {
                    services.Remove(dbContextOptions);
                }

                services.AddDbContext<AppDbContext>(options =>
                {
                    options.UseLazyLoadingProxies();
                    options.UseSqlite(_connection);
                });
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
