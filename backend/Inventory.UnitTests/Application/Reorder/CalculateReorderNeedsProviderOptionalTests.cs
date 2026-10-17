using Inventory.Application.Nayax;
using Inventory.Application.Reorder;
using Inventory.Application.SupportSessions;
using Inventory.Domain.Nayax;
using Moq;
using Xunit;

namespace InventoryApi.Tests.Application.Reorder;

/// <summary>
/// Reorder's two explicit modes (issue #726, under #329): a business with no usable sales provider
/// still gets a complete storage-only answer, and a business whose fleet really was read still gets
/// the machine-aware one.
///
/// <para>What these tests are about is the line between an <em>availability</em> answer and a
/// <em>failure</em>. Exactly two things degrade the mode - the business having no credentials an
/// ordinary operation may use, and Nayax refusing one of the two fleet reads for lack of permission -
/// and each says why. Everything else propagates, which is what keeps a half-read fleet from being
/// published as a business whose machines need nothing. The bounded fan-out, the per-product
/// summing and the upstream-failure propagation itself stay covered by
/// <c>InventoryApi.Tests.Application.Reorder.CalculateReorderNeedsTests</c>, which needs the typed
/// Infrastructure exception.</para>
/// </summary>
public class CalculateReorderNeedsProviderOptionalTests
{
    private const long OutstandingOrderProductId = 7;
    private const decimal OutstandingOrderQuantity = 12m;

    /// <summary>
    /// The release-gate case (#328): nothing is stored, so the credential provider refuses before
    /// the connection is read and no request leaves the API. The answer is a complete storage-only
    /// result, not a failure - and the outstanding supplier orders, a local fact, are still in it.
    /// </summary>
    [Fact]
    public async Task A_business_with_no_stored_connection_is_answered_in_storage_only_mode()
    {
        var result = await Handle(FleetRefusing(new NayaxNotConnectedException(NayaxConnectionStatus.NotConfigured)));

        Assert.Equal(ReorderMode.StorageOnly, result.Mode);
        Assert.Equal(MachineNeedUnavailableReason.NayaxNotConnected, result.MachineNeedUnavailableReason);
        Assert.Empty(result.MachineReplenishmentNeedByProductId);
        Assert.Empty(result.MachineIds);
        Assert.Empty(result.MachineSelections);
        Assert.Equal(OutstandingOrderQuantity, result.OnOrderQuantityByProductId[OutstandingOrderProductId]);
    }

    /// <summary>
    /// A connection the status gate refuses is the same answer, deliberately: stored credentials
    /// that are known not to work leave the business exactly as able to manage its storage as a
    /// business that never connected. One reason code covers both, because what a caller does about
    /// it - connect or reconnect Nayax - is the same.
    /// </summary>
    [Fact]
    public async Task A_connection_that_needs_attention_is_answered_in_storage_only_mode_too()
    {
        var result = await Handle(FleetRefusing(new NayaxNotConnectedException(NayaxConnectionStatus.NeedsAttention)));

        Assert.Equal(ReorderMode.StorageOnly, result.Mode);
        Assert.Equal(MachineNeedUnavailableReason.NayaxNotConnected, result.MachineNeedUnavailableReason);
    }

    /// <summary>
    /// Availability is per feature (#329 P9). The credentials are usable and Nayax refused the fleet
    /// read itself for lack of permission, so reorder is storage-only with its own reason - not
    /// "not connected", which would tell an operator to reconnect a connection that is working.
    /// </summary>
    [Fact]
    public async Task A_fleet_read_nayax_has_not_granted_is_storage_only_with_its_own_reason()
    {
        var result = await Handle(FleetRefusing(new NayaxPermissionNotGrantedException("GetMachinesAsync")));

        Assert.Equal(ReorderMode.StorageOnly, result.Mode);
        Assert.Equal(MachineNeedUnavailableReason.PermissionNotGranted, result.MachineNeedUnavailableReason);
        Assert.Empty(result.MachineReplenishmentNeedByProductId);
    }

    /// <summary>
    /// Machine-aware mode needs both reads. The fleet listed, and the machine-products read of one
    /// machine was refused: the result is storage-only, and everything the machines that did answer
    /// reported is discarded rather than summed into a need that covers part of the fleet.
    /// </summary>
    [Fact]
    public async Task A_machine_products_read_nayax_has_not_granted_makes_the_whole_result_storage_only()
    {
        var fleet = FleetOf(1, 2);
        fleet.Setup(client => client.GetMachineProductsAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new NayaxMachineProduct { NayaxProductID = 100, PAR = 10, MissingStockByMDB = 6 }]);
        fleet.Setup(client => client.GetMachineProductsAsync(2, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new NayaxPermissionNotGrantedException("GetMachineProductsAsync"));

        var result = await Handle(fleet);

        Assert.Equal(ReorderMode.StorageOnly, result.Mode);
        Assert.Equal(MachineNeedUnavailableReason.PermissionNotGranted, result.MachineNeedUnavailableReason);
        Assert.Empty(result.MachineReplenishmentNeedByProductId);
        Assert.Empty(result.MachineIds);
        Assert.Empty(result.MachineSelections);
    }

    /// <summary>
    /// The regression guard: a business whose fleet reads both succeed is unchanged. It is answered
    /// machine-aware, with the machines' need, the machines it covered and no unavailability reason
    /// to explain.
    /// </summary>
    [Fact]
    public async Task A_fleet_that_answers_is_machine_aware_with_no_reason_to_report()
    {
        var fleet = FleetOf(1);
        fleet.Setup(client => client.GetMachineProductsAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new NayaxMachineProduct { NayaxProductID = 100, PAR = 10, MissingStockByMDB = 6 }]);

        var result = await Handle(fleet);

        Assert.Equal(ReorderMode.MachineAware, result.Mode);
        Assert.Null(result.MachineNeedUnavailableReason);
        Assert.Equal(6, result.MachineReplenishmentNeedByProductId[100]);
        Assert.Equal([1L], result.MachineIds);
        Assert.Equal(OutstandingOrderQuantity, result.OnOrderQuantityByProductId[OutstandingOrderProductId]);
    }

    /// <summary>
    /// A support session resolves no Nayax credentials at all (issue #551), and that refusal is not
    /// one of this use case's two availability answers: it propagates, so the Dashboard's own
    /// support-view branch (issue #552) stays the one place that degrades a session's summary and
    /// the reorder-alert list keeps refusing rather than quietly answering storage-only.
    /// </summary>
    [Fact]
    public async Task The_support_session_refusal_propagates_rather_than_degrading_the_mode()
    {
        var fleet = FleetRefusing(new SupportSessionNayaxUnavailableException());

        await Assert.ThrowsAsync<SupportSessionNayaxUnavailableException>(() => Handle(fleet));
    }

    private static Task<ReorderNeedsResult> Handle(Mock<INayaxLynxClient> fleet)
    {
        var outstanding = new Mock<IOutstandingSupplierOrderQuantityStore>();
        outstanding.Setup(store => store.GetOutstandingQuantitiesByProductAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<long, decimal> { [OutstandingOrderProductId] = OutstandingOrderQuantity });

        return new CalculateReorderNeeds(fleet.Object, outstanding.Object).Handle(CancellationToken.None);
    }

    private static Mock<INayaxLynxClient> FleetOf(params long[] machineIds)
    {
        var fleet = new Mock<INayaxLynxClient>();
        fleet.Setup(client => client.GetMachinesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([.. machineIds.Select(id => new NayaxMachine { MachineID = id })]);
        return fleet;
    }

    private static Mock<INayaxLynxClient> FleetRefusing(Exception refusal)
    {
        var fleet = new Mock<INayaxLynxClient>();
        fleet.Setup(client => client.GetMachinesAsync(It.IsAny<CancellationToken>())).ThrowsAsync(refusal);
        return fleet;
    }
}
