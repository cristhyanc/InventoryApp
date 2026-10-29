using Inventory.Application.Documents;
using Inventory.Infrastructure.Documents.Reconciliation;
using InventoryApi.Bootstrap;
using Xunit;

namespace InventoryApi.Tests.Bootstrap;

/// <summary>
/// The operator-facing half of <c>reconcile-attachment-storage</c> (issue #968): how it is invoked,
/// what it refuses, what it exits with, and what it prints.
///
/// <para>The engine's behaviour is <c>AttachmentStorageReconcilerTests</c>'. What is worth pinning
/// down here is what an operator relies on without re-reading the code: that a half-remembered
/// invocation cannot write anything, that the exit code means what the documentation says it means,
/// and that the report names records and stored names rather than anything about a business or what
/// a document contains.</para>
/// </summary>
public sealed class AttachmentStorageReconciliationCommandTests
{
    [Fact]
    public void The_command_is_recognised_by_name()
    {
        Assert.True(AttachmentStorageReconciliationCommand.Matches(
            ["reconcile-attachment-storage", "--dry-run"]));
        Assert.False(AttachmentStorageReconciliationCommand.Matches(["migrate-documents", "--dry-run"]));
        Assert.False(AttachmentStorageReconciliationCommand.Matches([]));
    }

    [Fact]
    public void The_dry_run_flag_selects_a_dry_run_and_the_apply_flag_applies()
    {
        Assert.True(AttachmentStorageReconciliationArguments.TryParse(
            ["reconcile-attachment-storage", "--dry-run"], out var dryRun, out var dryRunError));
        Assert.False(dryRun);
        Assert.Empty(dryRunError);

        Assert.True(AttachmentStorageReconciliationArguments.TryParse(
            ["reconcile-attachment-storage", "--apply"], out var apply, out _));
        Assert.True(apply);
    }

    /// <summary>
    /// A bare invocation is refused rather than treated as a dry run, as <c>migrate-documents</c>
    /// is: this command reads a database and a storage account at once, and an operator should
    /// never have to answer "what does it do if I just run it?" from memory.
    /// </summary>
    [Fact]
    public void An_invocation_with_no_mode_is_refused_and_does_not_apply()
    {
        Assert.False(AttachmentStorageReconciliationArguments.TryParse(
            ["reconcile-attachment-storage"], out var apply, out var error));

        Assert.False(apply);
        Assert.Contains("--dry-run", error, StringComparison.Ordinal);
        Assert.Contains("--apply", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Both_modes_at_once_and_an_unknown_argument_are_refused()
    {
        Assert.False(AttachmentStorageReconciliationArguments.TryParse(
            ["reconcile-attachment-storage", "--dry-run", "--apply"], out _, out var bothError));
        Assert.Contains("mutually exclusive", bothError, StringComparison.Ordinal);

        Assert.False(AttachmentStorageReconciliationArguments.TryParse(
            ["reconcile-attachment-storage", "--delete-orphans"], out _, out var unknownError));
        Assert.Contains("--delete-orphans", unknownError, StringComparison.Ordinal);
    }

    /// <summary>
    /// A clean reconciliation says so and exits zero, so an apply can be gated on a clean dry run.
    /// </summary>
    [Fact]
    public void A_report_with_nothing_to_resolve_says_so()
    {
        var report = new AttachmentStorageReconciliationReport(
            DryRun: true,
            [new AttachmentStorageBusinessReport(7, 2, 500, [])],
            []);

        var written = Write(report);

        Assert.Equal(0, report.ExitCode);
        Assert.Contains("DRY RUN (nothing will be written)", written, StringComparison.Ordinal);
        Assert.Contains("Business 7: 2 attachment(s), 500 byte(s) charged", written, StringComparison.Ordinal);
        Assert.Contains("Nothing needs attention.", written, StringComparison.Ordinal);
    }

    /// <summary>
    /// And one that found something prints each finding with the record and the stored name, says
    /// plainly that nothing was deleted, and exits one.
    /// </summary>
    [Fact]
    public void A_report_with_findings_names_them_and_says_nothing_was_deleted()
    {
        var report = new AttachmentStorageReconciliationReport(
            DryRun: false,
            [
                new AttachmentStorageBusinessReport(
                    7,
                    1,
                    300,
                    [
                        new AttachmentStorageFinding(
                            AttachmentStorageFindingKind.MissingFile,
                            DocumentCategory.PurchaseDocument,
                            11,
                            "missing.jpg",
                            null),
                        new AttachmentStorageFinding(
                            AttachmentStorageFindingKind.OrphanFile,
                            DocumentCategory.ExpenseAttachment,
                            null,
                            "orphan.pdf",
                            900),
                    ]),
            ],
            [
                new AttachmentStorageFinding(
                    AttachmentStorageFindingKind.OrphanFile,
                    DocumentCategory.PurchaseDocument,
                    null,
                    "unattributed.jpg",
                    100),
            ]);

        var written = Write(report);

        Assert.Equal(1, report.ExitCode);
        Assert.Equal(900, report.Businesses.Single().OrphanBytes);
        Assert.Contains("APPLY", written, StringComparison.Ordinal);
        Assert.Contains("MissingFile", written, StringComparison.Ordinal);
        Assert.Contains("missing.jpg", written, StringComparison.Ordinal);
        Assert.Contains("record 11", written, StringComparison.Ordinal);
        Assert.Contains("unattributed.jpg", written, StringComparison.Ordinal);
        Assert.Contains("Nothing was deleted.", written, StringComparison.Ordinal);
    }

    private static string Write(AttachmentStorageReconciliationReport report)
    {
        using var output = new StringWriter();
        AttachmentStorageReconciliationCommand.Write(report, output);
        return output.ToString();
    }
}
