using Inventory.Application.Gst;
using Inventory.Application.Tenancy;
using Inventory.Domain.Exceptions;
using Inventory.Domain.Gst;
using Xunit;

namespace InventoryApi.Tests.Application.Gst;

/// <summary>
/// The Preview/Apply orchestration of the historical GST classification maintenance workflow
/// (issue #433): that the preview persists nothing, that the apply writes exactly the previewed
/// changes inside one transaction, that a stale, tampered or foreign preview writes nothing at all,
/// and that re-running after an apply changes nothing.
///
/// The precedence, the counts and the GST are pinned against plain values in
/// <c>HistoricalGstClassificationPolicyTests</c>; what is asserted here is the sequence the
/// acceptance criteria turn on - the authoritative read, the comparison and the write as one
/// operation - which is why the fake store records its loads, its transaction and its writes.
/// </summary>
public class HistoricalGstClassificationUseCaseTests
{
    private const int BusinessA = 1;
    private const int BusinessB = 2;

    private static readonly GstClassificationState Unclassified = GstClassificationState.Unclassified;

    /// <summary>
    /// One unclassified purchase whose line is taxable by its product rule and whose delivery charge
    /// is taxable by the supplier's delivery default, with a package charge no default covers.
    /// </summary>
    private static HistoricalGstPurchase Unclassified7() =>
        new(
            PurchaseId: 7,
            DeliveryCost: 5.50m,
            Delivery: Unclassified,
            PackageCost: 3.00m,
            Package: Unclassified,
            SupplierDefaults: new SupplierGstDefaults(
                GstRules.None, GstClassification.Taxable, GstRules.None),
            Lines: [new HistoricalGstPurchaseLine(1, 100, 10m, 1.21m, Unclassified, GstClassification.Taxable)]);

    private static PreviewHistoricalGstClassification Preview(
        FakeHistoricalGstClassificationStore store, int businessId = BusinessA) =>
        new(store, new FakeCurrentBusinessProvider(businessId));

    private static ApplyHistoricalGstClassification Apply(
        FakeHistoricalGstClassificationStore store, int businessId = BusinessA) =>
        new(store, new FakeCurrentBusinessProvider(businessId));

    [Fact]
    public async Task The_preview_reports_the_plan_and_persists_nothing()
    {
        var store = new FakeHistoricalGstClassificationStore(Unclassified7());

        var preview = await Preview(store).Handle(CancellationToken.None);

        Assert.Equal(3, preview.Summary.ComponentsExamined);
        Assert.Equal(2, preview.Summary.BecomingTaxable);
        Assert.Equal(1, preview.Summary.StayingUnknown);
        Assert.Equal(1.60m, preview.Summary.InputGst);
        Assert.NotEmpty(preview.Fingerprint);

        // Read-only by construction: no transaction, no write.
        Assert.Equal(1, store.Loads);
        Assert.Equal(0, store.TransactionsBegun);
        Assert.Null(store.Applied);
    }

    [Fact]
    public async Task The_apply_writes_exactly_the_previewed_changes_in_one_committed_transaction()
    {
        var store = new FakeHistoricalGstClassificationStore(Unclassified7());
        var preview = await Preview(store).Handle(CancellationToken.None);

        var applied = await Apply(store).Handle(
            new ApplyHistoricalGstClassificationRequest(preview.Fingerprint),
            CancellationToken.None);

        Assert.Equal(2, applied.ComponentsClassified);
        Assert.Equal(preview.Summary, applied.Summary);
        Assert.Equal(1, store.TransactionsBegun);
        Assert.True(store.Committed);
        Assert.Equal(
            [
                new HistoricalGstClassificationChange(
                    GstComponentKind.ProductLine, 7, 1, GstClassification.Taxable,
                    GstClassificationSource.ProductRule),
                new HistoricalGstClassificationChange(
                    GstComponentKind.DeliveryCharge, 7, null, GstClassification.Taxable,
                    GstClassificationSource.SupplierFeeDefault),
            ],
            store.Applied);
    }

    /// <summary>
    /// The apply does not trust the preview it is handed: it re-reads the history inside its own
    /// transaction and re-derives the plan from that read, which is the only way a change between
    /// the two can be noticed at all.
    /// </summary>
    [Fact]
    public async Task The_apply_re_reads_the_history_inside_its_transaction()
    {
        var store = new FakeHistoricalGstClassificationStore(Unclassified7());
        var preview = await Preview(store).Handle(CancellationToken.None);
        Assert.Equal(1, store.Loads);

        await Apply(store).Handle(
            new ApplyHistoricalGstClassificationRequest(preview.Fingerprint),
            CancellationToken.None);

        Assert.Equal(2, store.Loads);
    }

    /// <summary>
    /// The concurrent-change case: a purchase line is classified by hand between the preview and the
    /// apply. The plan the operator approved is no longer the plan that would be applied, so the
    /// apply refuses it and writes nothing - no partial update, and the transaction never commits.
    /// </summary>
    [Fact]
    public async Task A_concurrent_classification_between_preview_and_apply_is_refused_and_writes_nothing()
    {
        var store = new FakeHistoricalGstClassificationStore(Unclassified7());
        var preview = await Preview(store).Handle(CancellationToken.None);
        store.ChangeHistoryBeforeRead = changed => changed.Replace(
            Unclassified7() with
            {
                Lines =
                [
                    new HistoricalGstPurchaseLine(
                        1,
                        100,
                        10m,
                        1.21m,
                        new GstClassificationState(GstClassification.GstFree, GstClassificationSource.Manual),
                        GstClassification.Taxable),
                ],
            });

        var refused = await Assert.ThrowsAsync<DomainValidationException>(() => Apply(store).Handle(
            new ApplyHistoricalGstClassificationRequest(preview.Fingerprint),
            CancellationToken.None));

        Assert.Equal(ApplyHistoricalGstClassification.StalePreviewMessage, refused.Message);
        Assert.Null(store.Applied);
        Assert.False(store.Committed);
        Assert.True(store.Disposed);
    }

    /// <summary>
    /// A purchase amount edited in between is refused for the same reason: the previewed GST figure
    /// no longer describes the data that would be classified.
    /// </summary>
    [Fact]
    public async Task A_concurrent_purchase_edit_between_preview_and_apply_is_refused()
    {
        var store = new FakeHistoricalGstClassificationStore(Unclassified7());
        var preview = await Preview(store).Handle(CancellationToken.None);
        store.ChangeHistoryBeforeRead = changed =>
            changed.Replace(Unclassified7() with { DeliveryCost = 6.00m });

        await Assert.ThrowsAsync<DomainValidationException>(() => Apply(store).Handle(
            new ApplyHistoricalGstClassificationRequest(preview.Fingerprint),
            CancellationToken.None));

        Assert.Null(store.Applied);
        Assert.False(store.Committed);
    }

    /// <summary>
    /// A configured rule changed in between is refused too: the rules are relevant source data, not
    /// just the purchases.
    /// </summary>
    [Fact]
    public async Task A_concurrent_rule_change_between_preview_and_apply_is_refused()
    {
        var store = new FakeHistoricalGstClassificationStore(Unclassified7());
        var preview = await Preview(store).Handle(CancellationToken.None);
        store.ChangeHistoryBeforeRead = changed => changed.Replace(
            Unclassified7() with
            {
                SupplierDefaults = new SupplierGstDefaults(
                    GstClassification.GstFree, GstClassification.Taxable, GstRules.None),
            });

        await Assert.ThrowsAsync<DomainValidationException>(() => Apply(store).Handle(
            new ApplyHistoricalGstClassificationRequest(preview.Fingerprint),
            CancellationToken.None));

        Assert.Null(store.Applied);
        Assert.False(store.Committed);
    }

    /// <summary>
    /// A tampered or invented fingerprint is the same refusal. There is nothing else in the request
    /// to tamper with: the apply derives every classification, provenance and total from its own
    /// read, so no submitted value can reach the database.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("not-a-fingerprint")]
    [InlineData("0000000000000000000000000000000000000000000000000000000000000000")]
    public async Task A_tampered_fingerprint_is_refused_and_writes_nothing(string fingerprint)
    {
        var store = new FakeHistoricalGstClassificationStore(Unclassified7());

        await Assert.ThrowsAsync<DomainValidationException>(() => Apply(store).Handle(
            new ApplyHistoricalGstClassificationRequest(fingerprint),
            CancellationToken.None));

        Assert.Null(store.Applied);
        Assert.False(store.Committed);
    }

    /// <summary>
    /// Tenant binding: a preview taken by one business is refused by another, even though both see
    /// the identical purchase history here. The business comes from the authenticated actor's
    /// membership and is part of the fingerprint, so a foreign preview can never be applied.
    /// </summary>
    [Fact]
    public async Task Another_businesss_preview_is_refused()
    {
        var store = new FakeHistoricalGstClassificationStore(Unclassified7());
        var foreign = await Preview(store, BusinessA).Handle(CancellationToken.None);

        await Assert.ThrowsAsync<DomainValidationException>(() => Apply(store, BusinessB).Handle(
            new ApplyHistoricalGstClassificationRequest(foreign.Fingerprint),
            CancellationToken.None));

        Assert.Null(store.Applied);
        Assert.False(store.Committed);
    }

    /// <summary>
    /// Even with nothing to classify, a foreign preview is refused: two empty histories are the one
    /// case that would otherwise render identically for every business.
    /// </summary>
    [Fact]
    public async Task Another_businesss_preview_of_an_empty_history_is_refused()
    {
        var store = new FakeHistoricalGstClassificationStore();
        var foreign = await Preview(store, BusinessA).Handle(CancellationToken.None);

        await Assert.ThrowsAsync<DomainValidationException>(() => Apply(store, BusinessB).Handle(
            new ApplyHistoricalGstClassificationRequest(foreign.Fingerprint),
            CancellationToken.None));

        Assert.Null(store.Applied);
    }

    /// <summary>
    /// Fail closed: a caller whose business cannot be resolved reads nothing and writes nothing,
    /// rather than previewing or classifying an unscoped history.
    /// </summary>
    [Fact]
    public async Task A_caller_with_no_resolvable_business_reads_and_writes_nothing()
    {
        var store = new FakeHistoricalGstClassificationStore(Unclassified7());
        var preview = new PreviewHistoricalGstClassification(store, FakeCurrentBusinessProvider.Denied());
        var apply = new ApplyHistoricalGstClassification(store, FakeCurrentBusinessProvider.Denied());

        await Assert.ThrowsAsync<BusinessAccessDeniedException>(
            () => preview.Handle(CancellationToken.None));
        await Assert.ThrowsAsync<BusinessAccessDeniedException>(() => apply.Handle(
            new ApplyHistoricalGstClassificationRequest("whatever"), CancellationToken.None));

        Assert.Equal(0, store.Loads);
        Assert.Null(store.Applied);
        Assert.False(store.Committed);
    }

    [Fact]
    public async Task A_null_apply_request_is_refused_before_any_transaction_opens()
    {
        var store = new FakeHistoricalGstClassificationStore(Unclassified7());

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => Apply(store).Handle(null!, CancellationToken.None));

        Assert.Equal(0, store.TransactionsBegun);
        Assert.Null(store.Applied);
    }

    /// <summary>
    /// The idempotent rerun, through the use cases: a fresh preview over the state an apply leaves
    /// behind has nothing to examine, and applying that preview writes nothing.
    /// </summary>
    [Fact]
    public async Task Re_running_preview_and_apply_after_an_apply_changes_nothing()
    {
        var applied = new HistoricalGstPurchase(
            PurchaseId: 7,
            DeliveryCost: 5.50m,
            Delivery: new GstClassificationState(
                GstClassification.Taxable, GstClassificationSource.SupplierFeeDefault),
            PackageCost: 3.00m,
            Package: Unclassified,
            SupplierDefaults: new SupplierGstDefaults(GstRules.None, GstClassification.Taxable, GstRules.None),
            Lines:
            [
                new HistoricalGstPurchaseLine(
                    1,
                    100,
                    10m,
                    1.21m,
                    new GstClassificationState(GstClassification.Taxable, GstClassificationSource.ProductRule),
                    GstClassification.Taxable),
            ]);
        var store = new FakeHistoricalGstClassificationStore(applied);

        var preview = await Preview(store).Handle(CancellationToken.None);
        var reapplied = await Apply(store).Handle(
            new ApplyHistoricalGstClassificationRequest(preview.Fingerprint),
            CancellationToken.None);

        // Only the package charge is still examined, and no default covers it.
        Assert.Equal(1, preview.Summary.ComponentsExamined);
        Assert.Equal(1, preview.Summary.StayingUnknown);
        Assert.False(preview.Summary.ClassifiesAnything);
        Assert.Equal(0, reapplied.ComponentsClassified);
        Assert.Empty(store.Applied!);
        Assert.True(store.Committed);
    }
}
