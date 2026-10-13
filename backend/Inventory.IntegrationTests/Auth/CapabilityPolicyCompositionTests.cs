using Inventory.Application.Access;
using InventoryApi.Auth;
using InventoryApi.Auth.PlatformAdmin;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Inventory.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace InventoryApi.Tests.Auth;

/// <summary>
/// What the composition root registers for issue #525: one authorization policy per declared
/// capability, and nothing that lets the two named-policy families decide each other's questions.
///
/// <para>The "one per capability" half matters for a reason that is easy to miss. A capability with
/// no registered policy is not an open endpoint - the authorization middleware throws for a policy
/// name it cannot resolve - but it is a startup-time failure waiting for the first request to a
/// newly marked endpoint. Registering every declared capability, including the ones no endpoint
/// uses yet, means issues #526 and #527 add an attribute and nothing else.</para>
/// </summary>
public sealed class CapabilityPolicyCompositionTests : IClassFixture<CapabilityPolicyCompositionTests.HostFactory>
{
    private readonly HostFactory _factory;

    public CapabilityPolicyCompositionTests(HostFactory factory) => _factory = factory;

    private IAuthorizationPolicyProvider Policies =>
        _factory.Services.GetRequiredService<IAuthorizationPolicyProvider>();

    [Fact]
    public async Task One_policy_is_registered_for_every_declared_capability()
    {
        foreach (var capability in Capabilities.All)
        {
            var policy = await Policies.GetPolicyAsync(CapabilityPolicy.Name(capability));

            Assert.NotNull(policy);
            var requirement = Assert.Single(policy.Requirements.OfType<BusinessCapabilityRequirement>());
            Assert.Equal(capability, requirement.Capability);
        }
    }

    /// <summary>
    /// A capability policy denies an anonymous caller in its own right, so it cannot be satisfied by
    /// a request that was never authenticated even if it were ever applied to an endpoint without
    /// <c>[Authorize]</c>.
    /// </summary>
    [Fact]
    public async Task Every_capability_policy_denies_an_anonymous_caller()
    {
        foreach (var capability in Capabilities.All)
        {
            var policy = await Policies.GetPolicyAsync(CapabilityPolicy.Name(capability));

            Assert.Contains(
                policy!.Requirements,
                requirement => requirement.GetType().Name == "DenyAnonymousAuthorizationRequirement");
        }
    }

    /// <summary>
    /// The two named-policy families are separate decisions. <c>PlatformAdminPolicy</c> grants no
    /// business capability: it holds no capability requirement, so being the configured platform
    /// administrator satisfies nothing a business endpoint asks for. The converse holds too - a
    /// capability policy holds no platform-admin requirement - so a business role can never become
    /// a second way into the cross-business diagnostics surface.
    /// </summary>
    [Fact]
    public async Task The_platform_admin_policy_grants_no_business_capability()
    {
        var platformAdmin = await Policies.GetPolicyAsync(PlatformAdminPolicy.Name);

        Assert.NotNull(platformAdmin);
        Assert.Empty(platformAdmin.Requirements.OfType<BusinessCapabilityRequirement>());

        foreach (var capability in Capabilities.All)
        {
            var policy = await Policies.GetPolicyAsync(CapabilityPolicy.Name(capability));

            Assert.Empty(policy!.Requirements.OfType<PlatformAdminRequirement>());
        }
    }

    /// <summary>
    /// The default policy is still exactly what it was: every controller relies on <c>[Authorize]</c>
    /// with no policy name, and adding fifteen named policies must not have changed what that means.
    /// </summary>
    [Fact]
    public async Task The_default_authorization_policy_is_unchanged()
    {
        var defaultPolicy = await Policies.GetDefaultPolicyAsync();

        Assert.Empty(defaultPolicy.Requirements.OfType<BusinessCapabilityRequirement>());
        Assert.Empty(defaultPolicy.Requirements.OfType<PlatformAdminRequirement>());
    }

    /// <summary>
    /// The handler that answers the capability requirement is registered per request, like the
    /// current-business provider whose memoised resolution it reads. A singleton would hold one
    /// request's provider for the life of the process.
    /// </summary>
    [Fact]
    public void The_capability_handler_is_registered_once_and_per_request()
    {
        var registration = Assert.Single(_factory.Descriptors.Where(descriptor =>
            descriptor.ServiceType == typeof(IAuthorizationHandler)
            && descriptor.ImplementationType == typeof(BusinessCapabilityAuthorizationHandler)));

        Assert.Equal(ServiceLifetime.Scoped, registration.Lifetime);
    }

    public sealed class HostFactory : WebApplicationFactory<Program>
    {
        private readonly SqliteConnection _connection = new("Data Source=:memory:");

        /// <summary>
        /// The service descriptors the composition root produced, so a registration's lifetime can
        /// be asserted rather than inferred from behaviour.
        /// </summary>
        public IReadOnlyList<ServiceDescriptor> Descriptors { get; private set; } = [];

        public HostFactory() => _connection.Open();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");

            builder.ConfigureServices(services =>
            {
                var dbContextOptions = services.SingleOrDefault(d => d.ServiceType == typeof(DbContextOptions<AppDbContext>));
                if (dbContextOptions is not null)
                {
                    services.Remove(dbContextOptions);
                }

                services.AddDbContext<AppDbContext>(options => options.UseSqlite(_connection));

                Descriptors = services.ToList();
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
