using Inventory.Domain.Nayax;
using Xunit;

namespace InventoryApi.Tests.Domain.Nayax;

/// <summary>
/// The rule that turns one completed check run into what it concluded (issue #537), with the machine
/// list as the credential gate since issue #1310.
///
/// Every case of the agreed table is pinned here, including the ones the issue did not spell out,
/// because the fail-closed reading of those is a decision: a machine list that neither passed nor
/// produced Nayax's documented <c>401</c> verdict concludes <em>nothing</em>, so the stored status
/// is left exactly as it was. The alternative - guessing - would either break a working connection
/// or mark a dead token usable.
/// </summary>
public class NayaxConnectionCheckOutcomePolicyTests
{
    [Fact]
    public void A_run_whose_every_check_passed_is_ready()
    {
        var outcome = NayaxConnectionCheckOutcomePolicy.Classify(
            NayaxConnectionCheckStatus.Passed,
            machineListFailure: null,
            anyOtherCheckFailed: false);

        Assert.Equal(NayaxConnectionCheckOutcome.Ready, outcome);
        Assert.Equal(NayaxConnectionStatus.Ready, NayaxConnectionCheckOutcomePolicy.ConcludedStatus(outcome));
        Assert.True(NayaxConnectionCheckOutcomePolicy.MayBeSaved(outcome));
    }

    /// <summary>
    /// A token that reads the machine list but lacks another permission is a real, usable credential
    /// with a gap, which is the ordinary case this whole feature exists for.
    /// </summary>
    [Fact]
    public void A_machine_list_that_passed_beside_another_failed_check_is_pending_permissions()
    {
        var outcome = NayaxConnectionCheckOutcomePolicy.Classify(
            NayaxConnectionCheckStatus.Passed,
            machineListFailure: null,
            anyOtherCheckFailed: true);

        Assert.Equal(NayaxConnectionCheckOutcome.PendingPermissions, outcome);
        Assert.Equal(
            NayaxConnectionStatus.PendingPermissions,
            NayaxConnectionCheckOutcomePolicy.ConcludedStatus(outcome));
        Assert.True(NayaxConnectionCheckOutcomePolicy.MayBeSaved(outcome));
    }

    /// <summary>
    /// Nayax documents a <c>401</c> as "the token is invalid or has expired", so it is the one
    /// answer that judges the credentials and the one that moves a connection to
    /// <see cref="NayaxConnectionStatus.NeedsAttention"/>.
    /// </summary>
    [Fact]
    public void A_machine_list_refused_with_an_invalid_token_rejects_the_token()
    {
        var outcome = NayaxConnectionCheckOutcomePolicy.Classify(
            NayaxConnectionCheckStatus.Failed,
            NayaxConnectionCheckFailure.InvalidOrExpiredToken,
            anyOtherCheckFailed: false);

        Assert.Equal(NayaxConnectionCheckOutcome.TokenRejected, outcome);
        Assert.Equal(
            NayaxConnectionStatus.NeedsAttention,
            NayaxConnectionCheckOutcomePolicy.ConcludedStatus(outcome));
        Assert.False(NayaxConnectionCheckOutcomePolicy.MayBeSaved(outcome));
    }

    /// <summary>
    /// Nothing was learned about the credentials in any of these, so nothing is concluded about
    /// them: Nayax was unreachable, answered something nobody interprets, refused the machine list
    /// for lack of a permission, or the line never ran. In each case the stored status stays - and
    /// the refused machine list is the case issue #1310 kept apart from a dead token on purpose,
    /// because a permission refusal names an endpoint rather than judging the credentials.
    /// </summary>
    [Theory]
    [InlineData(NayaxConnectionCheckStatus.Failed, NayaxConnectionCheckFailure.NayaxUnavailable)]
    [InlineData(NayaxConnectionCheckStatus.Failed, NayaxConnectionCheckFailure.UnexpectedResponse)]
    [InlineData(NayaxConnectionCheckStatus.Failed, NayaxConnectionCheckFailure.PermissionNotGranted)]
    [InlineData(NayaxConnectionCheckStatus.Failed, NayaxConnectionCheckFailure.OperatorIdMismatch)]
    [InlineData(NayaxConnectionCheckStatus.NotChecked, null)]
    public void A_machine_list_that_produced_no_verdict_concludes_nothing(
        NayaxConnectionCheckStatus machineList,
        NayaxConnectionCheckFailure? failure)
    {
        var outcome = NayaxConnectionCheckOutcomePolicy.Classify(machineList, failure, anyOtherCheckFailed: false);

        Assert.Equal(NayaxConnectionCheckOutcome.Inconclusive, outcome);
        Assert.Null(NayaxConnectionCheckOutcomePolicy.ConcludedStatus(outcome));
        Assert.False(NayaxConnectionCheckOutcomePolicy.MayBeSaved(outcome));
    }

    /// <summary>
    /// A later check failing cannot turn a dead token into a mere permission gap, and cannot turn an
    /// unanswered machine list into one either: the machine list decides first.
    /// </summary>
    [Theory]
    [InlineData(NayaxConnectionCheckFailure.InvalidOrExpiredToken, NayaxConnectionCheckOutcome.TokenRejected)]
    [InlineData(NayaxConnectionCheckFailure.NayaxUnavailable, NayaxConnectionCheckOutcome.Inconclusive)]
    public void A_failed_machine_list_decides_the_outcome_whatever_else_failed(
        NayaxConnectionCheckFailure failure,
        NayaxConnectionCheckOutcome expected)
    {
        Assert.Equal(
            expected,
            NayaxConnectionCheckOutcomePolicy.Classify(
                NayaxConnectionCheckStatus.Failed,
                failure,
                anyOtherCheckFailed: true));
    }

    /// <summary>
    /// An undeclared value is refused rather than defaulted. An enum is a compile-time constraint,
    /// and a status read back from a stored report could be anything.
    /// </summary>
    [Fact]
    public void An_undeclared_machine_list_status_concludes_nothing_and_cannot_be_saved()
    {
        var outcome = NayaxConnectionCheckOutcomePolicy.Classify(
            (NayaxConnectionCheckStatus)999,
            machineListFailure: null,
            anyOtherCheckFailed: false);

        Assert.Equal(NayaxConnectionCheckOutcome.Inconclusive, outcome);
        Assert.Null(NayaxConnectionCheckOutcomePolicy.ConcludedStatus(outcome));
        Assert.False(NayaxConnectionCheckOutcomePolicy.MayBeSaved(outcome));
    }

    [Fact]
    public void An_undeclared_outcome_concludes_no_status_and_cannot_be_saved()
    {
        Assert.Null(NayaxConnectionCheckOutcomePolicy.ConcludedStatus((NayaxConnectionCheckOutcome)999));
        Assert.False(NayaxConnectionCheckOutcomePolicy.MayBeSaved((NayaxConnectionCheckOutcome)999));
    }
}
