using System.Text.Json;
using Inventory.Application.Nayax;
using Inventory.Domain.Nayax;
using Xunit;

namespace InventoryApi.Tests.Application.Nayax;

/// <summary>
/// What a completed check run reports about itself (issues #536, #537), and in particular what it
/// reports about a line this build does not declare (issue #1310).
///
/// <para>A report is serialized into the business's test draft and read back by the save, so the
/// retired <c>GET devices</c> sign-in probe survives in storage as the number it was - long after
/// the check itself stopped existing. Every answer derived from a report therefore ignores a line
/// whose check is not declared: it is evidence about nothing, because this build neither ran it nor
/// knows what it meant. The alternative is worse in both directions - a refusal of a permission this
/// application no longer asks for would decide a stored status, and a pass of a probe this
/// application no longer makes would vouch for a token nothing has read a machine with.</para>
/// </summary>
public class NayaxConnectionCheckReportTests
{
    /// <summary>The retired <c>SignIn</c> check: the number the enum reserves and no longer names.</summary>
    private const NayaxConnectionCheck RetiredSignIn = (NayaxConnectionCheck)1;

    [Fact]
    public void A_run_of_this_build_reports_every_line_as_declared()
    {
        var report = AllPassed();

        Assert.Equal(report.Lines, report.DeclaredLines);
        Assert.True(report.NothingFailed);
        Assert.Equal(NayaxConnectionCheckOutcome.Ready, report.Outcome);
    }

    /// <summary>
    /// The case the issue exists for, at the report level: the only refusal is of the retired
    /// device-inventory probe, so nothing this application depends on failed. The run reads as
    /// <c>Ready</c> and the summary says nothing failed.
    /// </summary>
    [Fact]
    public void A_retired_lines_refusal_is_not_a_failure_and_concludes_ready()
    {
        var report = WithRetiredLine(
            NayaxConnectionCheckLine.Failed(RetiredSignIn, NayaxConnectionCheckFailure.PermissionNotGranted));

        Assert.DoesNotContain(RetiredSignIn, report.DeclaredLines.Select(line => line.Check));
        Assert.Equal(NayaxConnectionCheckPolicy.All, report.DeclaredLines.Select(line => line.Check).ToArray());
        Assert.True(report.NothingFailed);
        Assert.Equal(NayaxConnectionCheckOutcome.Ready, report.Outcome);
    }

    /// <summary>
    /// And the other direction: a retired line that passed cannot stand in for the machine list the
    /// credentials are now judged by.
    /// </summary>
    [Fact]
    public void A_retired_line_that_passed_cannot_stand_in_for_the_machine_list()
    {
        var report = new NayaxConnectionCheckReport(
            [
                NayaxConnectionCheckLine.Passed(RetiredSignIn),
                NayaxConnectionCheckLine.Failed(
                    NayaxConnectionCheck.Machines,
                    NayaxConnectionCheckFailure.PermissionNotGranted),
            ],
            MachineCount: null);

        Assert.False(report.NothingFailed);
        Assert.Equal(NayaxConnectionCheckOutcome.Inconclusive, report.Outcome);
        Assert.False(NayaxConnectionCheckOutcomePolicy.MayBeSaved(report.Outcome));
    }

    /// <summary>
    /// A business with no machines still reports its machine checks as pending with a retired line
    /// beside them: that flag is about the machine list and the machine-scoped lines, and an
    /// undeclared line is neither.
    /// </summary>
    [Fact]
    public void A_retired_line_does_not_change_the_pending_machine_checks()
    {
        var lines = new List<NayaxConnectionCheckLine>
        {
            NayaxConnectionCheckLine.Failed(RetiredSignIn, NayaxConnectionCheckFailure.PermissionNotGranted),
        };
        lines.AddRange(NayaxConnectionCheckPolicy.All.Select(check =>
            NayaxConnectionCheckPolicy.RequiresMachine(check)
                ? NayaxConnectionCheckLine.NotChecked(check)
                : NayaxConnectionCheckLine.Passed(check)));

        var report = new NayaxConnectionCheckReport(lines, MachineCount: 0);

        Assert.True(report.MachineChecksPending);
        Assert.True(report.NothingFailed);
        Assert.Equal(NayaxConnectionCheckOutcome.Ready, report.Outcome);
    }

    /// <summary>
    /// The retired check reaches a report by being read back out of the database, so the whole rule
    /// has to hold across the round trip the draft and the stored results actually take. The number
    /// survives serialization - which is exactly why it stays reserved - and the report read back
    /// from the JSON answers identically.
    /// </summary>
    [Fact]
    public void A_retired_line_survives_the_json_round_trip_and_is_still_ignored()
    {
        var stored = JsonSerializer.Serialize(WithRetiredLine(
            NayaxConnectionCheckLine.Failed(RetiredSignIn, NayaxConnectionCheckFailure.PermissionNotGranted)));

        var report = JsonSerializer.Deserialize<NayaxConnectionCheckReport>(stored);

        Assert.NotNull(report);
        Assert.Contains(RetiredSignIn, report!.Lines.Select(line => line.Check));
        Assert.DoesNotContain(RetiredSignIn, report.DeclaredLines.Select(line => line.Check));
        Assert.True(report.NothingFailed);
        Assert.Equal(NayaxConnectionCheckOutcome.Ready, report.Outcome);
    }

    /// <summary>
    /// A report with no machine-list line at all concludes nothing rather than defaulting: the
    /// fail-closed answer, whether that report came from a build that declared a different check or
    /// from a stored string nobody can account for.
    /// </summary>
    [Fact]
    public void A_report_with_no_machine_list_line_concludes_nothing()
    {
        var report = new NayaxConnectionCheckReport(
            [NayaxConnectionCheckLine.Passed(RetiredSignIn)],
            MachineCount: null);

        Assert.Empty(report.DeclaredLines);
        Assert.Equal(NayaxConnectionCheckOutcome.Inconclusive, report.Outcome);
        Assert.False(report.MachineChecksPending);
    }

    private static NayaxConnectionCheckReport AllPassed() =>
        new([.. NayaxConnectionCheckPolicy.All.Select(NayaxConnectionCheckLine.Passed)], MachineCount: 1);

    /// <summary>Every declared check passing, with one line for a check this build does not declare.</summary>
    private static NayaxConnectionCheckReport WithRetiredLine(NayaxConnectionCheckLine retired)
    {
        var lines = new List<NayaxConnectionCheckLine> { retired };
        lines.AddRange(NayaxConnectionCheckPolicy.All.Select(NayaxConnectionCheckLine.Passed));
        return new NayaxConnectionCheckReport(lines, MachineCount: 1);
    }
}
