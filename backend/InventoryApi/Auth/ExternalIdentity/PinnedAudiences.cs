namespace InventoryApi.Auth.ExternalIdentity;

/// <summary>
/// The audience values one API app registration's access tokens may carry (issue #710, design
/// § 5.1 rule 4).
///
/// Both schemes pin their audiences through this helper rather than relying on whatever the
/// identity library derives from a client id by default, and each pins only its <em>own</em>
/// registration's audience. The two sets are never merged into one pool: merging them would let a
/// token one tenant issued for its own API be accepted as the other tenant's, which is precisely
/// the substitution the two separate schemes exist to prevent.
///
/// Two spellings, because an Entra app registration's accepted access-token version decides which
/// one its tokens carry - the bare application id for a v2 token and the <c>api://</c> App ID URI
/// for a v1 one. Which version each registration issues is a portal setting a human owns; pinning
/// both here keeps that a decision about the registration rather than about this code.
/// </summary>
internal static class PinnedAudiences
{
    /// <summary>
    /// The two spellings of one registration's audience, plus an explicitly configured
    /// <c>Audience</c> when a section carries one - which is the same set
    /// <c>Microsoft.Identity.Web</c>'s own audience validation accepts, so stating it here changes
    /// which tokens are accepted not at all.
    /// </summary>
    public static IReadOnlyList<string> For(string clientId, string? configuredAudience = null) =>
        string.IsNullOrEmpty(configuredAudience)
            || string.Equals(configuredAudience, clientId, StringComparison.Ordinal)
            ? [clientId, $"api://{clientId}"]
            : [clientId, $"api://{clientId}", configuredAudience];
}
