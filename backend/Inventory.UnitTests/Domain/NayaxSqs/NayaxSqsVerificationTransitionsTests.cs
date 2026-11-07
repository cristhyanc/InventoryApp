using Inventory.Domain.NayaxSqs;
using Xunit;

namespace InventoryApi.Tests.Domain.NayaxSqs;

/// <summary>
/// Which state a sale's <c>SqsVerification</c> may move to, and - far more importantly - which it may
/// not (issue #1104).
///
/// <para><strong>A conflict is terminal here.</strong> An authoritative contradiction between the
/// queue message and the business's own <c>lastSales</c> is the one thing this reconciliation cannot
/// resolve: later matching evidence does not unsay it, and only an Owner decision (#1105) moves a row
/// out of it. Every other transition exists because evidence can genuinely arrive in either
/// direction, and the evidence that caused each one is appended rather than replaced.</para>
///
/// <para><strong>A null state is not a state.</strong> A sale the queue never delivered - every row
/// the <c>lastSales</c> sync or an uploaded export imported - is not part of this model at all, and
/// nothing moves it into it.</para>
/// </summary>
public class NayaxSqsVerificationTransitionsTests
{
    public static TheoryData<NayaxSqsVerification, NayaxSqsVerification> Allowed() => new()
    {
        { NayaxSqsVerification.Unverified, NayaxSqsVerification.Verified },
        { NayaxSqsVerification.Unverified, NayaxSqsVerification.Conflict },
        { NayaxSqsVerification.Unverified, NayaxSqsVerification.NeedsInvestigation },
        { NayaxSqsVerification.NeedsInvestigation, NayaxSqsVerification.Verified },
        { NayaxSqsVerification.NeedsInvestigation, NayaxSqsVerification.Conflict },
        { NayaxSqsVerification.Verified, NayaxSqsVerification.Conflict },
        { NayaxSqsVerification.Verified, NayaxSqsVerification.NeedsInvestigation },
    };

    [Theory]
    [MemberData(nameof(Allowed))]
    public void An_allowed_transition_is_applied(
        NayaxSqsVerification from, NayaxSqsVerification to)
    {
        Assert.True(NayaxSqsVerificationTransitions.IsAllowed(from, to));
        Assert.Equal(to, NayaxSqsVerificationTransitions.Apply(from, to));
    }

    /// <summary>
    /// The exhaustive half: every ordered pair of declared states that is not in the allowlist above,
    /// and the null state beside them, is refused. A new value added to the enum therefore has to be
    /// decided here rather than inheriting whatever the fall-through happened to do.
    /// </summary>
    [Fact]
    public void No_other_transition_happens()
    {
        var states = Enum.GetValues<NayaxSqsVerification>();
        var allowed = Allowed()
            .Select(row => ((NayaxSqsVerification)row[0], (NayaxSqsVerification)row[1]))
            .ToHashSet();

        foreach (var from in states)
        {
            foreach (var to in states)
            {
                if (allowed.Contains((from, to)))
                {
                    continue;
                }

                Assert.False(
                    NayaxSqsVerificationTransitions.IsAllowed(from, to),
                    $"{from} must not move to {to}.");
                Assert.Equal(from, NayaxSqsVerificationTransitions.Apply(from, to));
            }
        }
    }

    [Fact]
    public void A_conflict_stays_a_conflict_whatever_arrives_later()
    {
        foreach (var observed in Enum.GetValues<NayaxSqsVerification>())
        {
            Assert.Equal(
                NayaxSqsVerification.Conflict,
                NayaxSqsVerificationTransitions.Apply(NayaxSqsVerification.Conflict, observed));
        }
    }

    [Fact]
    public void A_state_never_moves_to_itself()
    {
        foreach (var state in Enum.GetValues<NayaxSqsVerification>())
        {
            Assert.False(NayaxSqsVerificationTransitions.IsAllowed(state, state));
        }
    }

    /// <summary>
    /// A sale that did not arrive from the queue stays outside this model: nothing this
    /// reconciliation observes gives it a state.
    /// </summary>
    [Fact]
    public void A_sale_that_never_came_from_the_queue_is_never_given_a_state()
    {
        foreach (var observed in Enum.GetValues<NayaxSqsVerification>())
        {
            Assert.False(NayaxSqsVerificationTransitions.IsAllowed(null, observed));
            Assert.Null(NayaxSqsVerificationTransitions.Apply(null, observed));
        }
    }

    /// <summary>
    /// An observation that decided nothing - a comparison with no instant on one side - leaves the
    /// state exactly as it was, including a conflict and including nothing at all.
    /// </summary>
    [Fact]
    public void An_observation_that_decides_nothing_leaves_the_state_alone()
    {
        Assert.Null(NayaxSqsVerificationTransitions.Apply(null, null));

        foreach (var state in Enum.GetValues<NayaxSqsVerification>())
        {
            Assert.Equal(state, NayaxSqsVerificationTransitions.Apply(state, null));
        }
    }
}
