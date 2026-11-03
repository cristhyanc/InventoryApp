using Inventory.Application.Products;
using Inventory.Application.Tenancy;
using Inventory.Domain.Exceptions;
using Inventory.Domain.Gst;
using Inventory.Domain.Tenancy;
using InventoryApi.Tests.Application.Gst;
using InventoryApi.Tests.Application.Tenancy;
using InventoryApi.Tests.Application.Time;
using Xunit;

namespace InventoryApi.Tests.Application.Products;

/// <summary>
/// The two use cases behind "Set GST rule in bulk" (issue #1076), without ASP.NET Core or EF Core:
/// the preview that counts the products carrying no rule, and the apply that configures exactly
/// those.
///
/// What they have to show here is where each decision comes from. The target rule is checked
/// against the Domain policy before anything is written; the eligible count the operator confirmed
/// is presented to the write and compared there, so a set that moved in between is refused rather
/// than quietly extended; the business comes from the caller's resolved membership and never from
/// the request; and the audit event is recorded after a committed change, for an identified actor,
/// and never for a refused attempt.
/// </summary>
public class BulkProductGstRuleUseCaseTests
{
    private const int BusinessId = 7;
    private const string DirectoryTenantId = "3F2504E0-4F89-11D3-9A0C-0305E82C3301";
    private const string ObjectId = "9A0C0305-4F89-11D3-9A0C-0305E82C3301";

    private static readonly DateTime AppliedAt = new(2026, 11, 3, 4, 5, 6, DateTimeKind.Utc);

    [Fact]
    public async Task The_preview_counts_only_the_products_carrying_no_rule()
    {
        var store = new FakeBulkProductGstRuleStore(
            GstRules.None, GstClassification.Taxable, GstRules.None, GstClassification.GstFree);

        var preview = await new PreviewBulkProductGstRule(store).Handle(CancellationToken.None);

        Assert.Equal(2, preview.EligibleCount);
    }

    [Fact]
    public async Task The_preview_writes_nothing()
    {
        var store = new FakeBulkProductGstRuleStore(GstRules.None, GstRules.None);

        await new PreviewBulkProductGstRule(store).Handle(CancellationToken.None);

        Assert.Equal([GstRules.None, GstRules.None], store.StoredRules);
        Assert.Null(store.LastRequestedRule);
    }

    [Fact]
    public async Task Nothing_eligible_is_a_count_of_zero_rather_than_an_absent_answer()
    {
        var store = new FakeBulkProductGstRuleStore(GstClassification.Taxable, GstClassification.GstFree);

        var preview = await new PreviewBulkProductGstRule(store).Handle(CancellationToken.None);

        Assert.Equal(0, preview.EligibleCount);
    }

    [Theory]
    [InlineData(GstClassification.Taxable)]
    [InlineData(GstClassification.GstFree)]
    public async Task The_apply_configures_every_eligible_product_and_leaves_an_explicit_rule_alone(
        GstClassification target)
    {
        var store = new FakeBulkProductGstRuleStore(
            GstRules.None, GstClassification.Taxable, GstRules.None, GstClassification.GstFree);
        var audit = new FakeBulkProductGstRuleAudit();

        var applied = await UseCase(store, audit).Handle(Request(target, 2), CancellationToken.None);

        Assert.Equal(target, applied.GstRule);
        Assert.Equal(2, applied.ChangedCount);
        Assert.Equal(
            [target, GstClassification.Taxable, target, GstClassification.GstFree], store.StoredRules);
    }

    [Fact]
    public async Task The_apply_presents_the_confirmed_count_to_the_write()
    {
        var store = new FakeBulkProductGstRuleStore(GstRules.None, GstRules.None, GstRules.None);

        await UseCase(store).Handle(Request(GstClassification.Taxable, 3), CancellationToken.None);

        Assert.Equal(3, store.LastExpectedEligibleCount);
    }

    /// <summary>
    /// The conflict this feature exists to avoid: the operator confirmed a count, somebody else
    /// configured, created or deleted a product, and applying anyway would write a set nobody
    /// approved. Nothing is written and nothing is audited.
    /// </summary>
    [Fact]
    public async Task A_count_that_moved_between_the_confirmation_and_the_write_is_refused()
    {
        var store = new FakeBulkProductGstRuleStore(GstRules.None, GstRules.None);
        var audit = new FakeBulkProductGstRuleAudit();
        store.BeforeWrite = () => store.SetStoredRule(1, GstClassification.GstFree);

        var refusal = await Assert.ThrowsAsync<DomainConflictException>(
            () => UseCase(store, audit).Handle(Request(GstClassification.Taxable, 2), CancellationToken.None));

        Assert.Equal(BulkProductGstRulePolicy.EligibleCountChangedMessage, refusal.Message);
        Assert.Equal([GstRules.None, GstClassification.GstFree], store.StoredRules);
        Assert.Empty(audit.Entries);
    }

    [Fact]
    public async Task A_product_created_with_no_rule_in_between_moves_the_count_and_is_refused()
    {
        var store = new FakeBulkProductGstRuleStore(GstRules.None);
        store.BeforeWrite = store.AddProductWithNoRule;

        await Assert.ThrowsAsync<DomainConflictException>(
            () => UseCase(store).Handle(Request(GstClassification.GstFree, 1), CancellationToken.None));

        Assert.Equal([GstRules.None, GstRules.None], store.StoredRules);
    }

    [Fact]
    public async Task No_rule_is_refused_as_a_target_and_writes_nothing()
    {
        var store = new FakeBulkProductGstRuleStore(GstRules.None, GstRules.None);
        var audit = new FakeBulkProductGstRuleAudit();

        var refusal = await Assert.ThrowsAsync<DomainValidationException>(
            () => UseCase(store, audit).Handle(Request(GstRules.None, 2), CancellationToken.None));

        Assert.Equal(BulkProductGstRulePolicy.UnsupportedTargetMessage, refusal.Message);
        Assert.Null(store.LastRequestedRule);
        Assert.Equal([GstRules.None, GstRules.None], store.StoredRules);
        Assert.Empty(audit.Entries);
    }

    [Theory]
    [InlineData(999)]
    [InlineData(-1)]
    [InlineData(3)]
    public async Task An_undefined_target_value_is_refused_and_writes_nothing(int value)
    {
        var store = new FakeBulkProductGstRuleStore(GstRules.None);

        await Assert.ThrowsAsync<DomainValidationException>(
            () => UseCase(store).Handle(
                Request((GstClassification)value, 1), CancellationToken.None));

        Assert.Null(store.LastRequestedRule);
        Assert.Equal([GstRules.None], store.StoredRules);
    }

    [Fact]
    public async Task A_request_carrying_no_confirmed_count_is_refused_and_writes_nothing()
    {
        var store = new FakeBulkProductGstRuleStore(GstRules.None);

        var refusal = await Assert.ThrowsAsync<DomainValidationException>(
            () => UseCase(store).Handle(
                new SetBulkProductGstRuleRequest(GstClassification.Taxable, null), CancellationToken.None));

        Assert.Equal(BulkProductGstRulePolicy.ExpectedEligibleCountRequiredMessage, refusal.Message);
        Assert.Null(store.LastRequestedRule);
    }

    [Fact]
    public async Task The_audit_event_records_the_actor_the_business_the_target_and_the_changed_count()
    {
        var store = new FakeBulkProductGstRuleStore(GstRules.None, GstRules.None, GstClassification.Taxable);
        var audit = new FakeBulkProductGstRuleAudit();

        await UseCase(store, audit).Handle(Request(GstClassification.GstFree, 2), CancellationToken.None);

        var entry = Assert.Single(audit.Entries);
        Assert.Equal(DirectoryTenantId, entry.ActorDirectoryTenantId);
        Assert.Equal(ObjectId, entry.ActorObjectId);
        Assert.Equal(BusinessId, entry.BusinessId);
        Assert.Equal(GstClassification.GstFree, entry.TargetRule);
        Assert.Equal(2, entry.ChangedCount);
        Assert.Equal(AppliedAt, entry.OccurredAtUtc);
    }

    /// <summary>
    /// A confirmed apply over nothing eligible is a no-op, so there is no change to be evidence
    /// about - the same rule the business-settings audit follows for a save that changed nothing.
    /// </summary>
    [Fact]
    public async Task A_change_of_nothing_is_not_audited()
    {
        var store = new FakeBulkProductGstRuleStore(GstClassification.Taxable);
        var audit = new FakeBulkProductGstRuleAudit();

        var applied = await UseCase(store, audit).Handle(
            Request(GstClassification.GstFree, 0), CancellationToken.None);

        Assert.Equal(0, applied.ChangedCount);
        Assert.Empty(audit.Entries);
    }

    [Theory]
    [InlineData(BusinessAccessDenialReason.MembershipMissing)]
    [InlineData(BusinessAccessDenialReason.BusinessInactive)]
    public async Task A_caller_with_no_usable_membership_writes_nothing(BusinessAccessDenialReason reason)
    {
        var store = new FakeBulkProductGstRuleStore(GstRules.None);

        await Assert.ThrowsAsync<BusinessAccessDeniedException>(
            () => new SetBulkProductGstRule(
                    store,
                    FakeCurrentBusinessProvider.Denied(reason),
                    FakeAuthenticatedActorAccessor.Identified(Actor()),
                    new FakeBulkProductGstRuleAudit(),
                    new FakeClock(AppliedAt))
                .Handle(Request(GstClassification.Taxable, 1), CancellationToken.None));

        Assert.Null(store.LastRequestedRule);
        Assert.Equal([GstRules.None], store.StoredRules);
    }

    /// <summary>
    /// An unauditable change is not made: the acting identity is read before the write, so a
    /// request with no identified actor is refused rather than stored with an anonymous event.
    /// </summary>
    [Fact]
    public async Task An_unidentified_actor_writes_nothing()
    {
        var store = new FakeBulkProductGstRuleStore(GstRules.None);

        await Assert.ThrowsAsync<BusinessAccessDeniedException>(
            () => new SetBulkProductGstRule(
                    store,
                    new FakeCurrentBusinessProvider(BusinessId),
                    FakeAuthenticatedActorAccessor.Unidentifiable(),
                    new FakeBulkProductGstRuleAudit(),
                    new FakeClock(AppliedAt))
                .Handle(Request(GstClassification.Taxable, 1), CancellationToken.None));

        Assert.Null(store.LastRequestedRule);
        Assert.Equal([GstRules.None], store.StoredRules);
    }

    private static SetBulkProductGstRuleRequest Request(GstClassification rule, int expectedEligibleCount) =>
        new(rule, expectedEligibleCount);

    private static SetBulkProductGstRule UseCase(
        FakeBulkProductGstRuleStore store, FakeBulkProductGstRuleAudit? audit = null) =>
        new(
            store,
            new FakeCurrentBusinessProvider(BusinessId),
            FakeAuthenticatedActorAccessor.Identified(Actor()),
            audit ?? new FakeBulkProductGstRuleAudit(),
            new FakeClock(AppliedAt));

    private static ActorIdentity Actor()
    {
        Assert.True(ActorIdentity.TryCreate(DirectoryTenantId, ObjectId, out var actor));
        return actor!;
    }
}
