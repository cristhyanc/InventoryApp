using Inventory.Application.Nayax;
using Inventory.Domain.Nayax;

namespace InventoryApi.Tests.Infrastructure.Nayax;

/// <summary>
/// A per-business Nayax credential provider for the HTTP client's own tests (issue #520).
///
/// It stands in for the real provider so a client test can state exactly what the current business
/// holds - including a gate refusal - without a database, and so the client's side of the contract
/// can be asserted: that it resolves a credential per call, sends that business's token, and
/// reports a 401 for the revision the call started with.
/// </summary>
internal sealed class FakeNayaxRequestCredentialProvider : INayaxRequestCredentialProvider
{
    private readonly NayaxRequestCredential? _credential;
    private readonly NayaxNotConnectedException? _refusal;

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

    private FakeNayaxRequestCredentialProvider(NayaxNotConnectedException refusal) => _refusal = refusal;

    /// <summary>A provider whose gate refuses, as it does for a business with no usable connection.</summary>
    public static FakeNayaxRequestCredentialProvider Refusing(NayaxConnectionStatus status) =>
        new(new NayaxNotConnectedException(status));

    /// <summary>How many times the client resolved a credential.</summary>
    public int Resolutions { get; private set; }

    /// <summary>The credential revisions the client reported a 401 for, in order.</summary>
    public List<int> UnauthorizedReports { get; } = new();

    public Task<NayaxRequestCredential> GetForOperationAsync(CancellationToken cancellationToken)
    {
        Resolutions++;

        if (_refusal is not null)
        {
            throw _refusal;
        }

        return Task.FromResult(_credential!);
    }

    public Task ReportUnauthorizedAsync(int credentialRevision, CancellationToken cancellationToken)
    {
        UnauthorizedReports.Add(credentialRevision);
        return Task.CompletedTask;
    }
}
