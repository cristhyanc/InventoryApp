namespace InventoryApi.Auth.ExternalIdentity;

/// <summary>What the composition root decided about customer identity, for one host.</summary>
public enum ExternalIdAuthenticationState
{
    /// <summary>
    /// No customer identity tenant is configured. This is the shipped state and the rollback
    /// state: one workforce bearer scheme, no second issuer, and nothing observable added.
    /// </summary>
    NotConfigured,

    /// <summary>
    /// The section was started but is not usable. The second scheme is not registered - a
    /// configuration mistake must never mean "accept whatever arrives" - and workforce sign-in is
    /// unaffected. It is recorded as a startup error because the deployment that filled the
    /// section in intended something that is not happening.
    /// </summary>
    Incomplete,

    /// <summary>One configured tenant, one pinned scheme, registered beside the workforce one.</summary>
    Registered,

    /// <summary>
    /// The dedicated end-to-end host, which registers the synthetic scheme and nothing else. No
    /// real scheme exists there to add a second issuer to, whatever is configured.
    /// </summary>
    SyntheticHost,
}

/// <summary>
/// The customer identity decision, resolved once at startup from configuration alone (issue #710).
///
/// It exists so the decision is a value the host can state, log and assert, rather than something
/// inferred from which schemes happen to be registered. Three things read it: the composition root,
/// which registers the second scheme only for <see cref="ExternalIdAuthenticationState.Registered"/>;
/// startup, which logs one line of evidence about what this deployment is actually trusting; and
/// the tests, which pin the fail-closed cases.
/// </summary>
public sealed class ExternalIdAuthenticationDecision
{
    private ExternalIdAuthenticationDecision(
        ExternalIdAuthenticationState state,
        ExternalIdAuthority? authority,
        string? problem)
    {
        State = state;
        Authority = authority;
        Problem = problem;
    }

    public ExternalIdAuthenticationState State { get; }

    /// <summary>The configured tenant, or <c>null</c> unless <see cref="IsRegistered"/>.</summary>
    public ExternalIdAuthority? Authority { get; }

    /// <summary>
    /// Why an attempted configuration is unusable, naming the setting keys and never their values.
    /// <c>null</c> in every other state.
    /// </summary>
    public string? Problem { get; }

    public bool IsRegistered => State == ExternalIdAuthenticationState.Registered;

    public static ExternalIdAuthenticationDecision SyntheticHost { get; } =
        new(ExternalIdAuthenticationState.SyntheticHost, authority: null, problem: null);

    /// <summary>
    /// Resolves the decision from the <c>ExternalId</c> section.
    ///
    /// All three identifiers absent or blank is "not configured" - and <c>Scopes</c> deliberately
    /// does not count, because source control carries it filled in so the configured surface stays
    /// discoverable. Anything else must be complete and well formed, or it is
    /// <see cref="ExternalIdAuthenticationState.Incomplete"/>.
    /// </summary>
    public static ExternalIdAuthenticationDecision From(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return From(configuration.GetSection(ExternalIdOptions.SectionName).Get<ExternalIdOptions>());
    }

    internal static ExternalIdAuthenticationDecision From(ExternalIdOptions? options)
    {
        if (options is null
            || (string.IsNullOrWhiteSpace(options.Instance)
                && string.IsNullOrWhiteSpace(options.TenantId)
                && string.IsNullOrWhiteSpace(options.ClientId)))
        {
            return new ExternalIdAuthenticationDecision(
                ExternalIdAuthenticationState.NotConfigured, authority: null, problem: null);
        }

        return ExternalIdAuthority.TryCreate(options, out var authority, out var problem)
            ? new ExternalIdAuthenticationDecision(ExternalIdAuthenticationState.Registered, authority, problem: null)
            : new ExternalIdAuthenticationDecision(ExternalIdAuthenticationState.Incomplete, authority: null, problem);
    }

    /// <summary>
    /// One line of startup evidence about which issuers this deployment trusts. It carries
    /// identifiers only: there is no secret in this configuration, and no token, claim or request
    /// value reaches it.
    /// </summary>
    public void LogTo(ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);

        switch (State)
        {
            case ExternalIdAuthenticationState.Registered:
                logger.LogInformation(
                    "Customer identity sign-in is enabled: bearer tokens from the External ID tenant {ExternalIdTenantId} are validated against {ExternalIdAuthority} beside workforce Microsoft sign-in.",
                    Authority!.TenantId,
                    Authority.MetadataAuthority);
                break;

            case ExternalIdAuthenticationState.Incomplete:
                logger.LogError(
                    "Customer identity sign-in is NOT enabled because its configuration is unusable: {ExternalIdConfigurationProblem}. Workforce Microsoft sign-in is unaffected and every External ID token is rejected.",
                    Problem);
                break;

            case ExternalIdAuthenticationState.NotConfigured:
                logger.LogInformation(
                    "Customer identity sign-in is not configured; workforce Microsoft sign-in is the only trusted issuer.");
                break;

            case ExternalIdAuthenticationState.SyntheticHost:
            default:
                break;
        }
    }
}
