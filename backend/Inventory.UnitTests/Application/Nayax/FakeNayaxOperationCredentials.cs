using Inventory.Application.Nayax;
using Inventory.Application.SupportSessions;
using Inventory.Domain.Nayax;

namespace InventoryApi.Tests.Application.Nayax;

/// <summary>
/// The gated per-business credential port, for a use-case test (issue #843).
///
/// It stands in for <see cref="NayaxRequestCredentialProvider"/> so a use case's test can state what
/// the current business's connection answers - a usable credential at a stated revision, the status
/// gate's refusal, or a support session's - without a database or a stored token. The revision is
/// settable because the freshness key of the latest-sales synchronization is the business *and* this
/// revision: replacing the business's credentials must make the next run happen in full.
///
/// It is not the HTTP client's double (<c>FakeNayaxRequestCredentialProvider</c>, in the integration
/// project): that one is about what the client sends, this one about what a use case is told.
/// </summary>
public sealed class FakeNayaxOperationCredentials : INayaxRequestCredentialProvider
{
    private readonly Func<NayaxRequestCredential> _answer;

    public FakeNayaxOperationCredentials(int credentialRevision = 1)
    {
        CredentialRevision = credentialRevision;
        _answer = () => new NayaxRequestCredential(
            new NayaxConnectionCredential("operator-1", "token-1", CredentialRevision),
            NayaxConnectionStatus.Ready);
    }

    private FakeNayaxOperationCredentials(Func<NayaxRequestCredential> answer) => _answer = answer;

    /// <summary>
    /// The revision the next resolution answers with. Setting it is what "the Owner saved new
    /// credentials" looks like to a consumer of this port.
    /// </summary>
    public int CredentialRevision { get; set; }

    /// <summary>How many times a credential was resolved through this port.</summary>
    public int Resolutions { get; private set; }

    /// <summary>The credential revisions a 401 was reported for, in order.</summary>
    public List<int> UnauthorizedReports { get; } = [];

    /// <summary>A connection the status gate refuses, as it does for a business that is not connected.</summary>
    public static FakeNayaxOperationCredentials Refusing(
        NayaxConnectionStatus status = NayaxConnectionStatus.NotConfigured) =>
        new(() => throw new NayaxNotConnectedException(status));

    /// <summary>A request inside a support session, which resolves no Nayax credentials at all (issue #551).</summary>
    public static FakeNayaxOperationCredentials InSupportSession() =>
        new(() => throw new SupportSessionNayaxUnavailableException());

    public Task<NayaxRequestCredential> GetForOperationAsync(CancellationToken cancellationToken)
    {
        Resolutions++;

        return Task.FromResult(_answer());
    }

    public Task ReportUnauthorizedAsync(int credentialRevision, CancellationToken cancellationToken)
    {
        UnauthorizedReports.Add(credentialRevision);

        return Task.CompletedTask;
    }
}
