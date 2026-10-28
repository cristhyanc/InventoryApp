namespace InventoryApi.Auth.PriceCollector;

/// <summary>
/// The Entra <em>application</em> role the price collector must hold (issue #924, parent #817).
///
/// <para>It is declared here, in one place, because three things have to agree about it: the bearer
/// scheme's token rule, the authorization policy's requirement, and the human who adds the app role
/// to the API registration in the Entra admin center (see docs/deployment.md § Price collector). A
/// second spelling anywhere would be a collector that authenticates and then cannot do
/// anything - or, far worse, one that is let in by a rule reading a role nobody granted.</para>
///
/// <para><strong>An application role is not a delegated scope.</strong> It arrives in <c>roles</c>,
/// not in <c>scp</c>, and it says what an <em>application</em> may do with no user involved at all -
/// which is the whole reason the collector needs no password and no person. The collector token must
/// carry no <c>scp</c> whatsoever: a token with one is a delegated user token, and those are refused
/// on this surface however they are otherwise shaped.</para>
/// </summary>
public static class PriceCollectorRole
{
    /// <summary>
    /// The app role's value, exactly as it is defined on the API app registration and exactly as it
    /// appears in an accepted token's <c>roles</c> claim. Compared ordinally and case-sensitively,
    /// because Entra issues the value as it was declared and a case-insensitive comparison would be a
    /// second, looser rule nobody asked for.
    /// </summary>
    public const string Name = "PriceCollector.Run";

    /// <summary>The <c>roles</c> claim, in the short and the WS-* mapped spelling.</summary>
    internal static readonly string[] RoleClaimTypes =
    [
        "roles",
        "http://schemas.microsoft.com/ws/2008/06/identity/claims/role",
    ];

    /// <summary>
    /// The claims that name the application a token was issued to, in the two spellings Entra uses:
    /// <c>azp</c> on a v2 token and <c>appid</c> on a v1 one. Which one a token carries is decided by
    /// the API registration's accepted access-token version, which is a portal setting a human owns -
    /// so both are read here, and both must name the configured collector when present.
    /// </summary>
    internal static readonly string[] ClientApplicationClaimTypes = ["azp", "appid"];
}
