using Inventory.Application.Nayax;
using Inventory.Infrastructure.Nayax;
using InventoryApi.Tests.Application.Nayax;
using InventoryApi.Tests.Application.Time;
using Moq;
using Xunit;

namespace InventoryApi.Tests.Infrastructure.Nayax;

/// <summary>
/// The machine-stock observation adapter's translation (issues #56 and #733): it carries <c>PAR</c>
/// and <c>MissingStockByMDB</c> across unchanged, and it resolves the slot's <c>NayaxProductID</c>
/// into the local product id the machine-stock ledger keys its refills, sales, counts and names by.
///
/// A slot it cannot resolve is dropped, exactly as an unidentified slot already was, and never
/// offered as the local product that happens to be numbered like the remote one - a shrinkage
/// reported against the wrong product is worse than a slot that is not reported at all.
///
/// <para>It is also a declared fresh caller (issue #842): an observation is what the machine says it
/// is holding at this instant, so the client here answers the fresh read only and throws on the
/// cached one - an adapter that started reading through the 60-second cache would move a reported
/// shrinkage, and this is what makes that fail loudly.</para>
/// </summary>
public class NayaxMachineStockObservationSourceTests
{
    private const long MachineId = 42;
    private static readonly DateTime ObservedAt = new(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc);

    private static NayaxMachineStockObservationSource Source(
        INayaxProductLinkStore productLinks, params NayaxMachineProduct[] slots)
    {
        var nayax = new Mock<INayaxLynxClient>();
        nayax.Setup(x => x.GetMachineProductsFreshAsync(MachineId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([.. slots]);
        nayax.Setup(x => x.GetMachineProductsAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new NotSupportedException(
                "The machine-stock observation source must read the machine's slots fresh."));

        return new NayaxMachineStockObservationSource(nayax.Object, productLinks, new FakeClock(ObservedAt));
    }

    [Fact]
    public async Task An_observed_slot_carries_the_local_product_its_nayax_link_names()
    {
        var source = Source(
            FakeNayaxProductLinkStore.Linking((100, 4100)),
            new NayaxMachineProduct { NayaxProductID = 100, PAR = 10, MissingStockByMDB = 4 });

        var observation = await source.GetObservationAsync(MachineId, CancellationToken.None);

        var slot = Assert.Single(observation.Slots);
        Assert.Equal(4100, slot.ProductId);
        Assert.Equal(10, slot.Par);
        Assert.Equal(4, slot.MissingStockByMdb);
        Assert.Equal(ObservedAt, observation.ObservedAtUtc);
    }

    [Fact]
    public async Task An_unlinked_slot_is_dropped_rather_than_reported_against_the_product_whose_id_equals_it()
    {
        var source = Source(
            FakeNayaxProductLinkStore.Linking((999, 4999)),
            new NayaxMachineProduct { NayaxProductID = 100, PAR = 10, MissingStockByMDB = 4 });

        var observation = await source.GetObservationAsync(MachineId, CancellationToken.None);

        Assert.Empty(observation.Slots);
    }

    /// <summary>
    /// A slot with no Nayax product id was never identifiable, so nothing is asked about it: it is
    /// dropped before the lookup, as it always was.
    /// </summary>
    [Fact]
    public async Task A_slot_with_no_nayax_product_id_is_dropped_and_nothing_is_looked_up_for_it()
    {
        var links = FakeNayaxProductLinkStore.Linking((100, 4100));
        var source = Source(links, new NayaxMachineProduct { NayaxProductID = null, PAR = 10 });

        var observation = await source.GetObservationAsync(MachineId, CancellationToken.None);

        Assert.Empty(observation.Slots);
        Assert.Empty(Assert.Single(links.Requests));
    }
}
