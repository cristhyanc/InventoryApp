using System.Globalization;
using Inventory.Domain.Nayax;

namespace Inventory.Application.Nayax;

/// <summary>
/// The credentials one ordinary Nayax call is made with, together with the connection status they
/// were read at (issue #520).
///
/// It wraps <see cref="NayaxConnectionCredential"/> rather than copying its members so the token
/// keeps living in the one type that is built to hold it: that type is a class with a redacting
/// <see cref="object.ToString"/> for a reason, and a second type that re-declared the token could
/// quietly reintroduce the log line it exists to prevent. <see cref="ToString"/> here renders the
/// wrapped credential, so it redacts for the same reason.
///
/// <see cref="Status"/> is the status of the very snapshot the token was read from
/// (<see cref="INayaxConnectionStore.FindForOperationAsync"/>), and it is carried because the
/// answer to a Nayax refusal depends on it: in
/// <see cref="NayaxConnectionStatus.PendingPermissions"/> a 403 is a per-feature permission answer
/// rather than a credential verdict. <see cref="CredentialRevision"/> is carried for the matching
/// reason on the 401 path - a status write must name the revision the call actually used, never
/// whatever is stored by the time the answer arrives.
/// </summary>
public sealed class NayaxRequestCredential
{
    /// <param name="credential">The operator id and decrypted token this call authenticates with.</param>
    /// <param name="status">The connection status of the snapshot that credential came from.</param>
    public NayaxRequestCredential(NayaxConnectionCredential credential, NayaxConnectionStatus status)
    {
        ArgumentNullException.ThrowIfNull(credential);

        Credential = credential;
        Status = status;
    }

    /// <summary>The operator id and decrypted access token. Carries a secret; never serialise it.</summary>
    public NayaxConnectionCredential Credential { get; }

    /// <summary>The connection status read in the same snapshot as the credential.</summary>
    public NayaxConnectionStatus Status { get; }

    /// <summary>The Nayax operator id, the Lynx API's <c>OperatorID</c> path parameter.</summary>
    public string OperatorId => Credential.OperatorId;

    /// <summary>The decrypted bearer token. A secret: never log it and never put it in a DTO.</summary>
    public string AccessToken => Credential.AccessToken;

    /// <summary>The credential revision these values were read at.</summary>
    public int CredentialRevision => Credential.CredentialRevision;

    /// <summary>
    /// Whether the stored credentials have not been tested since they were saved, which is what
    /// makes a Nayax 403 a per-feature "permission not granted" answer rather than an ordinary
    /// upstream refusal.
    /// </summary>
    public bool PermissionsAreUnverified => Status == NayaxConnectionStatus.PendingPermissions;

    /// <summary>Redacts the token, by rendering the wrapped credential's redacting form.</summary>
    public override string ToString() =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"NayaxRequestCredential {{ Status = {Status}, Credential = {Credential} }}");
}
