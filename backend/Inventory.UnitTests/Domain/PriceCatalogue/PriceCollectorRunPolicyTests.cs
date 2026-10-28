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
}
