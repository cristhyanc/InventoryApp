using Inventory.Domain.Exceptions;
using Inventory.Domain.PriceCatalogue;
using Xunit;

namespace Inventory.UnitTests.Domain.PriceCatalogue;

/// <summary>
/// The consistency rules of one price collector run (issue #925): its status against its end instant,
/// its end against its start, and its counts.
/// </summary>
public class PriceCollectorRunPolicyTests
{
    private static readonly DateTime Started = new(2026, 10, 28, 5, 0, 0, DateTimeKind.Utc);

    private static Exception? Refusal(
        PriceCollectorRunStatus status,
        DateTime? completedAtUtc,
        int listingsChecked = 10,
        int observationsRecorded = 9,
        int failures = 1) =>
        Record.Exception(
            () => PriceCollectorRunPolicy.RequireConsistentRun(
                status,
                Started,
                completedAtUtc,
                listingsChecked,
                observationsRecorded,
                failures));

    /// <summary>
    /// A run that is still going is the only one without an end instant, which is what keeps "still
    /// running" from reading as "finished and we lost the time".
    /// </summary>
    [Theory]
    [InlineData(PriceCollectorRunStatus.Running, false)]
    [InlineData(PriceCollectorRunStatus.Succeeded, true)]
    [InlineData(PriceCollectorRunStatus.PartiallySucceeded, true)]
    [InlineData(PriceCollectorRunStatus.Failed, true)]
    public void Exactly_the_finished_statuses_record_an_end_instant(
        PriceCollectorRunStatus status,
        bool finished)
    {
        Assert.Equal(finished, PriceCollectorRunPolicy.IsFinished(status));
        Assert.Null(Refusal(status, finished ? Started.AddMinutes(20) : null));

        var refusal = Assert.IsType<DomainValidationException>(
            Refusal(status, finished ? null : Started.AddMinutes(20)));
        Assert.Equal(PriceCollectorRunPolicy.EndInstantMismatchMessage, refusal.Message);
    }

    [Fact]
    public void A_run_cannot_end_before_it_started()
    {
        var refusal = Assert.IsType<DomainValidationException>(
            Refusal(PriceCollectorRunStatus.Succeeded, Started.AddSeconds(-1)));

        Assert.Equal(PriceCollectorRunPolicy.EndBeforeStartMessage, refusal.Message);
    }

    /// <summary>A run that started and ended in the same instant is accepted: it did nothing, quickly.</summary>
    [Fact]
    public void A_run_that_ends_at_its_start_instant_is_accepted() =>
        Assert.Null(
            Refusal(
                PriceCollectorRunStatus.Succeeded,
                Started,
                listingsChecked: 0,
                observationsRecorded: 0,
                failures: 0));

    [Theory]
    [InlineData(-1, 0, 0)]
    [InlineData(0, -1, 0)]
    [InlineData(0, 0, -1)]
    public void A_negative_count_is_refused(int listingsChecked, int recorded, int failures)
    {
        var refusal = Assert.IsType<DomainValidationException>(
            Refusal(
                PriceCollectorRunStatus.Succeeded,
                Started.AddMinutes(5),
                listingsChecked,
                recorded,
                failures));

        Assert.Equal(PriceCollectorRunPolicy.NegativeCountMessage, refusal.Message);
    }

    /// <summary>
    /// A status outside the declared vocabulary is refused: it arrives from a JSON body as an
    /// arbitrary integer, so the enum is a compile-time constraint and not a validation.
    /// </summary>
    [Fact]
    public void A_status_outside_the_vocabulary_is_refused()
    {
        Assert.False(PriceCollectorRunPolicy.IsDeclaredStatus((PriceCollectorRunStatus)0));

        var refusal = Assert.IsType<DomainValidationException>(
            Refusal((PriceCollectorRunStatus)0, Started.AddMinutes(5)));
        Assert.Equal(PriceCollectorRunPolicy.UnknownStatusMessage, refusal.Message);
    }

    /// <summary>
    /// An expired run is a <em>finished</em> run (issue #924), so it records an end instant like every
    /// other finished status and a running one still records none. That is what keeps "the collector
    /// never reported" a recorded outcome rather than a run stuck open forever.
    /// </summary>
    [Fact]
    public void An_expired_run_is_finished_and_records_when_it_ended()
    {
        Assert.True(PriceCollectorRunPolicy.IsFinished(PriceCollectorRunStatus.Expired));
        Assert.Null(Refusal(PriceCollectorRunStatus.Expired, Started.AddHours(6)));

        var refusal = Assert.IsType<DomainValidationException>(
            Refusal(PriceCollectorRunStatus.Expired, completedAtUtc: null));
        Assert.Equal(PriceCollectorRunPolicy.EndInstantMismatchMessage, refusal.Message);
    }

    /// <summary>
    /// An expired run is its own outcome, deliberately not a failed one: a failed run reported
    /// failing, and an expired run reported nothing at all.
    /// </summary>
    [Fact]
    public void The_declared_statuses_are_the_five_the_two_issues_name() =>
        Assert.Equal(
            ["Running", "Succeeded", "PartiallySucceeded", "Failed", "Expired"],
            Enum.GetNames<PriceCollectorRunStatus>());

    #region The run lifetime (issue #924)

    /// <summary>
    /// The owner's default is six hours, and the expiry instant is that lifetime after the run was
    /// issued.
    /// </summary>
    [Fact]
    public void A_run_expires_its_configured_lifetime_after_it_was_issued()
    {
        Assert.Equal(6, PriceCollectorRunPolicy.DefaultExpiryHours);
        Assert.Equal(
            Started.AddHours(6),
            PriceCollectorRunPolicy.ExpiresAt(Started, PriceCollectorRunPolicy.DefaultExpiryHours));
    }

    /// <summary>
    /// The expiry instant belongs to the expired side, the way the freshness limit belongs to the
    /// stale side: "six hours" answers "how long may this stay open", so a run at exactly its expiry
    /// has expired.
    /// </summary>
    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    public void The_expiry_instant_belongs_to_the_expired_side(int minutesPastExpiry, bool hasExpired)
    {
        var expiresAt = PriceCollectorRunPolicy.ExpiresAt(Started, PriceCollectorRunPolicy.DefaultExpiryHours);

        Assert.Equal(
            hasExpired,
            PriceCollectorRunPolicy.HasExpired(expiresAt, expiresAt.AddMinutes(minutesPastExpiry)));
    }

    /// <summary>
    /// A lifetime outside the accepted range is refused rather than resolved. Zero in particular is
    /// refused rather than read as "never expires" or "expires immediately": both are silently wrong,
    /// and which one a reader picked would decide whether a collector ever gets any work.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(PriceCollectorRunPolicy.MaxExpiryHours + 1)]
    public void A_lifetime_outside_the_accepted_range_is_refused(int expiryHours)
    {
        Assert.False(PriceCollectorRunPolicy.IsAcceptedExpiry(expiryHours));

        var refusal = Assert.Throws<DomainValidationException>(
            () => PriceCollectorRunPolicy.RequireExpiryHours(expiryHours));
        Assert.Equal(PriceCollectorRunPolicy.ExpiryOutOfRangeMessage, refusal.Message);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(PriceCollectorRunPolicy.DefaultExpiryHours)]
    [InlineData(PriceCollectorRunPolicy.MaxExpiryHours)]
    public void A_lifetime_inside_the_accepted_range_is_kept(int expiryHours) =>
        Assert.Equal(expiryHours, PriceCollectorRunPolicy.RequireExpiryHours(expiryHours));

    #endregion
}
