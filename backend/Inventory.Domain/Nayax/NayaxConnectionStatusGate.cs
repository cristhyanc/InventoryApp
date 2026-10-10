namespace Inventory.Domain.Nayax;

/// <summary>
/// The deterministic rule that decides whether one business's stored Nayax credentials may be used
/// for an ordinary operation (issue #520, a slice of #500).
///
/// It lives in the Domain, beside <see cref="NayaxConnectionStatus"/> itself, because it is the
/// meaning of those states rather than a policy of whichever adapter happens to ask: the Nayax HTTP
/// client, a later status display and the Owner wizard of #506 must all read the same gate, and a
/// second opinion written inline somewhere else is exactly how a fail-closed state becomes
/// accidentally usable.
///
/// <see cref="AllowsOrdinaryOperations"/> is deliberately an allow-list of the two usable states
/// rather than a deny-list of the two unusable ones. An enum is a compile-time constraint, not a
/// validation - a value read from the database or cast from an integer can be anything - so a state
/// this rule has never heard of is refused rather than treated as usable.
/// </summary>
public static class NayaxConnectionStatusGate
{
    /// <summary>
    /// Whether an ordinary Nayax operation (a sales sync, a catalogue read, a dashboard refresh)
    /// may use credentials in <paramref name="status"/>.
    ///
    /// <see cref="NayaxConnectionStatus.Ready"/> was tested and worked.
    /// <see cref="NayaxConnectionStatus.PendingPermissions"/> is allowed too, deliberately: the
    /// credentials are stored and may well work, and refusing every call until something has tested
    /// them would make a freshly connected business look broken. An individual call that Nayax
    /// refuses with a 403 in that state is a per-feature permission answer, not a credential
    /// verdict, and does not change the status.
    ///
    /// <see cref="NayaxConnectionStatus.NotConfigured"/> and
    /// <see cref="NayaxConnectionStatus.NeedsAttention"/> fail closed: nothing is stored, or what is
    /// stored is known not to work, and an operator has to act before a call can succeed.
    /// </summary>
    public static bool AllowsOrdinaryOperations(NayaxConnectionStatus status) =>
        status is NayaxConnectionStatus.Ready or NayaxConnectionStatus.PendingPermissions;
}
