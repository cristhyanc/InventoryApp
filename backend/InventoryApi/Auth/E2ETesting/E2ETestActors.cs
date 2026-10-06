namespace InventoryApi.Auth.E2ETesting;

/// <summary>
/// One synthetic end-to-end test identity: the <c>(tid, oid)</c> claim pair the real
/// <see cref="EntraActorIdentityAccessor"/> reads, plus the key the test suite names it by.
///
/// The identifiers are fixed, obviously synthetic GUIDs rather than generated ones, so a run is
/// reproducible and so no real Entra directory tenant or object id ever appears in the
/// repository.
/// </summary>
/// <param name="Key">The value the E2E suite sends to select this actor.</param>
/// <param name="DirectoryTenantId">The synthetic Entra directory tenant id (<c>tid</c>).</param>
/// <param name="ObjectId">The synthetic Entra object id (<c>oid</c>).</param>
public sealed record E2ETestActor(string Key, string DirectoryTenantId, string ObjectId);

/// <summary>
/// The closed set of synthetic actors the dedicated E2E host can authenticate (issue #46).
///
/// The set is a compile-time constant, not configuration and not request input: the actor header
/// can only ever name one of these three keys, and an unrecognised value authenticates nobody.
/// Selecting <em>which</em> known synthetic actor a request speaks as is the same thing a bearer
/// token does in every other environment; it is not what enables the scheme, which is decided
/// solely by <see cref="E2ETestEnvironment"/>.
///
/// <see cref="NoMembership"/> is deliberately never given a business membership by
/// <see cref="E2ETestFixture"/>. It exists so the suite can prove that authenticating is not
/// authorising: that actor reaches the business-scope middleware and is refused with 403, exactly
/// as a real signed-in account with no membership would be.
/// </summary>
public static class E2ETestActors
{
    /// <summary>
    /// The request header naming the synthetic actor. It is read only by
    /// <see cref="E2ETestAuthenticationHandler"/>, which is only registered in the dedicated E2E
    /// environment, so in every other environment this header is an unknown header with no
    /// meaning whatsoever.
    /// </summary>
    public const string ActorHeaderName = "X-E2E-Test-Actor";

    /// <summary>
    /// The API scope the real Entra access token carries, which every controller requires through
    /// <c>[RequiredScope("access_as_user")]</c>. The synthetic principal carries it so the normal
    /// scope check runs instead of being bypassed.
    /// </summary>
    public const string ApiScope = "access_as_user";

    /// <summary>
    /// One synthetic directory for every actor: two businesses owned by two actors from the same
    /// Entra directory is the harder isolation case, because the directory tenant alone must not
    /// decide which business's data a caller reads.
    /// </summary>
    private const string SyntheticDirectoryTenantId = "e2e70000-0000-0000-0000-00000000d1e0";

    public static readonly E2ETestActor BusinessAOwner =
        new("business-a-owner", SyntheticDirectoryTenantId, "e2e7aaaa-0000-0000-0000-0000000000a1");

    public static readonly E2ETestActor BusinessBOwner =
        new("business-b-owner", SyntheticDirectoryTenantId, "e2e7bbbb-0000-0000-0000-0000000000b1");

    public static readonly E2ETestActor NoMembership =
        new("no-membership", SyntheticDirectoryTenantId, "e2e7cccc-0000-0000-0000-0000000000c1");

    public static IReadOnlyList<E2ETestActor> All { get; } = [BusinessAOwner, BusinessBOwner, NoMembership];

    /// <summary>
    /// Resolves a header value to a known synthetic actor, or <c>null</c> when it names none.
    /// The comparison is ordinal: an actor key is an exact identifier, not a search term.
    /// </summary>
    public static E2ETestActor? Find(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        var trimmed = key.Trim();

        return All.FirstOrDefault(actor => string.Equals(actor.Key, trimmed, StringComparison.Ordinal));
    }
}
