using Inventory.Application.SupportSessions;

namespace InventoryApi.Tests.Application.SupportSessions;

/// <summary>
/// Answers the one support-session question (issue #551) with whatever a test decided, so the
/// Application components that branch on it - <c>CurrentCapabilities</c>,
/// <c>GetCurrentAccess</c> - can be tested without a header, a policy or a database.
///
/// <para>It counts the calls, because the real accessor is memoised per request: several components
/// ask in one request and must not each hash and look up the presented id.</para>
/// </summary>
public sealed class FakeSupportSessionAccessor : ISupportSessionAccessor
{
    private readonly SupportSessionAccess _access;

    public FakeSupportSessionAccessor(SupportSessionAccess access) => _access = access;

    /// <summary>A request that presented no session - every ordinary member's.</summary>
    public FakeSupportSessionAccessor()
        : this(SupportSessionAccess.NotRequested)
    {
    }

    public int CallCount { get; private set; }

    /// <summary>A request inside a usable session for <paramref name="businessId"/>.</summary>
    public static FakeSupportSessionAccessor Active(
        int businessId,
        string businessName = "Vending Co",
        DateTime? expiresAtUtc = null,
        int sessionKey = 1) =>
        new(SupportSessionAccess.Active(
            businessId,
            businessName,
            expiresAtUtc ?? new DateTime(2026, 10, 13, 9, 0, 0, DateTimeKind.Utc),
            sessionKey));

    /// <summary>A request that presented a session the server will not honour.</summary>
    public static FakeSupportSessionAccessor Ended() => new(SupportSessionAccess.Ended);

    public Task<SupportSessionAccess> ResolveAsync(CancellationToken cancellationToken)
    {
        CallCount++;
        return Task.FromResult(_access);
    }
}
