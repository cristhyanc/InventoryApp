using InventoryApi.Auth.ExternalIdentity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Identity.Web;

namespace InventoryApi.Auth.E2ETesting;

/// <summary>
/// The composition root's authentication decision (issue #46, extended by issue #710).
///
/// The first decision is made here, from the hosting environment, before any request exists: the
/// dedicated end-to-end host registers the synthetic
/// <see cref="E2ETestAuthenticationHandler"/> scheme and nothing else, and every other
/// environment - Production, Development, the backend suite's <c>Testing</c> environment, and any
/// future one - registers the real Microsoft Entra <c>JwtBearer</c> scheme. That host must not gain
/// a real scheme as a side effect of anything, which is why the branch returns before any other
/// registration runs.
///
/// The second decision is made from configuration, and only outside that host: when - and only
/// when - the <c>ExternalId</c> section names one complete, well-formed customer identity tenant,
/// a second named bearer scheme pinned to that tenant is registered beside the workforce one,
/// behind the policy scheme that routes each token to the scheme that validates it (issue #710,
/// docs/identity-provider-decision.md § 5.2). With that section empty - the shipped state - or
/// half-filled, the registration is the one this method always made: one workforce scheme, no
/// second issuer, and nothing for a token or a later edit to select. The only thing issue #710
/// changes about that state is that the workforce scheme's accepted audiences and its
/// delegated-access-token rule are now stated rather than inferred - see
/// <see cref="StateWorkforceTokenRules"/>.
///
/// Keeping both decisions in one method is the point. The environment branches are mutually
/// exclusive, the test scheme has no registration path that does not run
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
            services.AddSingleton(ExternalIdAuthenticationDecision.SyntheticHost);
            AddE2ETestAuthentication(services);
            return services;
        }

        var workforce = configuration.GetSection("AzureAd");
        var customerIdentity = ExternalIdAuthenticationDecision.From(configuration);
        services.AddSingleton(customerIdentity);

        if (customerIdentity.Authority is not { } authority)
        {
            services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
                .AddMicrosoftIdentityWebApi(workforce);

            StateWorkforceTokenRules(services, workforce);
            return services;
        }

        var authentication = services.AddAuthentication(IssuerSelectingPolicyScheme.Name);

        authentication.AddPolicyScheme(
            IssuerSelectingPolicyScheme.Name,
            displayName: null,
            options => options.ForwardDefaultSelector =
                context => IssuerSelectingPolicyScheme.SelectScheme(context, authority));

        authentication.AddMicrosoftIdentityWebApi(workforce);

        authentication.AddJwtBearer(
            ExternalIdBearerScheme.Name,
            options => ExternalIdBearerScheme.Configure(options, authority));

        services.AddOptions<JwtBearerOptions>(ExternalIdBearerScheme.Name)
            .PostConfigure<ILoggerFactory>(ExternalIdBearerScheme.FailClosedOnUnreadableMetadata);

        StateWorkforceTokenRules(services, workforce);
        return services;
    }

    /// <summary>
    /// States the workforce scheme's accepted audiences explicitly, instead of leaving them to be
    /// inferred from the configured client id (design § 5.1 rule 4), and requires its tokens to be
    /// delegated access tokens for this API (rule 6).
    ///
    /// The audiences are the same two the identity library derives by default - the API app
    /// registration's application id and its <c>api://</c> App ID URI - so this changes nothing
    /// about which workforce tokens are accepted. What it changes is that the second scheme's
    /// arrival cannot quietly widen them: each scheme now names its own audience, and neither can
    /// be edited into accepting the other's. The token-purpose rule can only refuse more, and only
    /// kinds of token no endpoint here was ever reachable with - see
    /// <see cref="BearerTokenValidationRules.RequireDelegatedAccessToken"/>.
    ///
    /// Everything else about this scheme is untouched, and deliberately so. It keeps
    /// <c>TenantId: common</c>, the identity library's own Entra issuer validation and therefore
    /// the issuer-to-<c>tid</c> binding that makes multi-tenant acceptance safe: a work or school
    /// account in a tenant nobody here has heard of still signs in.
    /// </summary>
    private static void StateWorkforceTokenRules(IServiceCollection services, IConfiguration workforce)
    {
        var clientId = workforce["ClientId"]?.Trim();

        if (string.IsNullOrEmpty(clientId))
        {
            return;
        }

        // Registered after the identity library's own configuration, so it is the last word on
        // these two values and the first thing a reviewer finds when asking what this API accepts.
        services.Configure<JwtBearerOptions>(
            JwtBearerDefaults.AuthenticationScheme,
            options =>
            {
                BearerTokenValidationRules.PinAudiences(
                    options, PinnedAudiences.For(clientId, workforce["Audience"]?.Trim()));
                BearerTokenValidationRules.RequireDelegatedAccessToken(options);
            });
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
