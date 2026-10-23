using Inventory.Application.Nayax;
using Inventory.Application.SupportSessions;
using Inventory.Domain.Nayax;

namespace InventoryApi.Tests.Infrastructure.Nayax;

/// <summary>
/// A per-business Nayax credential provider for the HTTP client's own tests (issue #520).
///
/// It stands in for the real provider so a client test can state exactly what the current business
/// holds - including a gate refusal - without a database, and so the client's side of the contract
/// can be asserted: that it resolves a credential per call, sends that business's token, and
/// reports a 401 for the revision the call started with.
///
/// <para><see cref="Refusal"/> is settable so a test can let a call succeed and then make the very
/// next one refuse, which is how the caches of issues #840 and #842 are proved to check the
/// connection before every hit rather than only before the read that filled the entry.</para>
/// </summary>
internal sealed class FakeNayaxRequestCredentialProvider : INayaxRequestCredentialProvider
{
    private readonly NayaxRequestCredential? _credential;
    private int _resolutions;

    public FakeNayaxRequestCredentialProvider(
        string operatorId,
        string accessToken,
        NayaxConnectionStatus status = NayaxConnectionStatus.Ready,
        int credentialRevision = 1)
    {
        _credential = new NayaxRequestCredential(
            new NayaxConnectionCredential(operatorId, accessToken, credentialRevision),
            status);
    }

    private FakeNayaxRequestCredentialProvider(Exception refusal) => Refusal = refusal;

    /// <summary>A provider whose gate refuses, as it does for a business with no usable connection.</summary>
    public static FakeNayaxRequestCredentialProvider Refusing(NayaxConnectionStatus status) =>
        new(new NayaxNotConnectedException(status));

    /// <summary>
    /// A provider that refuses because the request is running in a support session (issue #551): the
    /// refusal happens before the connection is read and before any token could be decrypted.
    /// </summary>
    public static FakeNayaxRequestCredentialProvider RefusingSupportSession() =>
        new(new SupportSessionNayaxUnavailableException());

    /// <summary>
    /// What every resolution from now on throws, or <c>null</c> to answer with the credential.
    /// </summary>
    public Exception? Refusal { get; set; }

    /// <summary>How many times the client resolved a credential.</summary>
    public int Resolutions => Volatile.Read(ref _resolutions);

    /// <summary>The credential revisions the client reported a 401 for, in order.</summary>
    public List<int> UnauthorizedReports { get; } = new();

    public Task<NayaxRequestCredential> GetForOperationAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _resolutions);

        if (Refusal is { } refusal)
        {
            throw refusal;
        }

        return Task.FromResult(_credential!);
    }

    public Task ReportUnauthorizedAsync(int credentialRevision, CancellationToken cancellationToken)
    {
        UnauthorizedReports.Add(credentialRevision);
        return Task.CompletedTask;
    }
}
