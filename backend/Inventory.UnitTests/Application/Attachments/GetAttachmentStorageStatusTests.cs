using Inventory.Application.Attachments;
using Inventory.Domain.Attachments;
using Inventory.Domain.Subscriptions;
using Xunit;

namespace InventoryApi.Tests.Application.Attachments;

/// <summary>
/// The attachment storage figures <c>GET /api/me/access</c> reports beside the entitlement
/// (issue #968): the business's own usage, its plan's allowance, and which of the three warning
/// states that puts it in.
/// </summary>
public class GetAttachmentStorageStatusTests
{
    private static readonly long FreeLimit =
        PlanCatalogue.For(SubscriptionPlan.Free).AttachmentStorageLimitBytes;

    /// <summary>
    /// While nothing is enforced there is no allowance to be a percentage of, so there is nothing
    /// to report - and nothing is read either, which is what keeps the shipped state's cost per
    /// request unchanged.
    /// </summary>
    [Fact]
    public async Task Nothing_is_reported_or_read_while_nothing_is_enforced()
    {
        var store = new RecordingQuotaStore();

        var status = await new GetAttachmentStorageStatus(store).Handle(
            EffectiveEntitlementPolicy.Unrestricted(EntitlementSource.EnforcementDisabled),
            CancellationToken.None);

        Assert.Null(status);
        Assert.Equal(0, store.Reads);
    }

    [Fact]
    public async Task The_usage_reported_is_the_stored_bytes_and_the_unexpired_reservations()
    {
        var store = new RecordingQuotaStore { Usage = new AttachmentStorageUsage(400, 100) };

        var status = await new GetAttachmentStorageStatus(store).Handle(Free(), CancellationToken.None);

        Assert.NotNull(status);
        Assert.Equal(500, status!.UsedBytes);
        Assert.Equal(FreeLimit, status.LimitBytes);
        Assert.Equal(AttachmentStorageWarningLevel.Normal, status.WarningLevel);
    }

    [Theory]
    [InlineData(0.5, AttachmentStorageWarningLevel.Normal)]
    [InlineData(0.8, AttachmentStorageWarningLevel.Warning)]
    [InlineData(1.0, AttachmentStorageWarningLevel.Full)]
    [InlineData(1.5, AttachmentStorageWarningLevel.Full)]
    public async Task The_warning_state_is_the_policys(double share, AttachmentStorageWarningLevel expected)
    {
        var used = (long)(FreeLimit * share);
        var store = new RecordingQuotaStore { Usage = new AttachmentStorageUsage(used, 0) };

        var status = await new GetAttachmentStorageStatus(store).Handle(Free(), CancellationToken.None);

        Assert.Equal(expected, status!.WarningLevel);
    }

    /// <summary>
    /// A downgrade leaves a business over its new allowance, and that is reported as what it is -
    /// full, with the usage above the limit - rather than clamped to look compliant. Nothing is
    /// deleted to make the figure fit.
    /// </summary>
    [Fact]
    public async Task A_business_above_its_allowance_reports_the_usage_it_actually_has()
    {
        var store = new RecordingQuotaStore
        {
            Usage = new AttachmentStorageUsage(FreeLimit * 2, 0),
        };

        var status = await new GetAttachmentStorageStatus(store).Handle(Free(), CancellationToken.None);

        Assert.Equal(FreeLimit * 2, status!.UsedBytes);
        Assert.Equal(FreeLimit, status.LimitBytes);
        Assert.Equal(AttachmentStorageWarningLevel.Full, status.WarningLevel);
    }

    /// <summary>Each plan's own allowance is reported, read from the catalogue and never restated.</summary>
    [Theory]
    [InlineData(SubscriptionPlan.Free)]
    [InlineData(SubscriptionPlan.Starter)]
    [InlineData(SubscriptionPlan.Pro)]
    public async Task The_allowance_is_the_effective_plans(SubscriptionPlan plan)
    {
        var store = new RecordingQuotaStore();

        var status = await new GetAttachmentStorageStatus(store).Handle(
            new EffectiveEntitlement(Enforced: true, plan, EntitlementSource.ManualGrant, null),
            CancellationToken.None);

        Assert.Equal(PlanCatalogue.For(plan).AttachmentStorageLimitBytes, status!.LimitBytes);
    }

    /// <summary>
    /// A plan this code does not declare is a record it cannot read, so there is no allowance to
    /// report - never the nearest plan it knows, which would withhold storage nobody agreed to
    /// withhold.
    /// </summary>
    [Fact]
    public async Task An_undeclared_plan_reports_no_allowance_at_all()
    {
        var store = new RecordingQuotaStore();

        var status = await new GetAttachmentStorageStatus(store).Handle(
            new EffectiveEntitlement(Enforced: true, (SubscriptionPlan)999, EntitlementSource.ManualGrant, null),
            CancellationToken.None);

        Assert.Null(status);
        Assert.Equal(0, store.Reads);
    }

    private static EffectiveEntitlement Free() =>
        new(Enforced: true, SubscriptionPlan.Free, EntitlementSource.NoActiveRecord, null);

    private sealed class RecordingQuotaStore : IAttachmentQuotaStore
    {
        public AttachmentStorageUsage Usage { get; set; } = new(0, 0);

        /// <summary>How many times usage was read, so an unnecessary read can be shown not to happen.</summary>
        public int Reads { get; private set; }

        public Task<AttachmentStorageUsage> ReadUsageAsync(CancellationToken cancellationToken = default)
        {
            Reads++;
            return Task.FromResult(Usage);
        }

        public Task<AttachmentUploadReservation?> ReserveAsync(
            AttachmentUploadRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The access read never reserves.");

        public Task<bool> ReleaseAsync(
            AttachmentUploadReservation reservation, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The access read never releases.");
    }
}
