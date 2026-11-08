using Inventory.Application.Nayax;
using Inventory.Application.NayaxSqs;
using Inventory.Application.SalesSync;
using Inventory.Domain.FinancialConfiguration;
using Inventory.Domain.Nayax;
using InventoryApi.Tests.Application.Nayax;
using InventoryApi.Tests.Application.Time;
using Moq;
using Xunit;

namespace InventoryApi.Tests.Application.NayaxSqs;

/// <summary>
/// Issue #1103: what one queued Nayax SQS message causes inside one business - the gates it has to
/// pass, the status the store is asked to save, and the one answer the reader acts on.
///
/// <para>The order is the rule and several of these cases are about it. The message is read first,
/// because a prepaid top-up needs no connection, no plan and no fleet read and asking Nayax about one
/// would spend the business's own request. Then every gate, before anything is written. Then one
/// durable write, then the rebuild drain - and the message is reported finished only when both are
/// done.</para>
/// </summary>
public class ProcessNayaxSqsSaleTests
{
    private static readonly DateTime Now = new(2026, 11, 6, 9, 30, 0, DateTimeKind.Utc);

    private const long MachineId = 4200001;
    private const long NayaxProductId = 4300001;
    private const string MessageId = "message-1";

    /// <summary>
    /// A message that cannot be read, one this application does not consume, and a free vend are each
    /// permanent: retrying cannot change what the message says, so it is released towards the
    /// dead-letter queue as a sample rather than deleted.
    /// </summary>
    [Theory]
    [InlineData("not json at all")]
    [InlineData("")]
    public async Task A_message_that_cannot_be_read_is_permanent_and_nothing_is_saved(string body)
    {
        var harness = new Harness();

        var outcome = await harness.Handle(body);

        Assert.Equal(NayaxSqsDisposition.Permanent, outcome.Disposition);
        Assert.Equal(NayaxSqsProcessingReason.MessageRefused, outcome.Reason);
        harness.AssertNothingSaved();
    }

    /// <summary>
    /// A pending status, an unrecognised status, a void and a negative amount are issue #1102's
    /// unsupported outcomes, and a free vend waits for a real sample. None of them reaches a Nayax
    /// call: the message is read before any gate, so a transaction this application will never
    /// consume costs the business nothing.
    ///
    /// <para>Status <c>62</c> was one of these until issue #1107 and no longer is: a refund now has
    /// its own outcome and its own gated write, which the refund cases further down cover.</para>
    /// </summary>
    [Theory]
    [InlineData("Transaction Status ID", "55")]
    [InlineData("Transaction Status ID", "999")]
    [InlineData("SeValue", "-3.50")]
    [InlineData("SeValue", "0")]
    public async Task An_unsupported_transaction_is_permanent_and_asks_nayax_nothing(
        string dataField, string json)
    {
        var harness = new Harness();

        var outcome = await harness.Handle(NayaxSqsRealMessage.ReceivedWith((dataField, json)));

        Assert.Equal(NayaxSqsDisposition.Permanent, outcome.Disposition);
        Assert.Equal(NayaxSqsProcessingReason.MessageRefused, outcome.Reason);
        harness.AssertNothingSaved();
        Assert.Equal(0, harness.DefaultCredentials.Resolutions);
    }

    /// <summary>
    /// A prepaid credit top-up and a multivend are understood and need no sale, so they are deleted.
    /// A multivend is left to the hourly <c>lastSales</c> run by parent #1100; neither asks Nayax
    /// anything.
    /// </summary>
    [Theory]
    [InlineData("Is Revalue Transaction")]
    [InlineData("Is Revalue Reward Transaction")]
    [InlineData("Is Multivend")]
    public async Task A_message_that_needs_no_sale_is_done_without_asking_nayax_anything(string flag)
    {
        var harness = new Harness();

        var outcome = await harness.Handle(NayaxSqsRealMessage.ReceivedWith((flag, "true")));

        Assert.Equal(NayaxSqsDisposition.Done, outcome.Disposition);
        Assert.Equal(NayaxSqsProcessingReason.NotASaleToStore, outcome.Reason);
        harness.AssertNothingSaved();
        Assert.Equal(0, harness.DefaultCredentials.Resolutions);

        // The message was still a message from this business's sender, so the last-message instant is
        // stamped: the Super Admin's page is about whether the queue is delivering at all.
        Assert.Equal([Now], harness.SenderActivity.Stamps);
    }

    /// <summary>
    /// The credential gate is the existing one, applied exactly as the request-driven sync applies
    /// it - the connection has to be usable and the plan has to include the sales provider - and a
    /// refusal is reported as <em>transient</em>. That is the one judgement the issue's table leaves
    /// open, and it is the principled reading: an unknown sender and a foreign machine are statements
    /// about the message, while a disconnected business or a downgraded plan is a statement about
    /// today, and reconnecting makes the very same message saveable.
    /// </summary>
    [Fact]
    public async Task A_business_with_no_usable_nayax_connection_is_transient_and_nothing_is_saved()
    {
        var harness = new Harness
        {
            Credentials = FakeNayaxOperationCredentials.Refusing(NayaxConnectionStatus.NeedsAttention),
        };

        var outcome = await harness.Handle(NayaxSqsRealMessage.Received);

        Assert.Equal(NayaxSqsDisposition.Transient, outcome.Disposition);
        Assert.Equal(NayaxSqsProcessingReason.ConnectionUnavailable, outcome.Reason);
        harness.AssertNothingSaved();
    }

    /// <summary>
    /// A business whose plan does not include the sales provider resolves no credentials either, and
    /// is reported on the same terms - the provider raises the same refusal with its own plan reason.
    /// </summary>
    [Fact]
    public async Task A_business_whose_plan_excludes_the_sales_provider_is_transient_too()
    {
        var harness = new Harness
        {
            Credentials = new PlanRefusingCredentials(),
        };

        var outcome = await harness.Handle(NayaxSqsRealMessage.Received);

        Assert.Equal(NayaxSqsDisposition.Transient, outcome.Disposition);
        Assert.Equal(NayaxSqsProcessingReason.ConnectionUnavailable, outcome.Reason);
        harness.AssertNothingSaved();
    }

    /// <summary>
    /// A machine neither the cached fleet list nor one fresh read holds belongs to somebody else, and
    /// no amount of retrying changes that. Saving it would attribute a sale, its stock and its revenue
    /// to a machine this business does not have.
    /// </summary>
    [Fact]
    public async Task A_machine_that_is_not_the_businesss_is_permanently_refused()
    {
        var harness = new Harness();
        harness.WithFleet(cached: [9999], fresh: [9999]);

        var outcome = await harness.Handle(NayaxSqsRealMessage.Received);

        Assert.Equal(NayaxSqsDisposition.Permanent, outcome.Disposition);
        Assert.Equal(NayaxSqsProcessingReason.MachineNotInFleet, outcome.Reason);
        harness.AssertNothingSaved();
    }

    /// <summary>
    /// The cached fleet list answers first, so a queue draining a hundred messages for one machine is
    /// not a hundred fleet reads.
    /// </summary>
    [Fact]
    public async Task A_machine_the_cached_fleet_list_holds_needs_no_fresh_read()
    {
        var harness = new Harness();

        var outcome = await harness.Handle(NayaxSqsRealMessage.Received);

        Assert.Equal(NayaxSqsDisposition.Done, outcome.Disposition);
        harness.Nayax.Verify(x => x.GetMachinesAsync(It.IsAny<CancellationToken>()), Times.Once);
        harness.Nayax.Verify(x => x.GetMachinesFreshAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// And a machine the cached list is missing gets exactly <em>one</em> fresh read, so a machine
    /// added minutes ago is not refused for the ten minutes the cache lives.
    /// </summary>
    [Fact]
    public async Task A_machine_missing_from_the_cached_list_is_checked_against_one_fresh_read()
    {
        var harness = new Harness();
        harness.WithFleet(cached: [], fresh: [MachineId]);

        var outcome = await harness.Handle(NayaxSqsRealMessage.Received);

        Assert.Equal(NayaxSqsDisposition.Done, outcome.Disposition);
        harness.Nayax.Verify(x => x.GetMachinesFreshAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// A Nayax read that failed or timed out while the gates were being resolved is transient: the
    /// sale is parked for the next receive rather than discarded or dead-lettered.
    /// </summary>
    [Fact]
    public async Task A_nayax_read_that_fails_while_gating_is_transient()
    {
        var harness = new Harness();
        harness.Nayax
            .Setup(x => x.GetMachinesAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TimeoutException("Nayax did not answer"));

        var outcome = await harness.Handle(NayaxSqsRealMessage.Received);

        Assert.Equal(NayaxSqsDisposition.Transient, outcome.Disposition);
        Assert.Equal(NayaxSqsProcessingReason.ProviderUnavailable, outcome.Reason);
        harness.AssertNothingSaved();
    }

    /// <summary>
    /// The status the store is asked to save is the message's own <c>Transaction Status ID</c>, and
    /// the message id and the instant travel with it as the row's provenance. This is the input half
    /// of the status rule; the saved half is asserted over a real database in
    /// <c>NayaxSqsSaleStatusTests</c>.
    /// </summary>
    [Theory]
    [InlineData("12", NayaxTransactionStatusIds.Completed)]
    [InlineData("26", NayaxTransactionStatusIds.CancelledOrDeclined26)]
    [InlineData("28", NayaxTransactionStatusIds.CashlessCancelledProductNotDispensed)]
    [InlineData("31", NayaxTransactionStatusIds.CancelledOrDeclined31)]
    [InlineData("250", NayaxTransactionStatusIds.CancelledOrDeclined250)]
    public async Task The_store_is_asked_to_save_the_messages_own_status(string json, int expected)
    {
        var harness = new Harness();

        await harness.Handle(NayaxSqsRealMessage.ReceivedWith(("Transaction Status ID", json)));

        var saved = Assert.Single(harness.Saved);
        Assert.Equal(expected, saved.SuppliedTransactionStatusId);
        Assert.Equal(MessageId, saved.MessageId);
        Assert.Equal(Now, saved.ReceivedAtUtc);
    }

    /// <summary>
    /// A message that states a usable <c>"Product ID"</c> is saved with it, and the machine's product
    /// map is not read at all: the provider already answered which product was vended.
    /// </summary>
    [Fact]
    public async Task A_message_stating_a_product_id_is_saved_with_it_and_reads_no_product_map()
    {
        var harness = new Harness();

        await harness.Handle(NayaxSqsRealMessage.Received);

        Assert.Equal(NayaxProductId, Assert.Single(harness.Saved).Report.NayaxProductId);
        harness.Nayax.Verify(
            x => x.GetMachineProductsAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// And a message that states none has its slot resolved through the machine's product map (issue
    /// #1102), which is the only thing it then says about which product was vended besides its name.
    /// </summary>
    [Fact]
    public async Task A_message_stating_no_product_id_has_its_slot_resolved()
    {
        var harness = new Harness();
        harness.Slots.Resolve = 7788;

        await harness.Handle(NayaxSqsRealMessage.ReceivedWithProduct(("Product ID", null)));

        Assert.Equal(7788L, Assert.Single(harness.Saved).Report.NayaxProductId);
        Assert.Equal([(MachineId, 14)], harness.Slots.Lookups);
    }

    /// <summary>
    /// A write that could not take the database's write lock, or any other persistence failure that
    /// may pass, is transient: nothing was saved and the next receive is the retry.
    /// </summary>
    [Fact]
    public async Task A_store_failure_is_transient()
    {
        var harness = new Harness();
        harness.Store
            .Setup(x => x.PersistSqsSaleAsync(It.IsAny<NayaxSqsSaleInput>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("database is locked"));

        var outcome = await harness.Handle(NayaxSqsRealMessage.Received);

        Assert.Equal(NayaxSqsDisposition.Transient, outcome.Disposition);
        Assert.Equal(NayaxSqsProcessingReason.StoreUnavailable, outcome.Reason);
    }

    /// <summary>
    /// The sale is durably saved and one of <em>its own</em> products still has a pending rebuild, so
    /// the message is not finished: the next receive finds the stored sale, sees the pending rebuild
    /// and retries exactly that.
    /// </summary>
    [Fact]
    public async Task A_sale_whose_own_rebuild_is_still_pending_keeps_the_message()
    {
        var harness = new Harness();
        harness.WithPersisted(inserted: true, affectedProductIds: [55]);
        harness.WithDrain(stillPending: [55]);

        var outcome = await harness.Handle(NayaxSqsRealMessage.Received);

        Assert.Equal(NayaxSqsDisposition.Transient, outcome.Disposition);
        Assert.Equal(NayaxSqsProcessingReason.RebuildPending, outcome.Reason);
    }

    /// <summary>
    /// A <em>different</em> product of the same business still failing deliberately does not hold this
    /// message. This sale is durably correct, and keeping the message would let one unreplayable
    /// product's history stop every later sale from ever being acknowledged - the whole queue would
    /// dead-letter over it.
    /// </summary>
    [Fact]
    public async Task Another_products_failing_rebuild_does_not_hold_this_message()
    {
        var harness = new Harness();
        harness.WithPersisted(inserted: true, affectedProductIds: [55]);
        harness.WithDrain(stillPending: [66]);

        var outcome = await harness.Handle(NayaxSqsRealMessage.Received);

        Assert.Equal(NayaxSqsDisposition.Done, outcome.Disposition);
        Assert.Equal(NayaxSqsProcessingReason.Saved, outcome.Reason);
    }

    /// <summary>
    /// A redelivery - or the <c>lastSales</c> sync having got there first - finds the transaction
    /// stored, drains whatever is pending for it, and is finished with a distinct reason so the two
    /// cases are told apart in a log rather than collapsed into "saved".
    /// </summary>
    [Fact]
    public async Task An_already_stored_transaction_is_done_with_its_own_reason()
    {
        var harness = new Harness();
        harness.WithPersisted(inserted: false, affectedProductIds: [55]);

        var outcome = await harness.Handle(NayaxSqsRealMessage.Received);

        Assert.Equal(NayaxSqsDisposition.Done, outcome.Disposition);
        Assert.Equal(NayaxSqsProcessingReason.AlreadyStored, outcome.Reason);
        harness.Store.Verify(
            x => x.RebuildInventoryCostsAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// The last-message instant is a stamp somebody looks at, not a financial value: a failure to
    /// write it is swallowed rather than turned into a disposition, because refusing to acknowledge a
    /// message over a cosmetic instant would redeliver a transaction that is finished with.
    /// </summary>
    [Fact]
    public async Task A_failure_to_stamp_the_sender_row_does_not_change_the_outcome()
    {
        var harness = new Harness();
        harness.SenderActivity.Failure = new InvalidOperationException("database is locked");

        var outcome = await harness.Handle(NayaxSqsRealMessage.Received);

        Assert.Equal(NayaxSqsDisposition.Done, outcome.Disposition);
        Assert.Equal(NayaxSqsProcessingReason.Saved, outcome.Reason);
    }

    #region Refunds (issue #1107)

    /// <summary>
    /// A refund takes the same gates as a sale and then a different write: the store is asked to
    /// apply it against the transaction the message names, with the message's own machine, amount and
    /// product travelling to the comparison and the message id and instant to the audit row. Nothing
    /// is persisted as a sale and no rebuild is drained - status <c>12</c> and status <c>62</c> both
    /// consume stock, so the change moves no quantity, value or cost and there is nothing to replay.
    /// </summary>
    [Fact]
    public async Task A_refund_is_applied_against_the_transaction_it_names_and_saves_no_sale()
    {
        var harness = new Harness();

        var outcome = await harness.Handle(
            NayaxSqsRealMessage.ReceivedWith(("Transaction Status ID", "62")));

        Assert.Equal(NayaxSqsDisposition.Done, outcome.Disposition);
        Assert.Equal(NayaxSqsProcessingReason.RefundApplied, outcome.Reason);

        var refund = Assert.Single(harness.Refunds);
        Assert.Equal(4100000001L, refund.TransactionId);
        Assert.Equal(MachineId, refund.MachineId);
        Assert.Equal(3.5m, refund.SettlementValue);
        Assert.Equal(NayaxProductId, refund.NayaxProductId);
        Assert.Equal(MessageId, refund.MessageId);
        Assert.Equal(Now, refund.ReceivedAtUtc);

        harness.AssertNoSaleSaved();
        harness.Store.Verify(
            x => x.RebuildInventoryCostsAsync(It.IsAny<CancellationToken>()), Times.Never);

        // A message from this business's sender still stamps the last-message instant.
        Assert.Equal([Now], harness.SenderActivity.Stamps);
    }

    /// <summary>
    /// A refund resolves no product, so the machine's product map is never read for one - not even
    /// when the message states no usable <c>"Product ID"</c>. There is no product to match: the
    /// refund names a sale that already records one.
    /// </summary>
    [Fact]
    public async Task A_refund_never_reads_the_machines_product_map()
    {
        var harness = new Harness();
        harness.Slots.Resolve = 7788;

        await harness.Handle(
            NayaxSqsRealMessage.ReceivedWithDataAndProduct(
                dataFields: [("Transaction Status ID", "62")], productFields: [("Product ID", null)]));

        Assert.Empty(harness.Slots.Lookups);
        Assert.Null(Assert.Single(harness.Refunds).NayaxProductId);
    }

    /// <summary>
    /// The refund outcomes the store reports, and what the reader does with each of them. Applied and
    /// already-applied are finished with; everything the store could not place is <em>permanent</em>,
    /// because retrying cannot make this business's stored sales say something else and a sample of a
    /// refund this application could not apply is exactly what belongs in the dead-letter queue.
    /// </summary>
    [Theory]
    [InlineData(NayaxSqsRefundApplication.Applied, NayaxSqsDisposition.Done, NayaxSqsProcessingReason.RefundApplied)]
    [InlineData(NayaxSqsRefundApplication.AlreadyRefunded, NayaxSqsDisposition.Done, NayaxSqsProcessingReason.RefundAlreadyApplied)]
    [InlineData(NayaxSqsRefundApplication.NoMatchingSale, NayaxSqsDisposition.Permanent, NayaxSqsProcessingReason.RefundUnmatched)]
    [InlineData(NayaxSqsRefundApplication.NotRefundable, NayaxSqsDisposition.Permanent, NayaxSqsProcessingReason.RefundNotApplicable)]
    [InlineData(NayaxSqsRefundApplication.Contradicted, NayaxSqsDisposition.Permanent, NayaxSqsProcessingReason.RefundContradicted)]
    public async Task Each_refund_outcome_has_its_own_disposition_and_reason(
        NayaxSqsRefundApplication applied,
        NayaxSqsDisposition expectedDisposition,
        NayaxSqsProcessingReason expectedReason)
    {
        var harness = new Harness();
        harness.WithRefund(applied);

        var outcome = await harness.Handle(
            NayaxSqsRealMessage.ReceivedWith(("Transaction Status ID", "62")));

        Assert.Equal(expectedDisposition, outcome.Disposition);
        Assert.Equal(expectedReason, outcome.Reason);
        harness.AssertNoSaleSaved();
    }

    /// <summary>
    /// A refund this application could not place leaves the sender's last-message stamp alone, which
    /// keeps that instant meaning "the queue delivered something we understood".
    /// </summary>
    [Theory]
    [InlineData(NayaxSqsRefundApplication.NoMatchingSale)]
    [InlineData(NayaxSqsRefundApplication.Contradicted)]
    public async Task A_refund_that_was_not_applied_stamps_nothing(NayaxSqsRefundApplication applied)
    {
        var harness = new Harness();
        harness.WithRefund(applied);

        await harness.Handle(NayaxSqsRealMessage.ReceivedWith(("Transaction Status ID", "62")));

        Assert.Empty(harness.SenderActivity.Stamps);
    }

    /// <summary>
    /// A refund still passes every gate a sale passes, before anything is written: a business with no
    /// usable connection or no sales-provider plan is transient, and a machine that is not the
    /// business's is permanently refused. A refund applied for a business whose plan no longer
    /// includes the provider would be the one write that ignored a gate every other one honours.
    /// </summary>
    [Fact]
    public async Task A_refund_is_gated_exactly_as_a_sale_is()
    {
        var disconnected = new Harness
        {
            Credentials = FakeNayaxOperationCredentials.Refusing(NayaxConnectionStatus.NeedsAttention),
        };

        var refused = await disconnected.Handle(
            NayaxSqsRealMessage.ReceivedWith(("Transaction Status ID", "62")));

        Assert.Equal(NayaxSqsDisposition.Transient, refused.Disposition);
        Assert.Equal(NayaxSqsProcessingReason.ConnectionUnavailable, refused.Reason);
        Assert.Empty(disconnected.Refunds);

        var foreignMachine = new Harness();
        foreignMachine.WithFleet(cached: [9999], fresh: [9999]);

        var rejected = await foreignMachine.Handle(
            NayaxSqsRealMessage.ReceivedWith(("Transaction Status ID", "62")));

        Assert.Equal(NayaxSqsDisposition.Permanent, rejected.Disposition);
        Assert.Equal(NayaxSqsProcessingReason.MachineNotInFleet, rejected.Reason);
        Assert.Empty(foreignMachine.Refunds);
    }

    /// <summary>
    /// A persistence failure while applying a refund is transient and nothing was changed: the next
    /// receive is the retry, exactly as it is for a sale.
    /// </summary>
    [Fact]
    public async Task A_store_failure_while_applying_a_refund_is_transient()
    {
        var harness = new Harness();
        harness.Store
            .Setup(x => x.ApplySqsRefundAsync(It.IsAny<NayaxSqsRefundInput>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("database is locked"));

        var outcome = await harness.Handle(
            NayaxSqsRealMessage.ReceivedWith(("Transaction Status ID", "62")));

        Assert.Equal(NayaxSqsDisposition.Transient, outcome.Disposition);
        Assert.Equal(NayaxSqsProcessingReason.StoreUnavailable, outcome.Reason);
    }

    #endregion

    /// <summary>
    /// Shutdown propagates rather than becoming a disposition: an in-flight message is either
    /// finished and deleted or left for redelivery, never reported as a failure of its own.
    /// </summary>
    [Fact]
    public async Task Shutdown_propagates_rather_than_becoming_a_disposition()
    {
        var harness = new Harness();
        using var stopping = new CancellationTokenSource();
        await stopping.CancelAsync();
        harness.Store
            .Setup(x => x.PersistSqsSaleAsync(It.IsAny<NayaxSqsSaleInput>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => harness.Handle(NayaxSqsRealMessage.Received, stopping.Token));
    }

    /// <summary>
    /// The use case with every port doubled, and the pieces a case needs to drive or read exposed.
    /// </summary>
    private sealed class Harness
    {
        public Harness()
        {
            Store = new Mock<ILatestNayaxSalesStore>();
            WithPersisted(inserted: true, affectedProductIds: []);
            WithDrain(stillPending: []);
            WithRefund(NayaxSqsRefundApplication.Applied);

            Nayax = new Mock<INayaxLynxClient>();
            WithFleet(cached: [MachineId], fresh: [MachineId]);

            Credentials = DefaultCredentials;
        }

        /// <summary>
        /// The usable connection the ordinary cases run on, kept as its own type so a case can assert
        /// that the gate was not even reached.
        /// </summary>
        public FakeNayaxOperationCredentials DefaultCredentials { get; } = new();

        public Mock<ILatestNayaxSalesStore> Store { get; }

        public Mock<INayaxLynxClient> Nayax { get; }

        public FakeSlotResolver Slots { get; } = new();

        public RecordingSenderActivity SenderActivity { get; } = new();

        public INayaxRequestCredentialProvider Credentials { get; init; }

        /// <summary>Every message the store was asked to save, in order.</summary>
        public List<NayaxSqsSaleInput> Saved { get; } = [];

        /// <summary>Every refund the store was asked to apply, in order (issue #1107).</summary>
        public List<NayaxSqsRefundInput> Refunds { get; } = [];

        public Task<NayaxSqsProcessingOutcome> Handle(
            string? body, CancellationToken cancellationToken = default) =>
            new ProcessNayaxSqsSale(
                    Store.Object,
                    Credentials,
                    Nayax.Object,
                    Slots,
                    SenderActivity,
                    new FakeClock(Now))
                .Handle(MessageId, body, cancellationToken);

        public void WithFleet(long[] cached, long[] fresh)
        {
            Nayax.Setup(x => x.GetMachinesAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync([.. cached.Select(id => new NayaxMachine { MachineID = id })]);
            Nayax.Setup(x => x.GetMachinesFreshAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync([.. fresh.Select(id => new NayaxMachine { MachineID = id })]);
        }

        public void WithPersisted(bool inserted, long[] affectedProductIds)
        {
            Store.Setup(x => x.PersistSqsSaleAsync(
                    It.IsAny<NayaxSqsSaleInput>(), It.IsAny<CancellationToken>()))
                .Callback<NayaxSqsSaleInput, CancellationToken>((input, _) => Saved.Add(input))
                .ReturnsAsync(new NayaxSqsSalePersistResult(inserted, affectedProductIds));
        }

        public void WithDrain(long[] stillPending)
        {
            Store.Setup(x => x.RebuildInventoryCostsAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new InventoryCostRebuildDrainResult(stillPending, []));
        }

        public void WithRefund(NayaxSqsRefundApplication applied)
        {
            Store.Setup(x => x.ApplySqsRefundAsync(
                    It.IsAny<NayaxSqsRefundInput>(), It.IsAny<CancellationToken>()))
                .Callback<NayaxSqsRefundInput, CancellationToken>((input, _) => Refunds.Add(input))
                .ReturnsAsync(applied);
        }

        /// <summary>Nothing reached the store, which is what "before anything is saved" has to mean.</summary>
        public void AssertNothingSaved()
        {
            AssertNoSaleSaved();
            Assert.Empty(Refunds);
            Store.Verify(
                x => x.ApplySqsRefundAsync(
                    It.IsAny<NayaxSqsRefundInput>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }

        /// <summary>
        /// No sale was stored and no replay was drained - what a refund has to leave true, because a
        /// refund is evidence about a sale and never a sale.
        /// </summary>
        public void AssertNoSaleSaved()
        {
            Assert.Empty(Saved);
            Store.Verify(
                x => x.PersistSqsSaleAsync(It.IsAny<NayaxSqsSaleInput>(), It.IsAny<CancellationToken>()),
                Times.Never);
            Store.Verify(
                x => x.RebuildInventoryCostsAsync(It.IsAny<CancellationToken>()), Times.Never);
        }
    }

    /// <summary>The slot lookup, recorded: a case states what it answers and asserts it was asked.</summary>
    private sealed class FakeSlotResolver : INayaxSqsSlotProductResolver
    {
        public List<(long MachineId, int? Slot)> Lookups { get; } = [];

        public long? Resolve { get; set; }

        public Task<long?> ResolveAsync(
            long machineId, int? productSlot, CancellationToken cancellationToken = default)
        {
            Lookups.Add((machineId, productSlot));
            return Task.FromResult(Resolve);
        }
    }

    /// <summary>The sender row's last-message stamp, recorded, and optionally failing.</summary>
    private sealed class RecordingSenderActivity : INayaxSqsSenderActivityStore
    {
        public List<DateTime> Stamps { get; } = [];

        public Exception? Failure { get; set; }

        public Task RecordMessageReceivedAsync(DateTime receivedAtUtc, CancellationToken cancellationToken)
        {
            if (Failure is { } failure)
            {
                return Task.FromException(failure);
            }

            Stamps.Add(receivedAtUtc);
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// The credential provider refusing for the plan reason of issue #967 rather than for the
    /// connection's status - the other half of the gate the reader applies unchanged.
    /// </summary>
    private sealed class PlanRefusingCredentials : INayaxRequestCredentialProvider
    {
        public Task<NayaxRequestCredential> GetForOperationAsync(CancellationToken cancellationToken) =>
            throw NayaxNotConnectedException.PlanRequired(
                Inventory.Domain.Subscriptions.SubscriptionPlan.Pro);

        public Task ReportUnauthorizedAsync(int credentialRevision, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}
