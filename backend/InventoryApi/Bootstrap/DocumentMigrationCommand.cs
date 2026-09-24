using Inventory.Application.Tenancy;
using Inventory.Infrastructure.Documents;
using Inventory.Infrastructure.Documents.Migration;
using InventoryApi.Data;
using Microsoft.EntityFrameworkCore;

namespace InventoryApi.Bootstrap;

/// <summary>
/// Parses the <c>migrate-documents</c> command line.
///
/// Unlike <c>migrate-database</c> and <c>bootstrap-business</c>, which treat a bare invocation as
/// a dry run, this command insists on being told which one it is. It talks to two systems at
/// once - a database and a storage account - so "what would this do if I just run it?" is a
/// question an operator should never have to answer from memory.
/// </summary>
public static class DocumentMigrationArguments
{
    public const string CommandName = "migrate-documents";
    public const string ApplyFlag = "--apply";
    public const string DryRunFlag = "--dry-run";

    public static bool Matches(string[] args) =>
        args.Length > 0 && string.Equals(args[0], CommandName, StringComparison.OrdinalIgnoreCase);

    /// <param name="args">The full argument list, including the command name at position 0.</param>
    /// <param name="apply">True only when <c>--apply</c> was given on its own.</param>
    /// <param name="error">Operator-facing reason the arguments were rejected, or empty.</param>
    public static bool TryParse(string[] args, out bool apply, out string error)
    {
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
/// Migrates stored business documents from the filesystem to tenant-scoped Azure Blob storage
/// as a deliberate, human-invoked step (issue #39, checkpoint 3).
///
/// From a source tree, with the SDK installed:
/// <code>
///   dotnet run --project backend/InventoryApi -- migrate-documents --dry-run
///   dotnet run --project backend/InventoryApi -- migrate-documents --apply
/// </code>
///
/// From the deployed application, which is `dotnet publish` output and has no SDK or sources:
/// <code>
///   dotnet InventoryApi.dll migrate-documents --dry-run
///   dotnet InventoryApi.dll migrate-documents --apply
/// </code>
///
/// The web host never performs this: starting the API and running this command are mutually
/// exclusive paths through <c>Program.cs</c>. It requires
/// <c>DocumentStorage:Provider=AzureBlob</c> and refuses to run against the application's
/// filesystem default, which would otherwise "migrate" every document from the filesystem to the
/// filesystem and report success.
///
/// It never deletes or modifies a source document, and never touches the database beyond reading
/// it. Retiring the filesystem copies is a separate human decision, after this has been run and
/// its report reviewed.
///
/// Exit code: <c>0</c> when the run completed with nothing unresolved; <c>1</c> when any document
/// is missing its source, collided with something already stored, carries no usable business id,
/// or failed - and for a refused argument list or an unusable storage configuration. A dry run
/// obeys the same rule, so an apply can be gated on a clean dry run.
/// </summary>
public static class DocumentMigrationCommand
{
    public const string CommandName = DocumentMigrationArguments.CommandName;

    public static bool Matches(string[] args) => DocumentMigrationArguments.Matches(args);

    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        if (!DocumentMigrationArguments.TryParse(args, out var apply, out var argumentError))
        {
            return Refuse(Console.Out, argumentError);
        }

        var builder = WebApplication.CreateBuilder(args);
        builder.Services.AddDbContext<AppDbContext>(options =>
        {
            options.UseLazyLoadingProxies();
            options.UseSqlite(builder.Configuration.GetConnectionString("DefaultConnection")
                ?? "Data Source=inventory.db");
        });

        var storageOptions = builder.Configuration
            .GetSection(DocumentStorageOptions.SectionName)
            .Get<DocumentStorageOptions>() ?? new DocumentStorageOptions();

        IDocumentMigrationDestination destination;
        try
        {
            destination = DocumentMigrationDestinationFactory.CreateAzureBlob(
                DocumentStorageConfiguration.Resolve(storageOptions));
        }
        catch (InvalidOperationException configurationError)
        {
            return Refuse(Console.Out, configurationError.Message);
        }

        var source = new FileSystemDocumentStorage(new FileSystemDocumentStorageOptions
        {
            ContentRootPath = builder.Environment.ContentRootPath,
            WebRootPath = builder.Environment.WebRootPath,
        });

        using var host = builder.Build();
        using var scope = host.Services.CreateScope();
        var dbOptions = scope.ServiceProvider.GetRequiredService<DbContextOptions<AppDbContext>>();

        // The migration reads every business's records on purpose, so it takes the unrestricted
        // scope explicitly. This is one of the few places in the codebase that does, and it is a
        // human-invoked command rather than a request path.
        await using var db = new AppDbContext(dbOptions, UnscopedBusinessScope.Instance);

        var report = await DocumentMigrator.RunAsync(db, source, destination, apply, cancellationToken);

        Write(report, Console.Out);

        return report.ExitCode;
    }

    private static int Refuse(TextWriter output, string reason)
    {
        output.WriteLine();
        output.WriteLine($"Document migration - REFUSED: {reason}");
        output.WriteLine();
        return 1;
    }

    /// <summary>
    /// Prints the report an operator reviews before authorising an apply, and again afterwards.
    ///
    /// It lists counts first and then every item that needs attention, with enough to find the
    /// record - type, id, business, stored name - and nothing that would reveal what the
    /// document contains. Hashes appear only as short prefixes, for correlating a source with
    /// what is already at its destination.
    /// </summary>
    public static void Write(DocumentMigrationReport report, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(output);

        output.WriteLine();
        output.WriteLine(
            $"Document migration - {(report.DryRun ? "DRY RUN (nothing will be written)" : "APPLY")}");
        output.WriteLine(new string('-', 78));
        output.WriteLine($"Candidates          : {report.Candidates}");

        if (report.DryRun)
        {
            output.WriteLine($"Pending             : {report.Pending}");
        }
        else
        {
            output.WriteLine($"Migrated            : {report.Migrated}");
        }

        output.WriteLine($"AlreadyPresent      : {report.AlreadyPresent}");
        output.WriteLine($"DuplicateReferences : {report.DuplicateReferences}");
        output.WriteLine($"MissingSource       : {report.MissingSource}");
        output.WriteLine($"Collision           : {report.Collision}");
        output.WriteLine($"InvalidBusiness     : {report.InvalidBusiness}");
        output.WriteLine($"Failed              : {report.Failed}");
        output.WriteLine();
        output.WriteLine($"Source in protected : {report.ProtectedSource}");
        output.WriteLine($"Source in legacy    : {report.LegacySource}");
        output.WriteLine();

        var unresolved = report.Unresolved;
        if (unresolved.Count > 0)
        {
            output.WriteLine($"{unresolved.Count} document(s) need attention:");
            output.WriteLine();

            foreach (var item in unresolved)
            {
                output.WriteLine(
                    $"  {item.Status,-15} {item.RecordType} {item.RecordId} "
                        + $"(business {item.BusinessId}, {item.Category}, stored name {item.StoredFileName})");
                output.WriteLine($"    {item.Reason}");
            }

            output.WriteLine();
        }

        if (report.DuplicateReferences > 0)
        {
            output.WriteLine(
                $"{report.DuplicateReferences} record(s) name a document another record also names. "
                    + "The bytes are migrated once; the records are listed so the shared reference is visible:");
            output.WriteLine();

            foreach (var item in report.Items.Where(x => x.Status == DocumentMigrationStatus.DuplicateReference))
            {
                output.WriteLine(
                    $"  {item.RecordType} {item.RecordId} (business {item.BusinessId}, {item.Category}, "
                        + $"stored name {item.StoredFileName})");
            }

            output.WriteLine();
        }

        output.WriteLine(
            report.DryRun
                ? $"Dry run: nothing was written. Re-run with {DocumentMigrationArguments.ApplyFlag} to copy."
                : "Source documents were NOT deleted. Retiring them is a separate, later decision.");

        if (report.ExitCode != 0)
        {
            output.WriteLine();
            output.WriteLine("Exiting 1: the run has unresolved documents (see above).");
        }

        output.WriteLine();
    }
}
