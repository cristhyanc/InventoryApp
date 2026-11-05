using Inventory.Application.Nayax;
using Inventory.Application.NayaxSqs;
using Moq;
using Xunit;

namespace InventoryApi.Tests.Application.NayaxSqs;

/// <summary>
/// Issue #1102: turning the machine slot an SQS message reports into Nayax's own product id, through
/// the machine's product map. An SQS message carries no product id, so this is the only thing that
/// links a queued sale to a product by anything other than its name.
/// </summary>
public class NayaxSqsSlotProductResolverTests
{
    private const long MachineId = 54265;

    private static Mock<INayaxLynxClient> NayaxClient(params NayaxMachineProduct[] machineProducts)
    {
        var nayax = new Mock<INayaxLynxClient>(MockBehavior.Strict);
        nayax.Setup(client => client.GetMachineProductsAsync(MachineId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([.. machineProducts]);
        return nayax;
    }

    private static NayaxMachineProduct Slot(int? mdbCode, long? nayaxProductId, string name = "Bounty") =>
        new() { MDBCode = mdbCode, NayaxProductID = nayaxProductId, MachineID = MachineId, ProductName = name };

    [Fact]
    public async Task A_slot_the_machines_product_map_holds_resolves_to_its_product_id()
    {
        var nayax = NayaxClient(Slot(0, 8811), Slot(1, 8812));

        var resolved = await new NayaxSqsSlotProductResolver(nayax.Object).ResolveAsync(MachineId, 0);

        Assert.Equal(8811L, resolved);
    }

    /// <summary>
    /// Slot <c>0</c> is a real slot: the published example message vends from it. It must not be
    /// confused with "no slot", which is why the resolver takes a nullable rather than a sentinel.
    /// </summary>
    [Fact]
    public async Task Slot_zero_is_a_slot_and_not_an_absent_one()
    {
        var nayax = NayaxClient(Slot(0, 8811));

        Assert.Equal(8811L, await new NayaxSqsSlotProductResolver(nayax.Object).ResolveAsync(MachineId, 0));
    }

    [Fact]
    public async Task A_slot_the_product_map_does_not_hold_resolves_to_nothing()
    {
        var nayax = NayaxClient(Slot(0, 8811), Slot(1, 8812));

        Assert.Null(await new NayaxSqsSlotProductResolver(nayax.Object).ResolveAsync(MachineId, 7));
    }

    /// <summary>
    /// A machine with no product map at all - never configured, or configured in Nayax after this
    /// read's 60 seconds began. The sale is still parsed and saved; only the product id is unknown.
    /// </summary>
    [Fact]
    public async Task A_machine_with_no_product_map_resolves_to_nothing()
    {
        var nayax = NayaxClient();

        Assert.Null(await new NayaxSqsSlotProductResolver(nayax.Object).ResolveAsync(MachineId, 0));
    }

    /// <summary>
    /// A message with no readable slot resolves to nothing, and asks Nayax nothing: there is no
    /// lookup to make, so the strict mock would fail if a call were attempted.
    /// </summary>
    [Fact]
    public async Task A_missing_slot_resolves_to_nothing_without_reading_the_product_map()
    {
        var nayax = new Mock<INayaxLynxClient>(MockBehavior.Strict);

        Assert.Null(await new NayaxSqsSlotProductResolver(nayax.Object).ResolveAsync(MachineId, productSlot: null));

        nayax.Verify(
            client => client.GetMachineProductsAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>A map entry that holds the slot but no product id resolves to nothing.</summary>
    [Fact]
    public async Task A_product_map_entry_with_no_product_id_resolves_to_nothing()
    {
        var nayax = NayaxClient(Slot(0, nayaxProductId: null));

        Assert.Null(await new NayaxSqsSlotProductResolver(nayax.Object).ResolveAsync(MachineId, 0));
    }

    /// <summary>
    /// Two different products on one slot is an answer this code cannot read out of the map, so it
    /// answers none rather than picking whichever row Nayax happened to return first - the same
    /// fail-closed shape as the Domain's unambiguous-name matching rule.
    /// </summary>
    [Fact]
    public async Task Two_products_claiming_one_slot_resolve_to_nothing()
    {
        var nayax = NayaxClient(Slot(0, 8811), Slot(0, 8899, "Mars"));

        Assert.Null(await new NayaxSqsSlotProductResolver(nayax.Object).ResolveAsync(MachineId, 0));
    }

    /// <summary>
    /// Two entries naming the same product on one slot are not ambiguous: there is still one answer.
    /// </summary>
    [Fact]
    public async Task Two_entries_naming_the_same_product_still_resolve()
    {
        var nayax = NayaxClient(Slot(0, 8811), Slot(0, 8811));

        Assert.Equal(8811L, await new NayaxSqsSlotProductResolver(nayax.Object).ResolveAsync(MachineId, 0));
    }

    /// <summary>
    /// The lookup goes through the 60-second machine-products cache (issue #842) rather than the
    /// fresh read, because a queue draining a machine's messages must not make one Nayax call per
    /// message. The strict mock only allows the cached read, so a fresh read would fail this test.
    /// </summary>
    [Fact]
    public async Task The_lookup_uses_the_cached_machine_products_read()
    {
        var nayax = NayaxClient(Slot(0, 8811));
        var resolver = new NayaxSqsSlotProductResolver(nayax.Object);

        await resolver.ResolveAsync(MachineId, 0);
        await resolver.ResolveAsync(MachineId, 0);

        nayax.Verify(
            client => client.GetMachineProductsAsync(MachineId, It.IsAny<CancellationToken>()),
            Times.Exactly(2));
        nayax.Verify(
            client => client.GetMachineProductsFreshAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
