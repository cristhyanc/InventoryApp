using Inventory.Domain.Imports;
using Xunit;

namespace InventoryApi.Tests.Domain.Imports;

/// <summary>
/// The deterministic rules behind the uploaded reimbursement report import (issue #1149): the retry
/// stop rule, what a finished job is called, and what an untrusted archive entry name is reduced to.
/// </summary>
public class ReimbursementImportRulesTests
{
    private static readonly DateTime Now = new(2026, 11, 9, 4, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// The back-off the owner confirmed: one minute, five, fifteen, then an hour - and nothing
    /// after the fifth attempt.
    /// </summary>
    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 5)]
    [InlineData(3, 15)]
    [InlineData(4, 60)]
    public void A_failed_attempt_waits_the_scheduled_time_before_the_next_one(
        int attempts, int expectedMinutes)
    {
        var next = ReimbursementImportRetryPolicy.NextAttemptAtUtc(attempts, Now);

        Assert.Equal(Now.AddMinutes(expectedMinutes), next);
        Assert.False(ReimbursementImportRetryPolicy.IsExhausted(attempts));
    }

    /// <summary>
    /// The fifth failure is final: there is no next attempt instant, which is what makes the job
    /// stop rather than wait for a moment nobody ends.
    /// </summary>
    [Theory]
    [InlineData(5)]
    [InlineData(6)]
    public void The_fifth_attempt_is_the_last(int attempts)
    {
        Assert.True(ReimbursementImportRetryPolicy.IsExhausted(attempts));
        Assert.Null(ReimbursementImportRetryPolicy.NextAttemptAtUtc(attempts, Now));
    }

    /// <summary>The numbers the issue fixes, asserted where they are defined.</summary>
    [Fact]
    public void The_retry_schedule_is_five_attempts_over_the_four_documented_waits()
    {
        Assert.Equal(5, ReimbursementImportRetryPolicy.MaxAttempts);
        Assert.Equal(
            [
                TimeSpan.FromMinutes(1),
                TimeSpan.FromMinutes(5),
                TimeSpan.FromMinutes(15),
                TimeSpan.FromHours(1),
            ],
            ReimbursementImportRetryPolicy.Schedule);
        Assert.Equal(
            "Stopped after repeated database errors. Upload the ZIP again.",
            ReimbursementImportRetryPolicy.AttemptsExhaustedReason);
    }

    /// <summary>
    /// A job with no failed file is completed; one failed file makes it completed-with-failures, and
    /// a job whose files were all already imported is a success rather than a failure.
    /// </summary>
    [Theory]
    [InlineData(3, 0, 0, ReimbursementImportJobState.Completed)]
    [InlineData(0, 3, 0, ReimbursementImportJobState.Completed)]
    [InlineData(2, 1, 0, ReimbursementImportJobState.Completed)]
    [InlineData(2, 0, 1, ReimbursementImportJobState.CompletedWithFailures)]
    [InlineData(0, 0, 2, ReimbursementImportJobState.CompletedWithFailures)]
    public void A_finished_job_is_named_from_whether_any_file_failed(
        int imported, int skipped, int failed, ReimbursementImportJobState expected) =>
        Assert.Equal(
            expected,
            ReimbursementImportJobCompletion.StateFor(
                new ReimbursementImportFileCounts(imported, skipped, failed)));

    /// <summary>The counts come from the outcomes themselves, in any order.</summary>
    [Fact]
    public void The_counts_are_counted_from_the_recorded_outcomes()
    {
        var counts = ReimbursementImportJobCompletion.Count(
            [
                ReimbursementImportFileOutcome.Skipped,
                ReimbursementImportFileOutcome.Imported,
                ReimbursementImportFileOutcome.Failed,
                ReimbursementImportFileOutcome.Imported,
            ]);

        Assert.Equal(new ReimbursementImportFileCounts(2, 1, 1), counts);
        Assert.Equal(4, counts.Total);
    }

    /// <summary>The three final states, and the two that are not.</summary>
    [Theory]
    [InlineData(ReimbursementImportJobState.Queued, false)]
    [InlineData(ReimbursementImportJobState.Processing, false)]
    [InlineData(ReimbursementImportJobState.Completed, true)]
    [InlineData(ReimbursementImportJobState.CompletedWithFailures, true)]
    [InlineData(ReimbursementImportJobState.Failed, true)]
    public void Only_the_three_finished_states_are_final(
        ReimbursementImportJobState state, bool expected) =>
        Assert.Equal(expected, ReimbursementImportJobStates.IsFinal(state));

    /// <summary>
    /// An entry name is reduced to a label, never a path: traversal segments, a leading separator, a
    /// drive prefix and control characters are all gone.
    /// </summary>
    [Theory]
    [InlineData("10-2026/report.xml", "10-2026/report.xml")]
    [InlineData("../../../etc/passwd", "etc/passwd")]
    [InlineData("..\\..\\windows\\system32", "windows/system32")]
    [InlineData("/etc/report.xml", "etc/report.xml")]
    [InlineData("C:\\Temp\\report.xml", "Temp/report.xml")]
    [InlineData("./report.xml", "report.xml")]
    [InlineData("..", "")]
    [InlineData("../..", "")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void An_untrusted_entry_name_is_reduced_to_a_label(string? entryName, string expected) =>
        Assert.Equal(expected, ReimbursementArchivePolicy.SanitiseEntryName(entryName));

    /// <summary>A control character in an entry name cannot survive into a recorded label.</summary>
    [Fact]
    public void A_control_character_in_an_entry_name_is_replaced()
    {
        var label = ReimbursementArchivePolicy.SanitiseEntryName("re\u0000port\n.xml");

        Assert.Equal("re_port_.xml", label);
        Assert.DoesNotContain('\u0000', label);
    }

    /// <summary>A label is capped, so a hostile name cannot fill the column it is recorded in.</summary>
    [Fact]
    public void A_very_long_entry_name_is_capped()
    {
        var label = ReimbursementArchivePolicy.SanitiseEntryName(new string('a', 5_000) + ".xml");

        Assert.Equal(ReimbursementArchivePolicy.MaxEntryLabelLength, label.Length);
    }

    /// <summary>
    /// An uploaded file name keeps only its leaf: what is recorded is what the operator sent, never
    /// a directory a client claimed it lived in.
    /// </summary>
    [Theory]
    [InlineData("Reports_09-11-26.zip", "Reports_09-11-26.zip")]
    [InlineData("../../Reports_09-11-26.zip", "Reports_09-11-26.zip")]
    [InlineData("C:\\Users\\someone\\Reports_09-11-26.zip", "Reports_09-11-26.zip")]
    [InlineData("folder/Reports_09-11-26.zip", "Reports_09-11-26.zip")]
    [InlineData("..", "")]
    public void An_uploaded_file_name_is_reduced_to_its_leaf(string fileName, string expected) =>
        Assert.Equal(expected, ReimbursementArchivePolicy.SanitiseFileName(fileName));

    /// <summary>
    /// The limits the owner confirmed on 9 November 2026, asserted where they are defined so a
    /// change to one is a change to this test.
    /// </summary>
    [Fact]
    public void The_archive_limits_are_the_ones_the_owner_confirmed()
    {
        Assert.Equal(10 * 1024 * 1024, ReimbursementArchivePolicy.MaxUploadBytes);
        Assert.Equal(20, ReimbursementArchivePolicy.MaxInnerArchives);
        Assert.Equal(2_000, ReimbursementArchivePolicy.MaxEntries);
        Assert.Equal(5 * 1024 * 1024, ReimbursementArchivePolicy.MaxEntryExpandedBytes);
        Assert.Equal(100L * 1024 * 1024, ReimbursementArchivePolicy.MaxTotalExpandedBytes);
        Assert.Equal(200, ReimbursementArchivePolicy.MaxCompressionRatio);
    }

    /// <summary>Only <c>.xml</c> is imported, and only <c>.zip</c> is an archive - either case.</summary>
    [Theory]
    [InlineData("report.xml", true, false)]
    [InlineData("REPORT.XML", true, false)]
    [InlineData("report.pdf", false, false)]
    [InlineData("report.xlsx", false, false)]
    [InlineData("Reimbursments.zip", false, true)]
    [InlineData("REIMBURSMENTS.ZIP", false, true)]
    [InlineData("report.xml.pdf", false, false)]
    public void Only_xml_is_imported_and_only_zip_is_an_archive(
        string name, bool isXml, bool isArchive)
    {
        Assert.Equal(isXml, ReimbursementArchivePolicy.IsXmlEntry(name));
        Assert.Equal(isArchive, ReimbursementArchivePolicy.IsArchiveEntry(name));
    }
}
