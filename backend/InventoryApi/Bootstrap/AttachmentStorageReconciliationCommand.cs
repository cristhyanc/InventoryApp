using Inventory.Application.Tenancy;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Documents;
using Inventory.Infrastructure.Documents.Reconciliation;
using Microsoft.EntityFrameworkCore;

namespace InventoryApi.Bootstrap;

/// <summary>
/// Parses the <c>reconcile-attachment-storage</c> command line (issue #968).
///
/// Like <c>migrate-documents</c>, and unlike the commands that treat a bare invocation as a dry
/// run, it insists on being told which one it is: it reads a database and a storage account at
/// once, so "what would this do if I just run it?" is a question an operator should never have to
/// answer from memory.
/// </summary>
public static class AttachmentStorageReconciliationArguments
{
    public const string CommandName = "reconcile-attachment-storage";
    public const string ApplyFlag = "--apply";
    public const string DryRunFlag = "--dry-run";

    public static bool Matches(string[] args) =>
        args.Length > 0 && string.Equals(args[0], CommandName, StringComparison.OrdinalIgnoreCase);

    /// <param name="args">The full argument list, including the command name at position 0.</param>
    /// <param name="apply">True only when <c>--apply</c> was given on its own.</param>
    /// <param name="error">Operator-facing reason the arguments were rejected, or empty.</param>
    public static bool TryParse(string[] args, out bool apply, out string error)
    {
        ArgumentNullException.ThrowIfNull(args);

        apply = false;
        error = string.Empty;

        var sawApply = false;
        var sawDryRun = false;

        foreach (var argument in args.Skip(1))
        {
            if (string.Equals(argument, ApplyFlag, StringComparison.OrdinalIgnoreCase))
            {
                sawApply = true;
            }
            else if (string.Equals(argument, DryRunFlag, StringComparison.OrdinalIgnoreCase))
            {
                sawDryRun = true;
            }
            else
            {
                error = $"Unrecognised argument '{argument}'. "
                    + $"Usage: {CommandName} [{DryRunFlag} | {ApplyFlag}]";
                return false;
            }
        }

        if (sawApply && sawDryRun)
        {
            error = $"{ApplyFlag} and {DryRunFlag} are mutually exclusive. Pass exactly one.";
            return false;
        }

        if (!sawApply && !sawDryRun)
        {
            error = $"Pass exactly one of {DryRunFlag} or {ApplyFlag}. "
                + $"Start with {DryRunFlag}: it writes nothing and reports what an apply would do.";
            return false;
        }

        apply = sawApply;
        return true;
    }
}

/// <summary>
/// Reconciles stored attachment files against the purchase and expense records that refer to them
/// (issue #968), as a deliberate, human-invoked step.
///
/// From a source tree, with the SDK installed:
/// <code>
///   dotnet run --project backend/InventoryApi -- reconcile-attachment-storage --dry-run
///   dotnet run --project backend/InventoryApi -- reconcile-attachment-storage --apply
/// </code>
///
/// From the deployed application, which is <c>dotnet publish</c> output and has no SDK or sources:
/// <code>
///   dotnet InventoryApi.dll reconcile-attachment-storage --dry-run
///   dotnet InventoryApi.dll reconcile-attachment-storage --apply
/// </code>
///
/// The web host never performs this: starting the API and running this command are mutually
/// exclusive paths through <c>Program.cs</c>. It works against whichever document storage the
/// deployment is configured with - the filesystem or Azure Blob - and reads it only.
///
/// <para><strong>It never deletes anything.</strong> An apply fills in attachment sizes that were
/// never recorded, taken from the stored files themselves, and writes nothing else. Orphan files
/// and records whose file is missing are reported for a human to decide about; automatic orphan
/// deletion is deliberately out of scope, because deleting a business's receipt is not recoverable
/// by rolling a transaction back.</para>
///
/// Exit code: <c>0</c> when nothing needs attention; <c>1</c> when something does - a record whose
/// file is missing, a size that is still unknown, or a stored file nothing refers to - and for a
/// refused argument list or an unusable storage configuration. A dry run obeys the same rule, so an
/// apply can be gated on a clean dry run.
/// </summary>
public static class AttachmentStorageReconciliationCommand
{
    public const string CommandName = AttachmentStorageReconciliationArguments.CommandName;

    public static bool Matches(string[] args) => AttachmentStorageReconciliationArguments.Matches(args);

    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        if (!AttachmentStorageReconciliationArguments.TryParse(args, out var apply, out var argumentError))
        {
            return Refuse(Console.Out, argumentError);
        }

        var builder = WebApplication.CreateBuilder(args);
        builder.Services.AddDbContext<AppDbContext>(options =>
        {
            options.UseSqlite(builder.Configuration.GetConnectionString("DefaultConnection")
                ?? "Data Source=inventory.db");
        });

        var storageOptions = builder.Configuration
            .GetSection(DocumentStorageOptions.SectionName)
            .Get<DocumentStorageOptions>() ?? new DocumentStorageOptions();

        IAttachmentFileInventory files;
        try
        {
            files = CreateInventory(
                DocumentStorageConfiguration.Resolve(storageOptions), builder.Environment);
        }
        catch (InvalidOperationException configurationError)
        {
            return Refuse(Console.Out, configurationError.Message);
        }

        using var host = builder.Build();
        using var scope = host.Services.CreateScope();
        var dbOptions = scope.ServiceProvider.GetRequiredService<DbContextOptions<AppDbContext>>();

        // The reconciliation reads every business's records on purpose - an orphan file is only an
        // orphan if no business refers to it - so it takes the unrestricted scope explicitly. It is
        // a human-invoked command rather than a request path, the same decision migrate-documents
        // and bootstrap-business make.
        await using var db = new AppDbContext(dbOptions, UnscopedBusinessScope.Instance);

        var report = await AttachmentStorageReconciler.RunAsync(db, files, apply, cancellationToken);

        Write(report, Console.Out);

        return report.ExitCode;
    }

    /// <summary>
    /// The read-only inspection of whichever storage this deployment uses. Azure Blob is laid out
    /// per business, so it can say whose an orphan file is; the filesystem is one folder per
    /// category for every business, so it cannot, and those orphans are reported separately rather
    /// than guessed at.
    /// </summary>
    private static IAttachmentFileInventory CreateInventory(
        ResolvedDocumentStorageConfiguration configuration, IWebHostEnvironment environment) =>
        AttachmentFileInventoryFactory.Create(
            configuration,
            new FileSystemDocumentStorageOptions
            {
                ContentRootPath = environment.ContentRootPath,
                WebRootPath = environment.WebRootPath,
            });

    private static int Refuse(TextWriter output, string reason)
    {
        output.WriteLine();
        output.WriteLine($"Attachment storage reconciliation - REFUSED: {reason}");
        output.WriteLine();
        return 1;
    }

    /// <summary>
    /// Prints the report an operator reviews, per business.
    ///
    /// It lists the counts first and then every item that needs attention, with enough to find the
    /// record - the kind of attachment, the record id, the stored name - and nothing that would
    /// reveal what a document contains or who a business is. Business ids, never business names.
    /// </summary>
    public static void Write(AttachmentStorageReconciliationReport report, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(output);

        output.WriteLine();
        output.WriteLine(
            $"Attachment storage reconciliation - {(report.DryRun ? "DRY RUN (nothing will be written)" : "APPLY")}");
        output.WriteLine(new string('-', 78));
        output.WriteLine($"Businesses          : {report.Businesses.Count}");
        output.WriteLine($"MissingFiles        : {report.MissingFiles}");
        output.WriteLine($"UnknownSizes        : {report.UnknownSizes}");
        output.WriteLine($"FilledSizes         : {report.FilledSizes}");
        output.WriteLine($"OrphanFiles         : {report.OrphanFiles}");
        output.WriteLine();

        foreach (var business in report.Businesses)
        {
            output.WriteLine(
                $"Business {business.BusinessId}: {business.Attachments} attachment(s), "
                + $"{business.ChargedBytes} byte(s) charged, {business.OrphanBytes} byte(s) orphaned");

            foreach (var finding in business.Findings)
            {
                output.WriteLine($"  {Describe(finding)}");
            }
        }

        if (report.UnattributedOrphans.Count > 0)
        {
            output.WriteLine();
            output.WriteLine(
                "Stored files no record refers to, in a layout that does not say whose they are "
                + "(charged to nobody, deleted by nothing):");
            foreach (var finding in report.UnattributedOrphans)
            {
                output.WriteLine($"  {Describe(finding)}");
            }
        }

        output.WriteLine();
        output.WriteLine(
            report.ExitCode == 0
                ? "Nothing needs attention."
                : "Items above need a human decision. Nothing was deleted.");
        output.WriteLine();
    }

    private static string Describe(AttachmentStorageFinding finding) =>
        $"{finding.Kind,-12} {finding.Category,-18} "
        + $"record {finding.RecordId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-",-8} "
        + $"{finding.StoredFileName} "
        + $"{finding.ByteLength?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?"} byte(s)";
}
