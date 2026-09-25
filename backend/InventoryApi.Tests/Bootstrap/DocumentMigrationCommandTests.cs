using Inventory.Application.Documents;
using Inventory.Infrastructure.Documents;
using Inventory.Infrastructure.Documents.Migration;
using InventoryApi.Bootstrap;
using Xunit;

namespace InventoryApi.Tests.Bootstrap;

/// <summary>
/// The operator-facing half of <c>migrate-documents</c>: how it is invoked, what it refuses, what
/// it exits with, and what it prints (issue #39, checkpoint 3).
///
/// This command copies business documents between two systems, so the things worth pinning down
/// are the ones an operator relies on without re-reading the code: that a half-remembered
/// invocation cannot write anything, that it will not run against the wrong destination, and
/// that the exit code means what the documentation says it means.
/// </summary>
public sealed class DocumentMigrationCommandTests
{
    private const string BlobServiceUri = "https://inventoryappdocs.blob.core.windows.net";

    #region Arguments

    [Fact]
    public void The_command_is_recognised_by_name()
    {
        Assert.True(DocumentMigrationCommand.Matches(["migrate-documents", "--dry-run"]));
        Assert.False(DocumentMigrationCommand.Matches(["migrate-database", "--dry-run"]));
        Assert.False(DocumentMigrationCommand.Matches([]));
    }

    [Fact]
    public void The_dry_run_flag_selects_a_dry_run()
    {
        Assert.True(DocumentMigrationArguments.TryParse(
            ["migrate-documents", "--dry-run"], out var apply, out var error));

        Assert.False(apply);
        Assert.Empty(error);
    }

    [Fact]
    public void The_apply_flag_alone_applies()
    {
        Assert.True(DocumentMigrationArguments.TryParse(
            ["migrate-documents", "--apply"], out var apply, out _));

        Assert.True(apply);
    }

    /// <summary>
    /// Unlike the other two commands, a bare invocation is refused rather than treated as a dry
    /// run. This one talks to a database and a storage account at once, so an operator should
    /// never be answering "what does it do if I just run it?" from memory.
    /// </summary>
    [Fact]
    public void An_invocation_with_no_mode_is_refused_and_does_not_apply()
    {
        Assert.False(DocumentMigrationArguments.TryParse(
            ["migrate-documents"], out var apply, out var error));

        Assert.False(apply);
        Assert.Contains("--dry-run", error, StringComparison.Ordinal);
        Assert.Contains("--apply", error, StringComparison.Ordinal);
    }

    /// <summary>
    /// Both flags together is ambiguous, and specifically must not resolve to the writing one: a
    /// typo made while reaching for safety must not become a live copy.
    /// </summary>
    [Theory]
    [InlineData("--apply", "--dry-run")]
    [InlineData("--dry-run", "--apply")]
    public void Both_modes_together_are_refused_and_do_not_apply(string first, string second)
    {
        Assert.False(DocumentMigrationArguments.TryParse(
            ["migrate-documents", first, second], out var apply, out var error));

        Assert.False(apply);
        Assert.Contains("mutually exclusive", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--delete-source")]
    [InlineData("--force")]
    [InlineData("-a")]
    public void An_unrecognised_argument_is_refused_and_does_not_apply(string argument)
    {
        Assert.False(DocumentMigrationArguments.TryParse(
            ["migrate-documents", "--apply", argument], out var apply, out var error));

        Assert.False(apply);
        Assert.Contains(argument, error, StringComparison.Ordinal);
    }

    #endregion

    #region Destination configuration

    /// <summary>
    /// The application's own default is the filesystem. A migration that accepted that default
    /// would copy every document from the filesystem back to the filesystem and report success,
    /// so the destination has to be Azure Blob explicitly or the command refuses to start.
    /// </summary>
    [Fact]
    public void The_migration_refuses_to_run_against_the_filesystem_provider()
    {
        var configuration = DocumentStorageConfiguration.Resolve(
            new DocumentStorageOptions { Provider = "FileSystem" });

        var failure = Assert.Throws<InvalidOperationException>(() =>
            DocumentMigrationDestinationFactory.CreateAzureBlob(configuration));

        Assert.Contains("AzureBlob", failure.Message, StringComparison.Ordinal);
        Assert.Contains("BlobServiceUri", failure.Message, StringComparison.Ordinal);
        Assert.Contains("ContainerName", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// An unconfigured section resolves to the filesystem default, which must be refused for the
    /// same reason rather than silently accepted.
    /// </summary>
    [Fact]
    public void The_migration_refuses_to_run_with_no_document_storage_configuration()
    {
        var configuration = DocumentStorageConfiguration.Resolve(new DocumentStorageOptions());

        Assert.Throws<InvalidOperationException>(() =>
            DocumentMigrationDestinationFactory.CreateAzureBlob(configuration));
    }

    [Fact]
    public void An_incomplete_azure_configuration_is_refused_before_a_destination_exists()
    {
        Assert.Throws<InvalidOperationException>(() => DocumentStorageConfiguration.Resolve(
            new DocumentStorageOptions { Provider = "AzureBlob", ContainerName = "business-documents-dev" }));
    }

    /// <summary>
    /// A complete configuration produces a destination without contacting Azure: the credential
    /// chain and the client are both lazy, which is what lets this run in the suite.
    /// </summary>
    [Fact]
    public void A_complete_azure_configuration_produces_a_destination()
    {
        var configuration = DocumentStorageConfiguration.Resolve(new DocumentStorageOptions
        {
            Provider = "AzureBlob",
            BlobServiceUri = BlobServiceUri,
            ContainerName = "business-documents-dev",
        });

        var destination = DocumentMigrationDestinationFactory.CreateAzureBlob(configuration);

        Assert.IsType<AzureBlobMigrationDestination>(destination);
        Assert.Equal(
            "tenants/4/purchases/receipt.jpg",
            destination.KeyFor(Inventory.Domain.Tenancy.BusinessId.From(4),
                DocumentCategory.PurchaseDocument, "receipt.jpg"));
    }

    #endregion

    #region Exit codes

    /// <summary>
    /// The documented contract: pending work is not a problem, so a clean dry run exits zero
    /// whether it found nothing to do or plenty. An operator can therefore gate an apply on it.
    /// </summary>
    [Theory]
    [InlineData(DocumentMigrationStatus.Pending)]
    [InlineData(DocumentMigrationStatus.Migrated)]
    [InlineData(DocumentMigrationStatus.AlreadyPresent)]
    [InlineData(DocumentMigrationStatus.DuplicateReference)]
    public void A_run_with_nothing_unresolved_exits_zero(DocumentMigrationStatus status)
    {
        var report = ReportOf(status);

        Assert.Equal(0, report.ExitCode);
        Assert.Empty(report.Unresolved);
    }

    [Theory]
    [InlineData(DocumentMigrationStatus.MissingSource)]
    [InlineData(DocumentMigrationStatus.Collision)]
    [InlineData(DocumentMigrationStatus.InvalidBusiness)]
    [InlineData(DocumentMigrationStatus.Failed)]
    public void A_run_with_an_unresolved_document_exits_one(DocumentMigrationStatus status)
    {
        var report = ReportOf(status);

        Assert.Equal(1, report.ExitCode);
        Assert.Single(report.Unresolved);
    }

    /// <summary>
    /// A dry run obeys the same rule, so a collision is surfaced as a failing exit code before
    /// anything has been written.
    /// </summary>
    [Fact]
    public void A_dry_run_that_found_a_collision_exits_one()
    {
        var report = new DocumentMigrationReport(DryRun: true, [
            Item(1, DocumentMigrationStatus.Pending),
            Item(2, DocumentMigrationStatus.Collision),
        ]);

        Assert.Equal(1, report.ExitCode);
    }

    [Fact]
    public void Every_candidate_is_accounted_for_exactly_once()
    {
        var report = new DocumentMigrationReport(DryRun: false, [
            Item(1, DocumentMigrationStatus.Migrated),
            Item(2, DocumentMigrationStatus.AlreadyPresent),
            Item(3, DocumentMigrationStatus.DuplicateReference),
            Item(4, DocumentMigrationStatus.MissingSource),
            Item(5, DocumentMigrationStatus.Collision),
            Item(6, DocumentMigrationStatus.InvalidBusiness),
            Item(7, DocumentMigrationStatus.Failed),
        ]);

        var counted = report.Migrated + report.AlreadyPresent + report.DuplicateReferences +
            report.MissingSource + report.Collision + report.InvalidBusiness + report.Failed +
            report.Pending;

        Assert.Equal(report.Candidates, counted);
    }

    #endregion

    #region Report

    /// <summary>
    /// The report is what a human reviews before authorising an apply, so it has to name the
    /// record precisely enough to find it - and say nothing about what the document contains.
    /// </summary>
    [Fact]
    public void The_report_identifies_an_unresolved_record_without_revealing_the_document()
    {
        var report = new DocumentMigrationReport(DryRun: true, [
            new DocumentMigrationItem(
                "Purchase", 42, 7, DocumentCategory.PurchaseDocument, "9f0c.jpg",
                DocumentMigrationStatus.Collision,
                DocumentSourceLocation.ProtectedStorage,
                ByteLength: 14,
                DestinationKey: "tenants/7/purchases/9f0c.jpg",
                Reason: "The destination already holds a different document."),
        ]);

        var output = Render(report);

        Assert.Contains("Collision", output, StringComparison.Ordinal);
        Assert.Contains("Purchase 42", output, StringComparison.Ordinal);
        Assert.Contains("business 7", output, StringComparison.Ordinal);
        Assert.Contains("9f0c.jpg", output, StringComparison.Ordinal);
        Assert.Contains("Exiting 1", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// Server paths belong in neither an operator's terminal nor their paste buffer; the report
    /// names locations by kind and documents by their tenant-scoped key.
    /// </summary>
    [Fact]
    public void The_report_prints_no_filesystem_paths()
    {
        var output = Render(new DocumentMigrationReport(DryRun: true, [
            Item(1, DocumentMigrationStatus.Pending),
            Item(2, DocumentMigrationStatus.MissingSource),
        ]));

        Assert.DoesNotContain("protected-files", output, StringComparison.Ordinal);
        Assert.DoesNotContain("wwwroot", output, StringComparison.Ordinal);
        Assert.DoesNotContain(Path.GetTempPath(), output, StringComparison.Ordinal);
    }

    /// <summary>
    /// An apply must say, every time, that the source documents are still there. Retiring them
    /// is a later human decision, and the report is where that expectation is set.
    /// </summary>
    [Fact]
    public void An_apply_report_states_that_source_documents_were_not_deleted()
    {
        var output = Render(new DocumentMigrationReport(DryRun: false, [
            Item(1, DocumentMigrationStatus.Migrated),
        ]));

        Assert.Contains("NOT deleted", output, StringComparison.Ordinal);
        Assert.Contains("Migrated", output, StringComparison.Ordinal);
    }

    [Fact]
    public void A_dry_run_report_says_nothing_was_written_and_how_to_apply()
    {
        var output = Render(new DocumentMigrationReport(DryRun: true, [
            Item(1, DocumentMigrationStatus.Pending),
        ]));

        Assert.Contains("DRY RUN", output, StringComparison.Ordinal);
        Assert.Contains("nothing was written", output, StringComparison.Ordinal);
        Assert.Contains("--apply", output, StringComparison.Ordinal);
    }

    [Fact]
    public void The_report_lists_records_that_share_a_document()
    {
        var output = Render(new DocumentMigrationReport(DryRun: false, [
            Item(1, DocumentMigrationStatus.Migrated),
            Item(2, DocumentMigrationStatus.DuplicateReference),
        ]));

        Assert.Contains("DuplicateReferences : 1", output, StringComparison.Ordinal);
        Assert.Contains("Purchase 2", output, StringComparison.Ordinal);
    }

    #endregion

    #region Helpers

    private static DocumentMigrationReport ReportOf(DocumentMigrationStatus status) =>
        new(DryRun: status == DocumentMigrationStatus.Pending, [Item(1, status)]);

    private static DocumentMigrationItem Item(int recordId, DocumentMigrationStatus status) =>
        new("Purchase", recordId, 1, DocumentCategory.PurchaseDocument, $"{recordId}.jpg", status);

    private static string Render(DocumentMigrationReport report)
    {
        using var output = new StringWriter();
        DocumentMigrationCommand.Write(report, output);
        return output.ToString();
    }

    #endregion
}
