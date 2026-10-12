using Inventory.Domain.Nayax;
using Xunit;

namespace InventoryApi.Tests.Domain.Nayax;

/// <summary>
/// The connection-check policy of issue #536, answer by answer.
///
/// What an operator is told to fix comes from this mapping, so it is pinned exhaustively rather
/// than sampled: a token that lacks one permission and a token that has expired need completely
/// different actions, and the whole point of the test is that the right one is named. The vocabulary
/// tests at the end fail if a check, a status or a cause is added without a decision being made
/// here.
/// </summary>
public class NayaxConnectionCheckPolicyTests
{
    [Theory]
    [InlineData(NayaxConnectionCheck.SignIn)]
    [InlineData(NayaxConnectionCheck.Machines)]
    [InlineData(NayaxConnectionCheck.Machine)]
    [InlineData(NayaxConnectionCheck.ProductCatalogue)]
    [InlineData(NayaxConnectionCheck.ProductGroups)]
    [InlineData(NayaxConnectionCheck.MachineProducts)]
    [InlineData(NayaxConnectionCheck.MachineLastSales)]
    [InlineData(NayaxConnectionCheck.MachineLastAlerts)]
    public void A_401_is_an_invalid_or_expired_token_on_every_check(NayaxConnectionCheck check)
    {
        Assert.Equal(
            NayaxConnectionCheckFailure.InvalidOrExpiredToken,
            NayaxConnectionCheckPolicy.Classify(check, 401));
    }

    [Theory]
    [InlineData(NayaxConnectionCheck.SignIn)]
    [InlineData(NayaxConnectionCheck.Machines)]
    [InlineData(NayaxConnectionCheck.Machine)]
    [InlineData(NayaxConnectionCheck.ProductCatalogue)]
    [InlineData(NayaxConnectionCheck.ProductGroups)]
    [InlineData(NayaxConnectionCheck.MachineProducts)]
    [InlineData(NayaxConnectionCheck.MachineLastSales)]
    [InlineData(NayaxConnectionCheck.MachineLastAlerts)]
    public void A_403_is_a_missing_permission_on_every_check(NayaxConnectionCheck check)
    {
        Assert.Equal(
            NayaxConnectionCheckFailure.PermissionNotGranted,
            NayaxConnectionCheckPolicy.Classify(check, 403));
    }

    /// <summary>
    /// Only the two operator-scoped endpoints carry the submitted operator id in their path, so only
    /// they can answer "that operator is not one this token covers".
    /// </summary>
    [Theory]
    [InlineData(NayaxConnectionCheck.ProductCatalogue)]
    [InlineData(NayaxConnectionCheck.ProductGroups)]
    public void A_404_on_an_operator_scoped_check_is_an_operator_id_mismatch(NayaxConnectionCheck check)
    {
        Assert.Equal(
            NayaxConnectionCheckFailure.OperatorIdMismatch,
            NayaxConnectionCheckPolicy.Classify(check, 404));
    }

    /// <summary>
    /// A <c>404</c> anywhere else says nothing about the operator id - the path carried a machine id
    /// or no id at all - so it stays an answer nobody has interpreted. Reporting it as a mismatch
    /// would send an operator to re-check a correct operator id.
    /// </summary>
    [Theory]
    [InlineData(NayaxConnectionCheck.SignIn)]
    [InlineData(NayaxConnectionCheck.Machines)]
    [InlineData(NayaxConnectionCheck.Machine)]
    [InlineData(NayaxConnectionCheck.MachineProducts)]
    [InlineData(NayaxConnectionCheck.MachineLastSales)]
    [InlineData(NayaxConnectionCheck.MachineLastAlerts)]
    public void A_404_on_a_check_that_carries_no_operator_id_is_an_unexpected_response(
        NayaxConnectionCheck check)
    {
        Assert.Equal(
            NayaxConnectionCheckFailure.UnexpectedResponse,
            NayaxConnectionCheckPolicy.Classify(check, 404));
    }

    [Theory]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    [InlineData(504)]
    [InlineData(599)]
    public void A_5xx_is_nayax_being_unavailable(int statusCode)
    {
        Assert.Equal(
            NayaxConnectionCheckFailure.NayaxUnavailable,
            NayaxConnectionCheckPolicy.Classify(NayaxConnectionCheck.SignIn, statusCode));
    }

    /// <summary>
    /// No answer at all - a timeout, a dropped connection, an unreadable body - is Nayax being
    /// unavailable and never a verdict on the credentials: nothing about them was learned.
    /// </summary>
    [Fact]
    public void No_answer_at_all_is_nayax_being_unavailable()
    {
        Assert.Equal(
            NayaxConnectionCheckFailure.NayaxUnavailable,
            NayaxConnectionCheckPolicy.Classify(NayaxConnectionCheck.SignIn, null));
    }

    /// <summary>
    /// The statuses Nayax documents are the four above. A <c>400</c>, a <c>429</c> - the rate limit
    /// of the app-token troubleshooting page - or anything else is reported as unexpected rather
    /// than folded into one of them.
    /// </summary>
    [Theory]
    [InlineData(400)]
    [InlineData(402)]
    [InlineData(405)]
    [InlineData(409)]
    [InlineData(418)]
    [InlineData(429)]
    [InlineData(600)]
    [InlineData(0)]
    public void Any_other_answer_is_an_unexpected_response(int statusCode)
    {
        Assert.Equal(
            NayaxConnectionCheckFailure.UnexpectedResponse,
            NayaxConnectionCheckPolicy.Classify(NayaxConnectionCheck.Machines, statusCode));
    }

    [Theory]
    [InlineData(NayaxConnectionCheck.Machine)]
    [InlineData(NayaxConnectionCheck.MachineProducts)]
    [InlineData(NayaxConnectionCheck.MachineLastSales)]
    [InlineData(NayaxConnectionCheck.MachineLastAlerts)]
    public void The_machine_scoped_checks_need_a_machine(NayaxConnectionCheck check)
    {
        Assert.True(NayaxConnectionCheckPolicy.RequiresMachine(check));
    }

    [Theory]
    [InlineData(NayaxConnectionCheck.SignIn)]
    [InlineData(NayaxConnectionCheck.Machines)]
    [InlineData(NayaxConnectionCheck.ProductCatalogue)]
    [InlineData(NayaxConnectionCheck.ProductGroups)]
    public void The_other_checks_do_not(NayaxConnectionCheck check)
    {
        Assert.False(NayaxConnectionCheckPolicy.RequiresMachine(check));
    }

    [Theory]
    [InlineData(NayaxConnectionCheck.ProductCatalogue, true)]
    [InlineData(NayaxConnectionCheck.ProductGroups, true)]
    [InlineData(NayaxConnectionCheck.SignIn, false)]
    [InlineData(NayaxConnectionCheck.Machines, false)]
    [InlineData(NayaxConnectionCheck.Machine, false)]
    [InlineData(NayaxConnectionCheck.MachineProducts, false)]
    [InlineData(NayaxConnectionCheck.MachineLastSales, false)]
    [InlineData(NayaxConnectionCheck.MachineLastAlerts, false)]
    public void Only_the_product_checks_are_operator_scoped(NayaxConnectionCheck check, bool expected)
    {
        Assert.Equal(expected, NayaxConnectionCheckPolicy.IsOperatorScoped(check));
    }

    /// <summary>
    /// The reported order is the declared order, and it is complete: a client renders the lines as
    /// given, so a reordering or an omission is a contract change and has to be a deliberate one.
    /// </summary>
    [Fact]
    public void Every_declared_check_is_reported_in_declaration_order()
    {
        Assert.Equal(
            new[]
            {
                NayaxConnectionCheck.SignIn,
                NayaxConnectionCheck.Machines,
                NayaxConnectionCheck.Machine,
                NayaxConnectionCheck.ProductCatalogue,
                NayaxConnectionCheck.ProductGroups,
                NayaxConnectionCheck.MachineProducts,
                NayaxConnectionCheck.MachineLastSales,
                NayaxConnectionCheck.MachineLastAlerts,
            },
            NayaxConnectionCheckPolicy.All);
    }

    /// <summary>
    /// Fails if a status or a cause is added to either vocabulary without a decision being made in
    /// this file, so the mapping can never silently fall behind the vocabulary it is the mapping
    /// for.
    /// </summary>
    [Fact]
    public void The_status_and_failure_vocabularies_are_the_ones_this_test_covers()
    {
        Assert.Equal(
            new[]
            {
                NayaxConnectionCheckStatus.Passed,
                NayaxConnectionCheckStatus.Failed,
                NayaxConnectionCheckStatus.NotChecked,
            },
            Enum.GetValues<NayaxConnectionCheckStatus>());

        Assert.Equal(
            new[]
            {
                NayaxConnectionCheckFailure.InvalidOrExpiredToken,
                NayaxConnectionCheckFailure.PermissionNotGranted,
                NayaxConnectionCheckFailure.OperatorIdMismatch,
                NayaxConnectionCheckFailure.NayaxUnavailable,
                NayaxConnectionCheckFailure.UnexpectedResponse,
            },
            Enum.GetValues<NayaxConnectionCheckFailure>());
    }
}
