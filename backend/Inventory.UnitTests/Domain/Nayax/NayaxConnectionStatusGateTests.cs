using Inventory.Domain.Nayax;
using Xunit;

namespace InventoryApi.Tests.Domain.Nayax;

/// <summary>
/// The status gate of issue #520, state by state. Four declared states and the undeclared ones are
/// a small enough vocabulary to pin exhaustively, which is the point: a later state added to
/// <see cref="NayaxConnectionStatus"/> has to make a deliberate decision here rather than inherit
/// "usable" by default.
/// </summary>
public class NayaxConnectionStatusGateTests
{
    [Theory]
    [InlineData(NayaxConnectionStatus.Ready)]
    [InlineData(NayaxConnectionStatus.PendingPermissions)]
    public void Ready_and_pending_permissions_may_run_ordinary_operations(NayaxConnectionStatus status)
    {
        Assert.True(NayaxConnectionStatusGate.AllowsOrdinaryOperations(status));
    }

    [Theory]
    [InlineData(NayaxConnectionStatus.NotConfigured)]
    [InlineData(NayaxConnectionStatus.NeedsAttention)]
    public void Not_configured_and_needs_attention_may_not(NayaxConnectionStatus status)
    {
        Assert.False(NayaxConnectionStatusGate.AllowsOrdinaryOperations(status));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(4)]
    [InlineData(99)]
    public void A_value_outside_the_declared_vocabulary_is_refused(int status)
    {
        Assert.False(NayaxConnectionStatusGate.AllowsOrdinaryOperations((NayaxConnectionStatus)status));
    }

    /// <summary>
    /// Fails if a state is added to the enum without a decision being made here, so the gate can
    /// never silently fall behind the vocabulary it is the gate for.
    /// </summary>
    [Fact]
    public void Every_declared_status_is_covered_by_this_test()
    {
        Assert.Equal(
            new[]
            {
                NayaxConnectionStatus.NotConfigured,
                NayaxConnectionStatus.PendingPermissions,
                NayaxConnectionStatus.Ready,
                NayaxConnectionStatus.NeedsAttention,
            },
            Enum.GetValues<NayaxConnectionStatus>());
    }
}
