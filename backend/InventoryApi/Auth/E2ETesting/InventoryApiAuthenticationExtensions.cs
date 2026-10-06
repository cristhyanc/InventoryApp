using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Identity.Web;

namespace InventoryApi.Auth.E2ETesting;

/// <summary>
/// The composition root's authentication decision (issue #46).
///
/// There is exactly one decision and it is made here, from the hosting environment, before any
/// request exists: the dedicated end-to-end host registers the synthetic
/// <see cref="E2ETestAuthenticationHandler"/> scheme and nothing else, and every other
/// environment - Production, Development, the backend suite's <c>Testing</c> environment, and any
/// future one - registers the real Microsoft Entra <c>JwtBearer</c> scheme and nothing else.
///
/// Keeping it in one method is the point. The two branches are mutually exclusive, the test
/// scheme has no registration path that does not run
/// <see cref="E2ETestEnvironment.IsEnabled"/> first, and the real scheme is what an unrecognised
/// environment falls back to, so a configuration mistake removes the test scheme rather than the
/// production one.
/// </summary>
public static class InventoryApiAuthenticationExtensions
{
    public static IServiceCollection AddInventoryApiAuthentication(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        if (E2ETestEnvironment.IsEnabled(environment))
        {
            AddE2ETestAuthentication(services);
            return services;
        }

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddMicrosoftIdentityWebApi(configuration.GetSection("AzureAd"));

        return services;
    }

    /// <summary>
    /// Private on purpose: the test scheme has no public registration method, so no other part of
    /// the application - and no future caller who has not read
    /// <see cref="E2ETestEnvironment"/> - can add it to a host.
    /// </summary>
    private static void AddE2ETestAuthentication(IServiceCollection services) =>
        services.AddAuthentication(E2ETestAuthenticationHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, E2ETestAuthenticationHandler>(
                E2ETestAuthenticationHandler.SchemeName,
                displayName: null,
                configureOptions: _ => { });
}
