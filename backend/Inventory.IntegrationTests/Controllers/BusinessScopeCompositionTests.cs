using System.Net;
using Inventory.Application.PlatformAdmin;
using Inventory.Application.SupportSessions;
using Inventory.Application.Tenancy;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Persistence;
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
///
/// <para>Issue #544 extended it to the Super Admin's business store, the first write-capable
/// platform-admin adapter and the only request path that reads across every business through
/// <see cref="AppDbContext"/>: it is registered scoped, takes nothing but the request's own
/// context, and no registration anywhere offers a request path an unscoped one. Issue #550's
/// support-session store is held to the same three things for the same reason: it is the second
/// adapter a platform administrator's denied-scope request reaches.</para>
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
    /// The Super Admin's business store is not an exception to any of the above (issue #544). It is
    /// the first write-capable platform-admin adapter and the only request path that reads across
    /// every business through <c>AppDbContext</c>, so this is where "no unscoped context is exposed"
    /// has to be asserted rather than described.
    ///
    /// <para>Three things together make that true, and all three are checked: the port is satisfied
    /// by the Infrastructure adapter as a <em>scoped</em> registration, so it is built per request;
    /// its only constructor dependency is <see cref="AppDbContext"/>, so nothing can hand it a
    /// scope, a context factory or options of its own; and the context it therefore receives is the
    /// request's own, which the test above proves starts denied and filters every tenant-owned
    /// read.</para>
    /// </summary>
    [Fact]
    public void The_super_admin_business_store_gets_the_request_scoped_context_and_nothing_else()
    {
        using var scope = _factory.Services.CreateScope();

        var store = scope.ServiceProvider.GetRequiredService<IPlatformBusinessDirectoryStore>();

        Assert.IsType<EfPlatformBusinessDirectoryStore>(store);

        var dependencies = typeof(EfPlatformBusinessDirectoryStore)
            .GetConstructors()
            .SelectMany(constructor => constructor.GetParameters())
            .Select(parameter => parameter.ParameterType)
            .ToArray();

        Assert.Equal([typeof(AppDbContext)], dependencies);

        // The context that adapter is therefore built with: the request's own, which has resolved no
        // business and reads nothing tenant-owned.
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.True(db.TenantFilteringEnabled);
        Assert.Null(db.CurrentBusinessId);
        Assert.Empty(db.Products.ToList());
    }

    /// <summary>
    /// The support-session store is not an exception either (issue #550). It is the second adapter a
    /// platform administrator's denied-scope request reaches, and the only one that writes a row
    /// about access to another business, so the same three things are checked: the port is satisfied
    /// by the Infrastructure adapter as a <em>scoped</em> registration, its only constructor
    /// dependency is <see cref="AppDbContext"/> - so nothing can hand it a scope, a context factory
    /// or options of its own - and the context it therefore receives is the request's own, which
    /// starts denied and filters every tenant-owned read.
    /// </summary>
    [Fact]
    public void The_support_session_store_gets_the_request_scoped_context_and_nothing_else()
    {
        using var scope = _factory.Services.CreateScope();

        var store = scope.ServiceProvider.GetRequiredService<ISupportSessionStore>();

        Assert.IsType<EfSupportSessionStore>(store);

        var dependencies = typeof(EfSupportSessionStore)
            .GetConstructors()
            .SelectMany(constructor => constructor.GetParameters())
            .Select(parameter => parameter.ParameterType)
            .ToArray();

        Assert.Equal([typeof(AppDbContext)], dependencies);

        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.True(db.TenantFilteringEnabled);
        Assert.Null(db.CurrentBusinessId);
        Assert.Empty(db.Products.ToList());
    }

    /// <summary>
    /// The deactivation hook the composition root registers is the support-session end (issue #550),
    /// not the no-op issue #545 shipped. It is asserted here because the registration is the whole
    /// mechanism: there is exactly one implementation, the lifecycle transition calls whatever it is
    /// given inside its own transaction, and a deactivation that silently went back to doing nothing
    /// would leave a supporter reading a business that had just been switched off.
    /// </summary>
    [Fact]
    public void The_business_deactivation_hook_ends_support_sessions()
    {
        using var scope = _factory.Services.CreateScope();

        Assert.IsType<EndSupportSessionsOnBusinessDeactivation>(
            scope.ServiceProvider.GetRequiredService<IBusinessDeactivationHook>());
    }

    /// <summary>
    /// The other half of the same rule: the composition root registers no way for <em>any</em>
    /// request-path service to obtain unrestricted access. <see cref="UnscopedBusinessScope"/> is
    /// not resolvable at all, and the scope that is resolvable is the request-backed one - so the
    /// platform-admin adapter above cannot be given an unscoped context by asking for one either
    /// (issue #544).
    /// </summary>
    [Fact]
    public void No_registration_exposes_unrestricted_access_to_a_request_path()
    {
        using var scope = _factory.Services.CreateScope();

        Assert.Null(scope.ServiceProvider.GetService<UnscopedBusinessScope>());
        Assert.IsNotType<UnscopedBusinessScope>(scope.ServiceProvider.GetRequiredService<IBusinessScope>());
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
