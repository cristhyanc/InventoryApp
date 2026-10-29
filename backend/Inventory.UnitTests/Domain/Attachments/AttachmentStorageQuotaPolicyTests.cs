using Inventory.Domain.Attachments;
using Xunit;

namespace InventoryApi.Tests.Domain.Attachments;

/// <summary>
/// The attachment storage quota rule (issue #968), row by row: what an upload is charged, whether
/// it fits, and which warning state a business is in.
/// </summary>
public class AttachmentStorageQuotaPolicyTests
{
    [Fact]
    public void A_new_attachment_is_charged_its_whole_size()
    {
        Assert.Equal(1_000, AttachmentStorageQuotaPolicy.ChargeableBytes(1_000, currentAttachmentBytes: null));
    }

    [Theory]
    [InlineData(1_000, 400, 600)]
    [InlineData(1_000, 1_000, 0)]
    [InlineData(400, 1_000, 0)]
    [InlineData(1_000, 0, 1_000)]
    public void A_replacement_is_charged_only_its_net_increase(
        long uploadBytes, long currentBytes, long expected)
    {
        Assert.Equal(expected, AttachmentStorageQuotaPolicy.ChargeableBytes(uploadBytes, currentBytes));
    }

    [Fact]
    public void An_upload_that_lands_exactly_on_the_limit_fits()
    {
        Assert.True(AttachmentStorageQuotaPolicy.Fits(usedBytes: 900, chargeableBytes: 100, limitBytes: 1_000));
    }

    [Fact]
    public void One_byte_past_the_limit_does_not_fit()
    {
        Assert.False(AttachmentStorageQuotaPolicy.Fits(usedBytes: 900, chargeableBytes: 101, limitBytes: 1_000));
    }

    /// <summary>
    /// A business already over its limit - which a downgrade can produce - still fits an upload
    /// that costs nothing, because a same-size or smaller replacement must keep working.
    /// </summary>
    [Fact]
    public void A_business_over_its_limit_still_fits_an_upload_that_costs_nothing()
    {
        Assert.True(AttachmentStorageQuotaPolicy.Fits(usedBytes: 5_000, chargeableBytes: 0, limitBytes: 1_000));
        Assert.False(AttachmentStorageQuotaPolicy.Fits(usedBytes: 5_000, chargeableBytes: 1, limitBytes: 1_000));
    }

    [Theory]
    [InlineData(0, AttachmentStorageWarningLevel.Normal)]
    [InlineData(799, AttachmentStorageWarningLevel.Normal)]
    [InlineData(800, AttachmentStorageWarningLevel.Warning)]
    [InlineData(999, AttachmentStorageWarningLevel.Warning)]
    [InlineData(1_000, AttachmentStorageWarningLevel.Full)]
    [InlineData(1_200, AttachmentStorageWarningLevel.Full)]
    public void The_warning_state_turns_at_eighty_percent_and_again_at_the_limit(
        long usedBytes, AttachmentStorageWarningLevel expected)
    {
        Assert.Equal(expected, AttachmentStorageQuotaPolicy.WarningLevelFor(usedBytes, limitBytes: 1_000));
    }

    /// <summary>
    /// The threshold is exact arithmetic over the real limits, not a percentage computed in
    /// floating point: 80% of 20 GB is a whole number of bytes and the state must turn on it.
    /// </summary>
    [Fact]
    public void The_eighty_percent_threshold_is_exact_at_a_real_plan_limit()
    {
        const long limit = 20_000_000_000L;

        Assert.Equal(
            AttachmentStorageWarningLevel.Normal,
            AttachmentStorageQuotaPolicy.WarningLevelFor(15_999_999_999L, limit));
        Assert.Equal(
            AttachmentStorageWarningLevel.Warning,
            AttachmentStorageQuotaPolicy.WarningLevelFor(16_000_000_000L, limit));
    }

    /// <summary>
    /// A reservation that outlives its upload has to stop counting on its own, or a crashed upload
    /// would hold storage for ever.
    /// </summary>
    [Fact]
    public void A_reservation_expires_thirty_minutes_after_it_is_taken()
    {
        var takenAt = new DateTime(2026, 10, 29, 9, 0, 0, DateTimeKind.Utc);

        Assert.Equal(TimeSpan.FromMinutes(30), AttachmentStorageQuotaPolicy.ReservationLifetime);
        Assert.Equal(
            takenAt.AddMinutes(30),
            AttachmentStorageQuotaPolicy.ReservationExpiryFor(takenAt));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(29, false)]
    [InlineData(30, true)]
    [InlineData(31, true)]
    public void A_reservation_is_expired_from_its_expiry_instant_inclusive(int minutesLater, bool expired)
    {
        var takenAt = new DateTime(2026, 10, 29, 9, 0, 0, DateTimeKind.Utc);
        var expiresAt = AttachmentStorageQuotaPolicy.ReservationExpiryFor(takenAt);

        Assert.Equal(expired, AttachmentStorageQuotaPolicy.HasExpired(expiresAt, takenAt.AddMinutes(minutesLater)));
    }
}
