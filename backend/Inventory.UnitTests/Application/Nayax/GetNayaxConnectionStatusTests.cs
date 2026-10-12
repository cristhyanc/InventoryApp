using Inventory.Application.Nayax;
using Inventory.Domain.Nayax;
using Xunit;

namespace InventoryApi.Tests.Application.Nayax;

/// <summary>
/// The connection state every member may read (issue #537).
///
/// The point of the use case is what it does <em>not</em> return. The banners of #539 are shown to a
/// Manager and an Operator, so this read is the smallest one that answers "is Nayax working here,
/// and is a re-test still needed?" - and a business with no connection at all gets the same
/// fail-closed answer as one whose business does not resolve.
/// </summary>
public class GetNayaxConnectionStatusTests
{
    private const string OperatorId = "2002736764";
    private const string Token = "fake-token-not-a-real-credential";

    [Theory]
    [InlineData(NayaxConnectionStatus.Ready, false)]
    [InlineData(NayaxConnectionStatus.Ready, true)]
    [InlineData(NayaxConnectionStatus.PendingPermissions, false)]
    [InlineData(NayaxConnectionStatus.NeedsAttention, false)]
    public async Task The_stored_status_and_the_pending_machine_checks_are_reported(
        NayaxConnectionStatus stored,
        bool machineChecksPending)
    {
        var connections = new FakeNayaxConnectionStore(
            OperatorId, Token, stored, credentialRevision: 2, machineChecksPending);

        var summary = await new GetNayaxConnectionStatus(connections).Handle();

        Assert.Equal(new NayaxConnectionStatusSummary(stored, machineChecksPending), summary);
    }

    /// <summary>
    /// No stored connection is <c>NotConfigured</c>, which is also what a caller whose business does
    /// not resolve reads: the store reports nothing without a resolved business.
    /// </summary>
    [Fact]
    public async Task A_business_with_no_connection_is_not_configured()
    {
        var summary = await new GetNayaxConnectionStatus(new FakeNayaxConnectionStore()).Handle();

        Assert.Equal(NayaxConnectionStatus.NotConfigured, summary.Status);
        Assert.False(summary.MachineChecksPending);
    }

    /// <summary>
    /// The read never decrypts the stored token: a status display has no use for it, and a read the
    /// whole business can make must not handle the business's secret at all.
    /// </summary>
    [Fact]
    public async Task The_status_read_never_decrypts_the_token()
    {
        var connections = new FakeNayaxConnectionStore(
            OperatorId, Token, NayaxConnectionStatus.Ready);

        await new GetNayaxConnectionStatus(connections).Handle();

        Assert.Equal(0, connections.CredentialReads);
    }
}
