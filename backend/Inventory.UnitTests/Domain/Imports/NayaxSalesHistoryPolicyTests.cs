using Inventory.Domain.Imports;
using Xunit;

namespace InventoryApi.Tests.Domain.Imports;

/// <summary>
/// The sales history import's limits and refusals (issue #1304). They are a Domain policy because a
/// limit written where the bytes happen to be read is a limit the next reader forgets, so these
/// cases pin the numbers an operator is told about and the relationship between them.
/// </summary>
public class NayaxSalesHistoryPolicyTests
{
    /// <summary>
    /// The ceiling the owner's decision asked for, and the byte limit beside it. A file inside the
    /// row ceiling is nowhere near the byte limit at the export's own ~90 bytes a row, which is what
    /// makes the byte limit a guard against something else being uploaded rather than a second row
    /// limit.
    /// </summary>
    [Fact]
    public void The_limits_are_the_decided_ones()
    {
        Assert.Equal(250_000, NayaxSalesHistoryPolicy.MaxRows);
        Assert.Equal(50L * 1024 * 1024, NayaxSalesHistoryPolicy.MaxUploadBytes);
    }

    /// <summary>
    /// A batch is far smaller than the file, and far smaller than the synchronous upload's whole
    /// ceiling: that is the entire reason this import can accept fifty times as many rows.
    /// </summary>
    [Fact]
    public void A_batch_is_a_small_fraction_of_the_file_and_of_the_uploads_own_ceiling()
    {
        Assert.Equal(500, NayaxSalesHistoryPolicy.BatchRows);
        Assert.True(NayaxSalesHistoryPolicy.BatchRows < NayaxSalesFilePolicy.MaxRows);
        Assert.True(NayaxSalesHistoryPolicy.MaxRows > NayaxSalesFilePolicy.MaxRows * 10);
    }

    /// <summary>
    /// The write-lock budget is the synchronous upload's own, because it is the same lock and the
    /// same thirty-second busy timeout every other writer gives up after. A background job is not a
    /// reason to make a purchase wait longer.
    /// </summary>
    [Fact]
    public void The_write_lock_budget_is_the_uploads_own()
    {
        Assert.Equal(
            NayaxSalesFilePolicy.MaxWriteLockHoldSeconds,
            NayaxSalesHistoryPolicy.MaxWriteLockHoldSeconds);
    }

    /// <summary>
    /// The refusal an operator reads names the limit from the constant, so lowering a limit cannot
    /// leave the sentence saying something the import no longer does.
    /// </summary>
    [Fact]
    public void The_refusals_name_their_own_limits()
    {
        Assert.Contains("250,000", NayaxSalesHistoryPolicy.TooManyRowsMessage);
        Assert.Contains("50 MB", NayaxSalesHistoryPolicy.TooLargeMessage);
        Assert.Contains(
            NayaxSalesHistoryPolicy.MaxAttempts.ToString(System.Globalization.CultureInfo.InvariantCulture),
            NayaxSalesHistoryPolicy.AttemptsExhaustedReason);
    }

    /// <summary>
    /// The synchronous upload's own too-large refusal points at this import, which is what makes the
    /// two limits one answer for an operator rather than a dead end.
    /// </summary>
    [Fact]
    public void The_synchronous_uploads_refusal_points_here()
    {
        Assert.Contains("sales history import", NayaxSalesFilePolicy.TooLargeMessage);
    }

    [Theory]
    [InlineData("history.csv", true)]
    [InlineData("HISTORY.CSV", true)]
    [InlineData("history.xlsx", false)]
    [InlineData("history.xls", false)]
    [InlineData("history.csv.xlsx", false)]
    [InlineData("history", false)]
    [InlineData(null, false)]
    public void Only_a_csv_is_accepted(string? fileName, bool accepted)
    {
        Assert.Equal(accepted, NayaxSalesHistoryPolicy.IsCsv(fileName));
    }

    /// <summary>
    /// The recorded name is a leaf label and never a path: the reimbursement import's own rule, asked
    /// rather than copied, so an untrusted upload name cannot become a traversal segment or a drive
    /// prefix on a stored row.
    /// </summary>
    [Theory]
    [InlineData("history.csv", "history.csv")]
    [InlineData("exports/2026/history.csv", "history.csv")]
    [InlineData("..\\..\\etc\\passwd.csv", "passwd.csv")]
    [InlineData("C:\\exports\\history.csv", "history.csv")]
    [InlineData("../", "")]
    [InlineData(null, "")]
    public void A_recorded_file_name_is_a_leaf_label(string? fileName, string expected)
    {
        Assert.Equal(expected, NayaxSalesHistoryPolicy.SanitiseFileName(fileName));
    }

    [Fact]
    public void A_long_file_name_is_capped()
    {
        var name = new string('a', NayaxSalesHistoryPolicy.MaxFileNameLength + 50) + ".csv";

        Assert.Equal(
            NayaxSalesHistoryPolicy.MaxFileNameLength,
            NayaxSalesHistoryPolicy.SanitiseFileName(name).Length);
    }

    /// <summary>Only the two final states are final; a queued or processing job is still the worker's.</summary>
    [Theory]
    [InlineData(NayaxSalesHistoryImportJobState.Queued, false)]
    [InlineData(NayaxSalesHistoryImportJobState.Processing, false)]
    [InlineData(NayaxSalesHistoryImportJobState.Completed, true)]
    [InlineData(NayaxSalesHistoryImportJobState.Failed, true)]
    public void Only_completed_and_failed_are_final(NayaxSalesHistoryImportJobState state, bool final)
    {
        Assert.Equal(final, NayaxSalesHistoryImportJobStates.IsFinal(state));
    }
}
