namespace InventoryApi.Auth.PriceCollector;

/// <summary>What the composition root decided about the price collector, for one host.</summary>
public enum PriceCollectorAuthenticationState
{
    /// <summary>
    /// No collector is configured. This is the shipped state and the rollback state: the collector
    /// scheme refuses every token, the collector policy denies everybody, and the work endpoint
    /// answers <c>401</c>.
    /// </summary>
    NotConfigured,

    /// <summary>
    /// The section was started but is not usable. The collector is <em>not</em> trusted - a
    /// configuration mistake must never mean "accept whatever arrives" - and it is recorded as a
    /// startup error, because the deployment that filled the section in intended something that is
    /// not happening.
    /// </summary>
    Incomplete,

    /// <summary>One configured collector, pinned to one tenant and one client application.</summary>
    Registered,

    /// <summary>
    /// The dedicated end-to-end host, which registers the synthetic scheme and nothing else. No real
    /// scheme exists there for a collector to be added to, whatever is configured.
    /// </summary>
    SyntheticHost,
}

/// <summary>
/// The one configured price collector, resolved once at startup from configuration alone (issue
/// #924, parent #817).
///
/// <para>It exists for the reason <see cref="ExternalIdentity.ExternalIdAuthenticationDecision"/>
/// exists: the decision is a value the host can state, log and assert, rather than something
/// inferred from which schemes happen to be registered. Three things read it - the composition root,
/// which pins the collector bearer scheme only for
/// <see cref="PriceCollectorAuthenticationState.Registered"/>; startup, which logs one line of
/// evidence about whether this deployment trusts a collector at all; and the authorization handler,
/// which denies every caller while no collector is configured.</para>
///
/// <para><strong>Every rule is fail-closed.</strong> A setting that is absent, blank, or not the kind
/// of value the setting means leaves this API with no collector rather than with a loosely
/// configured one, and the identifiers are compared as a whole pair: a token from the right tenant
/// issued to the wrong application is refused, and so is the reverse.</para>
/// </summary>
public sealed class PriceCollectorAuthenticationDecision
{
    private PriceCollectorAuthenticationDecision(
        PriceCollectorAuthenticationState state,
        Guid? tenantId,
        Guid? clientId,
        string? problem)
    {
        State = state;
        TenantId = tenantId;
        ClientId = clientId;
        Problem = problem;
    }

    public PriceCollectorAuthenticationState State { get; }

    /// <summary>The tenant every accepted collector token must claim in <c>tid</c>, while one is configured.</summary>
    public Guid? TenantId { get; }

    /// <summary>
    /// The client application every accepted collector token must have been issued to, in
    /// <c>azp</c> or <c>appid</c>, while one is configured.
    /// </summary>
    public Guid? ClientId { get; }

    /// <summary>
    /// Why an attempted configuration is unusable, naming the setting keys and never their values.
    /// <see langword="null"/> in every other state.
    /// </summary>
    public string? Problem { get; }

    /// <summary>
    /// Whether a collector is trusted at all. Everything about the collector surface asks this first:
    /// with no collector configured the scheme validates nothing and the policy grants nothing.
    /// </summary>
    public bool IsRegistered =>
        State == PriceCollectorAuthenticationState.Registered
        && TenantId is not null
        && ClientId is not null;

    public static PriceCollectorAuthenticationDecision SyntheticHost { get; } =
        new(PriceCollectorAuthenticationState.SyntheticHost, tenantId: null, clientId: null, problem: null);

    /// <summary>
    /// Resolves the decision from the <c>PriceCollector</c> section.
    ///
    /// <para>Both identifiers absent or blank is "not configured". Anything else must be complete and
    /// well formed, or it is <see cref="PriceCollectorAuthenticationState.Incomplete"/> -
    /// <c>RunExpiryHours</c> deliberately does not count either way, because a run lifetime says
    /// nothing about whether a collector is trusted.</para>
    /// </summary>
    public static PriceCollectorAuthenticationDecision From(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return From(configuration.GetSection(PriceCollectorOptions.SectionName).Get<PriceCollectorOptions>());
    }

    internal static PriceCollectorAuthenticationDecision From(PriceCollectorOptions? options)
    {
        var tenantId = options?.TenantId?.Trim();
        var clientId = options?.ClientId?.Trim();

        if (string.IsNullOrWhiteSpace(tenantId) && string.IsNullOrWhiteSpace(clientId))
        {
            return new PriceCollectorAuthenticationDecision(
                PriceCollectorAuthenticationState.NotConfigured,
                tenantId: null,
                clientId: null,
                problem: null);
        }

        if (!Guid.TryParse(tenantId, out var tenant) || tenant == Guid.Empty)
        {
            return Incomplete(
                $"{PriceCollectorOptions.SectionName}:{nameof(PriceCollectorOptions.TenantId)} must be the collector tenant's GUID");
        }

        if (!Guid.TryParse(clientId, out var client) || client == Guid.Empty)
        {
            return Incomplete(
                $"{PriceCollectorOptions.SectionName}:{nameof(PriceCollectorOptions.ClientId)} must be the GUID application id of the collector's app registration");
        }

        return new PriceCollectorAuthenticationDecision(
            PriceCollectorAuthenticationState.Registered, tenant, client, problem: null);
    }

    /// <summary>
    /// One line of startup evidence about whether this deployment trusts a collector. It carries
    /// identifiers only: there is no secret in this configuration, and no token, claim or request
    /// value reaches it.
    /// </summary>
    public void LogTo(ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);

        switch (State)
        {
            case PriceCollectorAuthenticationState.Registered:
                logger.LogInformation(
                    "The price collector surface is enabled: app-only tokens from tenant {PriceCollectorTenantId} issued to application {PriceCollectorClientId} and carrying the {PriceCollectorRole} role may call the collector endpoints, and nothing else may.",
                    TenantId,
                    ClientId,
                    PriceCollectorRole.Name);
                break;

            case PriceCollectorAuthenticationState.Incomplete:
                logger.LogError(
                    "The price collector surface is NOT enabled because its configuration is unusable: {PriceCollectorConfigurationProblem}. Every collector request is rejected and no other endpoint is affected.",
                    Problem);
                break;

            case PriceCollectorAuthenticationState.NotConfigured:
                logger.LogInformation(
                    "No price collector is configured; every collector request is rejected and the API accepts delegated user tokens only.");
                break;

            case PriceCollectorAuthenticationState.SyntheticHost:
            default:
                break;
        }
    }

    private static PriceCollectorAuthenticationDecision Incomplete(string problem) =>
        new(PriceCollectorAuthenticationState.Incomplete, tenantId: null, clientId: null, problem);
}
