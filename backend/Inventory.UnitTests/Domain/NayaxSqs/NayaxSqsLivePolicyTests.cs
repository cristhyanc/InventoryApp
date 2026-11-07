using Inventory.Domain.NayaxSqs;
using Xunit;

namespace Inventory.UnitTests.Domain.NayaxSqs;

/// <summary>
/// The rule that decides whether a business's sales arrive from SQS reliably enough for the Dashboard
/// to stop calling Nayax on load (issue #1106, child of #1100).
///
/// <para>Every case here is a case the issue asks for by name, because what is being decided is when
/// this application may claim "live from Nayax" about figures it did not read: an enabled sender, a
/// reader that is demonstrably polling and not failing, and a reconciliation that has fully succeeded
/// recently enough. Anything else is not live, and the reason says which of the three it was.</para>
/// </summary>
public class NayaxSqsLivePolicyTests
{
    private static readonly DateTime Now = new(2026, 11, 7, 9, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// The state of a business whose queue is working: an enabled sender, a poll seconds ago with no
    /// failures, and an hourly reconciliation that succeeded in full half an hour ago.
    /// </summary>
    private static NayaxSqsLiveState Healthy() =>
        new(
            SenderRecorded: true,
            IsEnabled: true,
            LastSuccessfulPollUtc: Now.AddSeconds(-20),
            ConsecutivePollFailures: 0,
            LastReconciliationSucceededAtUtc: Now.AddMinutes(-30));

    /// <summary>
    /// <strong>A quiet queue with a healthy reader is live.</strong> Time since the last message is
    /// deliberately not a condition: a fleet that sold nothing overnight is not a failure, and
    /// treating it as one would make every quiet business call Nayax for every machine at breakfast.
    /// </summary>
    [Fact]
    public void A_healthy_quiet_queue_is_live()
    {
        Assert.Null(NayaxSqsLivePolicy.FindNotLiveReason(Healthy(), Now));
    }

    /// <summary>
    /// No sender at all, which is one answer for two stored states on purpose: a business no row has
    /// ever existed for, and one whose sender id a Super Admin cleared - "Remove" keeps the row and
    /// its audit history (issue #1101), and a mapping with nothing to match on attributes nothing.
    /// </summary>
    [Fact]
    public void A_business_with_no_recorded_sender_is_not_live()
    {
        var state = Healthy() with { SenderRecorded = false, IsEnabled = false };

        Assert.Equal(NayaxSqsNotLiveReason.NoSender, NayaxSqsLivePolicy.FindNotLiveReason(state, Now));
    }

    [Fact]
    public void A_recorded_but_disabled_sender_is_not_live()
    {
        var state = Healthy() with { IsEnabled = false };

        Assert.Equal(
            NayaxSqsNotLiveReason.SenderDisabled, NayaxSqsLivePolicy.FindNotLiveReason(state, Now));
    }

    /// <summary>
    /// After a restart nothing has polled yet, so the record is empty - a true statement rather than
    /// a recovered one - and the business is not live until the first successful poll.
    /// </summary>
    [Fact]
    public void A_reader_that_has_never_polled_is_not_live()
    {
        var state = Healthy() with { LastSuccessfulPollUtc = null };

        Assert.Equal(
            NayaxSqsNotLiveReason.ReaderNotPolling, NayaxSqsLivePolicy.FindNotLiveReason(state, Now));
    }

    /// <summary>
    /// The issue's own case: three minutes without a poll that returned at all is a reader nobody can
    /// say is working, whatever it managed before.
    /// </summary>
    [Fact]
    public void A_poll_three_minutes_old_is_not_live()
    {
        var state = Healthy() with { LastSuccessfulPollUtc = Now.AddMinutes(-3) };

        Assert.Equal(
            NayaxSqsNotLiveReason.ReaderNotPolling, NayaxSqsLivePolicy.FindNotLiveReason(state, Now));
    }

    /// <summary>
    /// The boundary of the two-minute window, both sides of it: a second inside is live and the
    /// window's own edge is not, so "less than two minutes old" means exactly that.
    /// </summary>
    [Fact]
    public void The_poll_window_is_two_minutes_exclusive()
    {
        var inside = Healthy() with
        {
            LastSuccessfulPollUtc = Now - NayaxSqsLivePolicy.PollWindow + TimeSpan.FromSeconds(1),
        };
        var edge = Healthy() with { LastSuccessfulPollUtc = Now - NayaxSqsLivePolicy.PollWindow };

        Assert.Null(NayaxSqsLivePolicy.FindNotLiveReason(inside, Now));
        Assert.Equal(
            NayaxSqsNotLiveReason.ReaderNotPolling, NayaxSqsLivePolicy.FindNotLiveReason(edge, Now));
    }

    /// <summary>
    /// <strong>One failure is enough.</strong> The health record resets the count on the next
    /// successful poll, so a non-zero count is a current statement that the queue is not answering -
    /// and a Dashboard that skipped its own read on that evidence would be showing sales nothing is
    /// delivering.
    /// </summary>
    [Fact]
    public void One_consecutive_poll_failure_is_not_live()
    {
        var state = Healthy() with { ConsecutivePollFailures = 1 };

        Assert.Equal(
            NayaxSqsNotLiveReason.ReaderFailing, NayaxSqsLivePolicy.FindNotLiveReason(state, Now));
    }

    /// <summary>
    /// A reconciliation that has never fully succeeded is stale rather than live: the column is
    /// written only by a run that read every machine, committed, recorded every comparison and
    /// drained its rebuilds (issue #1104), so "never" is "nothing has verified this business's queue".
    /// </summary>
    [Fact]
    public void A_business_whose_reconciliation_has_never_succeeded_is_not_live()
    {
        var state = Healthy() with { LastReconciliationSucceededAtUtc = null };

        Assert.Equal(
            NayaxSqsNotLiveReason.ReconciliationStale,
            NayaxSqsLivePolicy.FindNotLiveReason(state, Now));
    }

    /// <summary>
    /// The 150-minute limit, at the two instants the issue names: 149 minutes is live and 151 is not.
    /// </summary>
    [Theory]
    [InlineData(149, true)]
    [InlineData(151, false)]
    public void The_reconciliation_limit_is_one_hundred_and_fifty_minutes(int minutesAgo, bool isLive)
    {
        var state = Healthy() with { LastReconciliationSucceededAtUtc = Now.AddMinutes(-minutesAgo) };

        var reason = NayaxSqsLivePolicy.FindNotLiveReason(state, Now);

        if (isLive)
        {
            Assert.Null(reason);
        }
        else
        {
            Assert.Equal(NayaxSqsNotLiveReason.ReconciliationStale, reason);
        }
    }

    /// <summary>
    /// <strong>The issue's worked example, from the run's own instant.</strong> A reconciliation
    /// succeeds at minute 0; the runs due at 60 and 120 are both missed. The business stays live until
    /// minute 150 and goes stale then - so 150 minutes is a <em>30-minute grace after the second
    /// missed run</em>, and the business does not go stale the moment that second run is missed. (A
    /// 90-minute limit would have tolerated no missed run at all: the run due at 60 being missed
    /// leaves the last success already 90 minutes old.)
    /// </summary>
    [Theory]
    [InlineData(0, true)]
    [InlineData(60, true)]
    [InlineData(90, true)]
    [InlineData(120, true)]
    [InlineData(149, true)]
    [InlineData(150, false)]
    [InlineData(151, false)]
    public void The_worked_example_of_two_missed_hourly_runs(int minutesSinceSuccess, bool isLive)
    {
        var succeededAt = Now;
        var state = Healthy() with { LastReconciliationSucceededAtUtc = succeededAt };
        var asOf = succeededAt.AddMinutes(minutesSinceSuccess);

        // The poll stays current throughout: this example is about the reconciliation alone.
        var atThatMoment = state with { LastSuccessfulPollUtc = asOf.AddSeconds(-10) };

        Assert.Equal(isLive, NayaxSqsLivePolicy.FindNotLiveReason(atThatMoment, asOf) is null);
    }

    /// <summary>
    /// The reasons are reported in the order the conditions are stated, so a business with nothing
    /// recorded is told that rather than being told its reader is failing: there is no sender for the
    /// reader's state to be about.
    /// </summary>
    [Fact]
    public void The_sender_is_decided_before_the_readers_state()
    {
        var state = new NayaxSqsLiveState(
            SenderRecorded: false,
            IsEnabled: false,
            LastSuccessfulPollUtc: null,
            ConsecutivePollFailures: 7,
            LastReconciliationSucceededAtUtc: null);

        Assert.Equal(NayaxSqsNotLiveReason.NoSender, NayaxSqsLivePolicy.FindNotLiveReason(state, Now));
    }

    /// <summary>
    /// A last-poll instant in the future is not evidence of anything - only a clock moved backwards
    /// produces one - so it is read as "not polling" rather than as a poll that is comfortably
    /// recent. It is the same fail-closed direction <c>NayaxSalesSyncFreshness</c> takes for its own
    /// recorded instant.
    /// </summary>
    [Fact]
    public void A_poll_instant_in_the_future_is_not_live()
    {
        var state = Healthy() with { LastSuccessfulPollUtc = Now.AddMinutes(5) };

        Assert.Equal(
            NayaxSqsNotLiveReason.ReaderNotPolling, NayaxSqsLivePolicy.FindNotLiveReason(state, Now));
    }

    /// <summary>And the same for a reconciliation instant in the future.</summary>
    [Fact]
    public void A_reconciliation_instant_in_the_future_is_not_live()
    {
        var state = Healthy() with { LastReconciliationSucceededAtUtc = Now.AddMinutes(5) };

        Assert.Equal(
            NayaxSqsNotLiveReason.ReconciliationStale,
            NayaxSqsLivePolicy.FindNotLiveReason(state, Now));
    }

    /// <summary>
    /// The two thresholds are the owner's numbers and are stated once, here, so the use case, the
    /// documentation and these tests cannot drift apart.
    /// </summary>
    [Fact]
    public void The_thresholds_are_two_and_one_hundred_and_fifty_minutes()
    {
        Assert.Equal(TimeSpan.FromMinutes(2), NayaxSqsLivePolicy.PollWindow);
        Assert.Equal(TimeSpan.FromMinutes(150), NayaxSqsLivePolicy.ReconciliationWindow);
    }
}
