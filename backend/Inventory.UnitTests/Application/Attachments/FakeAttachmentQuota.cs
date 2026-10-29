using Inventory.Application.Attachments;
using Inventory.Application.Subscriptions;
using Inventory.Domain.Attachments;
using Inventory.Domain.Subscriptions;
using InventoryApi.Tests.Application.Subscriptions;
using InventoryApi.Tests.Application.Time;

namespace InventoryApi.Tests.Application.Attachments;

/// <summary>
/// In-memory fake of the attachment storage accounting port (issue #968), so a use-case test can
/// put a business at, under or over its allowance without EF Core, SQLite or a plan record.
///
/// <para>It applies the real <see cref="AttachmentStorageQuotaPolicy"/> rather than a rule of its
/// own: what a use-case test needs from here is that an upload which does not fit is refused and
/// one that does is reserved, and the figures those answers rest on are the Domain's.</para>
/// </summary>
public sealed class FakeAttachmentQuotaStore : IAttachmentQuotaStore
{
    private readonly Dictionary<int, AttachmentUploadReservation> _reservations = [];
    private int _nextId = 1;

    /// <summary>What the business's recorded attachments already occupy.</summary>
    public long StoredBytes { get; set; }

    /// <summary>
    /// The size of the attachment a replacement would replace, which is what the real adapter reads
    /// from the record named in the request. <c>null</c> is "no attachment", so the whole upload is
    /// charged.
    /// </summary>
    public long? CurrentAttachmentBytes { get; set; }

    /// <summary>The attachment version the reservation records, as the real adapter reads it.</summary>
    public int? RecordVersion { get; set; }

    /// <summary>When reservations are taken, so their expiry is a value a test can predict.</summary>
    public DateTime NowUtc { get; set; } = new(2026, 10, 29, 9, 0, 0, DateTimeKind.Utc);

    /// <summary>Every request the guard made, in order.</summary>
    public List<AttachmentUploadRequest> Requests { get; } = [];

    /// <summary>Every reservation that was released, in order.</summary>
    public List<int> Released { get; } = [];

    /// <summary>The reservations that are still held.</summary>
    public IReadOnlyCollection<AttachmentUploadReservation> Outstanding => _reservations.Values;

    public Task<AttachmentStorageUsage> ReadUsageAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new AttachmentStorageUsage(
            StoredBytes, _reservations.Values.Sum(reservation => reservation.ChargeableBytes)));

    public Task<AttachmentUploadReservation?> ReserveAsync(
        AttachmentUploadRequest request, CancellationToken cancellationToken = default)
    {
        Requests.Add(request);

        var chargeable = AttachmentStorageQuotaPolicy.ChargeableBytes(
            request.UploadBytes, request.RecordId is null ? null : CurrentAttachmentBytes);
        var used = StoredBytes + _reservations.Values.Sum(reservation => reservation.ChargeableBytes);

        if (!AttachmentStorageQuotaPolicy.Fits(used, chargeable, request.LimitBytes))
            return Task.FromResult<AttachmentUploadReservation?>(null);

        var reservation = new AttachmentUploadReservation(
            _nextId++,
            request.Category,
            request.RecordId,
            request.UploadBytes,
            chargeable,
            request.LimitBytes,
            request.RecordId is null ? null : RecordVersion,
            AttachmentStorageQuotaPolicy.ReservationExpiryFor(NowUtc));
        _reservations[reservation.Id] = reservation;
        return Task.FromResult<AttachmentUploadReservation?>(reservation);
    }

    public Task<bool> ReleaseAsync(
        AttachmentUploadReservation reservation, CancellationToken cancellationToken = default)
    {
        Released.Add(reservation.Id);
        return Task.FromResult(_reservations.Remove(reservation.Id));
    }
}

/// <summary>
/// Builds the upload guard a use-case test needs: one that enforces a plan's allowance, and one
/// for the shipped state where nothing is enforced and every upload behaves as it always did.
/// </summary>
public static class FakeAttachmentQuota
{
    private static readonly DateTime Now = new(2026, 10, 29, 9, 0, 0, DateTimeKind.Utc);

    /// <summary>The shipped state: no plan is enforced, so nothing is reserved or refused.</summary>
    public static AttachmentQuotaGuard NotEnforced(IAttachmentQuotaStore? store = null) =>
        new(
            new GetEffectiveEntitlement(
                FakeSubscriptionEnforcementFlag.Disabled,
                new FakeSubscriptionGrandfatheringStore(),
                new FakeManualPlanGrantStore(),
                new FakeBillingSubscriptionStore(),
                new FakeClock(Now)),
            store ?? new FakeAttachmentQuotaStore());

    /// <summary>A business on <paramref name="plan"/>, with that plan's allowance enforced.</summary>
    public static AttachmentQuotaGuard Enforcing(
        IAttachmentQuotaStore store, SubscriptionPlan plan = SubscriptionPlan.Free) =>
        new(
            new GetEffectiveEntitlement(
                FakeSubscriptionEnforcementFlag.Enabled,
                FakeSubscriptionGrandfatheringStore.Completed(),
                new FakeManualPlanGrantStore(new ManualPlanGrantState(
                    plan,
                    ManualPlanGrantSource.Grandfathered,
                    new EntitlementPeriod(Now.AddDays(-1), null))),
                new FakeBillingSubscriptionStore(),
                new FakeClock(Now)),
            store);
}
