using Inventory.Application.Nayax;
using Inventory.Application.NayaxSqs;
using Inventory.Domain.FinancialConfiguration;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Persistence;
using InventoryApi.Tests.Application.Nayax;
using InventoryApi.Tests.Application.Time;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace InventoryApi.Tests.Application.NayaxSqs;

/// <summary>
/// Issue #1103's required tenant-isolation cases, over relational SQLite with two businesses.
///
/// <para><strong>The shape that makes these worth asserting.</strong> Both businesses own a machine
/// with the <em>same</em> Nayax machine number, and both have a product recording the <em>same</em>
/// Nayax product id - at deliberately different unit costs, so which business a sale landed in is
/// visible in the figures rather than only in a key. The one thing that differs is which sender the
/// message came from, which is the whole of how a shared queue decides whose sale it is: a message
/// attributed to one business must reach the other's rows in neither direction, however much its
/// contents look like the other's.</para>
/// </summary>
public class NayaxSqsTenantIsolationTests
{
    private const string SenderA = "AIDAFICTIONALSENDERAA";
    private const string SenderB = "AIDAFICTIONALSENDERBB";

    /// <summary>
    /// The same message, delivered from business A's sender, is saved in business A alone - and the
    /// figures prove it: A's product is costed at its own $2.00 and B's row, inventory and cost are
    /// untouched.
    /// </summary>
    [Fact]
    public async Task A_message_from_one_businesss_sender_is_saved_in_that_business_alone()
    {
        await using var fixture = await NayaxSqsSaleFixture.InMemoryAsync();
        await fixture.RecordSenderAsync(NayaxSqsSaleFixture.BusinessA, SenderA);
        await fixture.RecordSenderAsync(NayaxSqsSaleFixture.BusinessB, SenderB);

        var outcome = await ProcessForBusinessAsync(fixture, NayaxSqsSaleFixture.BusinessA);

        Assert.Equal(NayaxSqsDisposition.Done, outcome.Disposition);

        await using var inA = fixture.Context(NayaxSqsSaleFixture.BusinessA);
        var sale = await inA.NayaxSales.AsNoTracking().SingleAsync();
        Assert.Equal(NayaxSqsSaleFixture.ProductA, sale.ProductId);
        Assert.Equal(2m, sale.CostOfGoodsSold);
        Assert.Equal(9, (await inA.Products.AsNoTracking()
            .SingleAsync(row => row.Id == NayaxSqsSaleFixture.ProductA)).CostingQuantity);

        await using var inB = fixture.Context(NayaxSqsSaleFixture.BusinessB);
        Assert.Empty(await inB.NayaxSales.AsNoTracking().ToListAsync());
        Assert.Empty(await inB.PendingInventoryCostRebuilds.AsNoTracking().ToListAsync());

        var productB = await inB.Products.AsNoTracking()
            .SingleAsync(row => row.Id == NayaxSqsSaleFixture.ProductB);
        Assert.Equal(10, productB.CostingQuantity);
        Assert.Equal(90m, productB.InventoryValue);
        Assert.Equal(9m, productB.AverageUnitCost);
    }

    /// <summary>
    /// And the mirror image: the very same message from business B's sender - the same transaction id,
    /// the same machine number and the same Nayax product id - is saved in business B, costed at B's
    /// own $9.00, with business A untouched. Nothing about the message decides whose sale it is.
    /// </summary>
    [Fact]
    public async Task The_same_message_from_the_other_businesss_sender_is_saved_in_the_other_business()
    {
        await using var fixture = await NayaxSqsSaleFixture.InMemoryAsync();
        await fixture.RecordSenderAsync(NayaxSqsSaleFixture.BusinessA, SenderA);
        await fixture.RecordSenderAsync(NayaxSqsSaleFixture.BusinessB, SenderB);

        var outcome = await ProcessForBusinessAsync(fixture, NayaxSqsSaleFixture.BusinessB);

        Assert.Equal(NayaxSqsDisposition.Done, outcome.Disposition);

        await using var inB = fixture.Context(NayaxSqsSaleFixture.BusinessB);
        var sale = await inB.NayaxSales.AsNoTracking().SingleAsync();
        Assert.Equal(NayaxSqsSaleFixture.ProductB, sale.ProductId);
        Assert.Equal(9m, sale.CostOfGoodsSold);
        Assert.Equal(9, (await inB.Products.AsNoTracking()
            .SingleAsync(row => row.Id == NayaxSqsSaleFixture.ProductB)).CostingQuantity);

        await using var inA = fixture.Context(NayaxSqsSaleFixture.BusinessA);
        Assert.Empty(await inA.NayaxSales.AsNoTracking().ToListAsync());
        Assert.Equal(10, (await inA.Products.AsNoTracking()
            .SingleAsync(row => row.Id == NayaxSqsSaleFixture.ProductA)).CostingQuantity);
    }

    /// <summary>
    /// Both businesses can hold a sale for the same remote transaction id, because the deduplication
    /// is per business - two operator accounts may legitimately report the same id (AGENTS.md
    /// § Tenant ownership and data isolation). Each one's own figures move and the other's do not.
    /// </summary>
    [Fact]
    public async Task Two_businesses_may_each_hold_a_sale_for_the_same_remote_transaction_id()
    {
        await using var fixture = await NayaxSqsSaleFixture.InMemoryAsync();
        await fixture.RecordSenderAsync(NayaxSqsSaleFixture.BusinessA, SenderA);
        await fixture.RecordSenderAsync(NayaxSqsSaleFixture.BusinessB, SenderB);

        await ProcessForBusinessAsync(fixture, NayaxSqsSaleFixture.BusinessA);
        await ProcessForBusinessAsync(fixture, NayaxSqsSaleFixture.BusinessB);

        await using var every = fixture.Unrestricted();
        var sales = await every.NayaxSales.AsNoTracking()
            .OrderBy(row => row.BusinessId)
            .ToListAsync();

        Assert.Equal(2, sales.Count);
        Assert.Equal(
            [NayaxSqsSaleFixture.BusinessA, NayaxSqsSaleFixture.BusinessB],
            sales.Select(row => row.BusinessId));
        Assert.All(sales, row => Assert.Equal(NayaxSqsSaleFixture.TransactionId, row.TransactionID));
        Assert.Equal(
            [NayaxSqsSaleFixture.ProductA, NayaxSqsSaleFixture.ProductB],
            sales.Select(row => row.ProductId));
    }

    /// <summary>
    /// The pending replay is tenant-owned too: a failed replay for one business's product is invisible
    /// to the other's drain, so one business's unreplayable history cannot hold another's messages.
    /// </summary>
    [Fact]
    public async Task One_businesss_pending_replay_is_invisible_to_the_other_businesss_drain()
    {
        await using var fixture = await NayaxSqsSaleFixture.InMemoryAsync();

        await using (var inA = fixture.Context(NayaxSqsSaleFixture.BusinessA))
        {
            inA.PendingInventoryCostRebuilds.Add(
                new Inventory.Infrastructure.Models.PendingInventoryCostRebuild
                {
                    ProductId = NayaxSqsSaleFixture.ProductA,
                    FromInstantUtc = NayaxSqsSaleFixture.SaleInstant,
                    CreatedAtUtc = NayaxSqsSaleFixture.ReceivedAtUtc,
                });
            await inA.SaveChangesAsync();
        }

        await using (var inB = fixture.Context(NayaxSqsSaleFixture.BusinessB))
        {
            var drained = await NayaxSqsSaleFixture.Store(inB)
                .RebuildInventoryCostsAsync(CancellationToken.None);

            Assert.Empty(drained.StillPendingProductIds);
            Assert.Empty(drained.Failures);
        }

        // A's row is still there, untouched by B's drain.
        await using var read = fixture.Context(NayaxSqsSaleFixture.BusinessA);
        var pending = await read.PendingInventoryCostRebuilds.AsNoTracking().SingleAsync();
        Assert.Equal(0, pending.AttemptCount);
    }

    /// <summary>
    /// The row the reader stamps is the resolved business's own: the last-message instant lands on
    /// that business's sender row and on no other. There is no business id anywhere on the path for
    /// it to be the wrong one - the central query filter is what finds the row.
    /// </summary>
    [Fact]
    public async Task The_last_message_instant_is_stamped_on_the_resolved_businesss_own_sender_row()
    {
        await using var fixture = await NayaxSqsSaleFixture.InMemoryAsync();
        await fixture.RecordSenderAsync(NayaxSqsSaleFixture.BusinessA, SenderA);
        await fixture.RecordSenderAsync(NayaxSqsSaleFixture.BusinessB, SenderB);

        await ProcessForBusinessAsync(fixture, NayaxSqsSaleFixture.BusinessA);

        await using var read = fixture.Unrestricted();
        var senders = await read.BusinessNayaxSqsSenders.AsNoTracking()
            .OrderBy(row => row.BusinessId)
            .ToListAsync();

        Assert.Equal(NayaxSqsSaleFixture.ReceivedAtUtc, senders[0].LastMessageReceivedAtUtc);
        Assert.Null(senders[1].LastMessageReceivedAtUtc);

        // And nothing else about either row moved: the sender id, the flag and the revision are the
        // Super Admin's alone (issue #1101).
        Assert.All(senders, sender => Assert.Equal(1, sender.Revision));
        Assert.Equal(SenderA, senders[0].AwsSenderId);
        Assert.True(senders[0].IsEnabled);
    }

    /// <summary>
    /// The scope the reader opens is resolved once and to one business, and a tenant-owned write in it
    /// is stamped by the central enforcer - so a reader scope is exactly a request's scope at the
    /// persistence boundary. A denied scope writes nothing, which is what the reader would be left
    /// with if it ever forgot to resolve one.
    /// </summary>
    [Fact]
    public async Task A_reader_scope_that_resolved_no_business_writes_nothing()
    {
        await using var fixture = await NayaxSqsSaleFixture.InMemoryAsync();

        await using var denied = TestAppDbContext.Denied(fixture.Options);

        await Assert.ThrowsAsync<CrossBusinessAccessException>(() =>
            NayaxSqsSaleFixture.Store(denied).PersistSqsSaleAsync(
                NayaxSqsSaleFixture.SqsSale(NayaxTransactionStatusIds.Completed),
                CancellationToken.None));

        await using var read = fixture.Unrestricted();
        Assert.Empty(await read.NayaxSales.AsNoTracking().ToListAsync());
    }

    /// <summary>
    /// The whole per-message path inside one business's scope: the real store, the real costing, the
    /// real replay, the real sender stamp, and the fleet that business owns.
    ///
    /// <para>The fixture message names the machine number <em>both</em> businesses own, which is the
    /// point: the fleet read is the business's own, so it answers for whichever scope asked.</para>
    /// </summary>
    private static async Task<NayaxSqsProcessingOutcome> ProcessForBusinessAsync(
        NayaxSqsSaleFixture fixture, int businessId)
    {
        await using var db = fixture.Context(businessId);

        var nayax = new Mock<INayaxLynxClient>();
        nayax.Setup(client => client.GetMachinesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([new NayaxMachine { MachineID = NayaxSqsSaleFixture.MachineId }]);

        var store = NayaxSqsSaleFixture.Store(db);

        var outcome = await new ProcessNayaxSqsSale(
                store,
                new FakeNayaxOperationCredentials(),
                nayax.Object,
                new NayaxSqsSlotProductResolver(nayax.Object),
                new EfNayaxSqsSenderActivityStore(db),
                new FakeClock(NayaxSqsSaleFixture.ReceivedAtUtc))
            .Handle("message-1", NayaxSqsRealMessage.Received, CancellationToken.None);

        return outcome;
    }
}
