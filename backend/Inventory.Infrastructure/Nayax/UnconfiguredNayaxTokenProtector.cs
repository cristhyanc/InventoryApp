namespace Inventory.Infrastructure.Nayax;

/// <summary>
/// The protector an environment with no Nayax encryption key configured gets (issue #518).
///
/// Both members throw. That is the fail-closed half of a deliberate split: provisioning the key is
/// a human step that has not happened in any environment yet, and nothing reads a per-business
/// connection until issue #520, so a missing key must not stop the API from starting - but it must
/// never let a token be stored in a form that cannot be protected, or a stored one be read as
/// absent. The message names the setting to configure.
/// </summary>
public sealed class UnconfiguredNayaxTokenProtector : INayaxTokenProtector
{
    /// <inheritdoc />
    public ProtectedNayaxToken Protect(string accessToken) => throw NotConfigured();

    /// <inheritdoc />
    public string Unprotect(ProtectedNayaxToken protectedToken) => throw NotConfigured();

    private static NayaxTokenProtectionException NotConfigured() =>
        new("No Nayax token encryption key is configured, so a per-business Nayax access token "
            + "cannot be stored or read. Configure "
            + $"{NayaxTokenProtectionOptions.SectionName}:"
            + $"{nameof(NayaxTokenProtectionOptions.ActiveKeyId)} and the matching "
            + $"{NayaxTokenProtectionOptions.SectionName}:"
            + $"{nameof(NayaxTokenProtectionOptions.Keys)} entry for this environment.");
}
