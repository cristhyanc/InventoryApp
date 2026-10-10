using System.Globalization;
using Inventory.Application.Tenancy;
using Inventory.Application.Time;
using Inventory.Domain.Nayax;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure;
using Inventory.Infrastructure.Clock;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Nayax;
using Inventory.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace InventoryApi.Bootstrap;

/// <summary>
/// Moves the application's one globally configured Nayax operator id and access token into the
/// existing business's own encrypted connection record (issue #519, a slice of #500).
///
/// It exists so that the switch to per-business Nayax credentials is something a human performs
/// deliberately, with its output in front of them, rather than something a deployment or a startup
/// step does to a production secret. The API never invokes it: starting the web host and running
/// this command are mutually exclusive paths through <c>Program.cs</c>.
///
/// From a source tree, with the SDK installed:
/// <code>
///   dotnet run --project backend/InventoryApi -- migrate-nayax-connection --dry-run
///   dotnet run --project backend/InventoryApi -- migrate-nayax-connection --apply
/// </code>
///
/// From the deployed application, which is `dotnet publish` output and has no SDK or sources:
/// <code>
///   dotnet InventoryApi.dll migrate-nayax-connection --dry-run
///   dotnet InventoryApi.dll migrate-nayax-connection --apply
/// </code>
///
/// <c>--apply</c> must be typed explicitly; an invocation with neither flag is a dry run, and a
/// dry run writes nothing at all. An apply is one transaction, committed only once the credential
/// is stored <em>and</em> the connection is marked Ready, so a run that reports anything other than
/// success has written nothing and the report says so in as many words. Re-running an applied
/// migration changes nothing, and re-running after a failed apply is simply the same run again. It
/// reads the same configuration the running Nayax client reads - <c>NayaxLynx:OperatorId</c> and the
/// token resolved by <see cref="NayaxLynxConfiguration.ResolveAccessToken"/> - and it does not remove
/// those settings: retiring them is a separate human step, after issue #520 makes the client read
/// the per-business record. See docs/tenant-rollout.md § Migrating the Nayax connection.
///
/// Exit code: <c>0</c> when the run succeeded (including a dry run and an idempotent re-run);
/// <c>1</c> for a refused argument list, an unusable key configuration, or any refusal
/// <see cref="NayaxConnectionMigrationOutcome"/> describes - so an apply can be gated on a clean
/// dry run.
/// </summary>
public static class NayaxConnectionMigrationCommand
{
    public const string CommandName = NayaxConnectionMigrationArguments.CommandName;

    public static bool Matches(string[] args) => NayaxConnectionMigrationArguments.Matches(args);

    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        if (!NayaxConnectionMigrationArguments.TryParse(args, out var apply, out var argumentError))
        {
            return Refuse(Console.Out, argumentError);
        }

        var dryRun = !apply;

        var builder = WebApplication.CreateBuilder(args);
        builder.Services.AddDbContext<AppDbContext>(options =>
        {
            options.UseSqlite(builder.Configuration.GetConnectionString("DefaultConnection")
                ?? "Data Source=inventory.db");
        });

        // The same two keys the composition root binds for the Nayax HTTP client, resolved by the
        // same function, so this command moves exactly the credential the running application
        // authenticates with rather than a second opinion about where it lives.
        var lynxOptions = builder.Configuration.GetSection(NayaxLynxOptions.SectionName).Get<NayaxLynxOptions>()
            ?? new NayaxLynxOptions();
        var configuredAccessToken = NayaxLynxConfiguration.ResolveAccessToken(
            builder.Configuration["NayaxLynx:AccessToken"],
            builder.Configuration["Nayax:Token"]);

        var protectionOptions = builder.Configuration
            .GetSection(NayaxTokenProtectionOptions.SectionName)
            .Get<NayaxTokenProtectionOptions>() ?? new NayaxTokenProtectionOptions();

        // Checked before either mode, so a dry run and an apply agree: with no key at all the API
        // deliberately starts anyway (the shipped state, see README.md § Configuration and
        // secrets), but this command cannot store a credential without one, and a dry run that
        // reported a plan the apply would then refuse would make "apply only after a clean dry run"
        // worthless.
        if (!protectionOptions.IsConfigured)
        {
            return Refuse(
                Console.Out,
                "No Nayax token encryption key is configured, so the credential cannot be stored "
                    + $"encrypted. Provision {NayaxTokenProtectionOptions.SectionName}:"
                    + $"{nameof(NayaxTokenProtectionOptions.ActiveKeyId)} and the matching "
                    + $"{NayaxTokenProtectionOptions.SectionName}:"
                    + $"{nameof(NayaxTokenProtectionOptions.Keys)} entry for this environment first. "
                    + "Nothing was written.");
        }

        // Registered through the composition root's own extension so the key section is validated
        // here, by the same code the API validates it with, and an incomplete section is a refusal
        // with the setting named rather than a stack trace. Its messages never carry key material.
        try
        {
            builder.Services.AddNayaxTokenProtection(protectionOptions);
        }
        catch (InvalidOperationException configurationError)
        {
            return Refuse(Console.Out, configurationError.Message);
        }

        using var host = builder.Build();
        using var scope = host.Services.CreateScope();

        var dbOptions = scope.ServiceProvider.GetRequiredService<DbContextOptions<AppDbContext>>();
        var protector = scope.ServiceProvider.GetRequiredService<INayaxTokenProtector>();
        var loggerFactory = host.Services.GetRequiredService<ILoggerFactory>();
        IClock clock = new SystemClock();

        // Deliberately *not* an unrestricted context, unlike `bootstrap-business`,
        // `migrate-database` and `migrate-documents` (AGENTS.md § Tenant ownership and data
        // isolation). This command needs no cross-business access at all: the tenancy tables it
        // reads to resolve and verify the business - Businesses, BusinessMemberships and
        // BusinessBackfillAudits - deliberately carry no tenant query filter, and every read and
        // write of the credential itself goes through a context scoped to the one business it
        // resolved, so the central query filters and BusinessOwnershipEnforcer stay fully in force.
        await using var tenancyDb = new AppDbContext(dbOptions, new BusinessScope());

        var scopedContexts = new List<AppDbContext>();

        try
        {
            var migrator = new NayaxConnectionMigrator(
                tenancyDb,
                businessId =>
                {
                    var scopedDb = new AppDbContext(dbOptions, ResolvedScopeFor(businessId));
                    scopedContexts.Add(scopedDb);

                    // The context and the store built over it travel together, because the apply's
                    // transaction is begun on that context and only covers writes the store makes
                    // through it (see NayaxConnectionMigrationTarget).
                    return new NayaxConnectionMigrationTarget(
                        scopedDb,
                        new EfNayaxConnectionStore(
                            scopedDb,
                            protector,
                            clock,
                            loggerFactory.CreateLogger<EfNayaxConnectionStore>()));
                },
                lynxOptions.OperatorId,
                configuredAccessToken,
                clock);

            var result = await migrator.RunAsync(dryRun, cancellationToken);

            Write(result, Console.Out);

            return result.Succeeded ? 0 : 1;
        }
        finally
        {
            foreach (var scopedDb in scopedContexts)
            {
                await scopedDb.DisposeAsync();
            }
        }
    }

    /// <summary>
    /// The scope a request for this business would carry. The business id comes from the database
    /// this command resolved it in, never from the command line: there is no <c>--business-id</c>
    /// flag, and adding one would make a credential's owner something an operator types.
    /// </summary>
    private static IBusinessScope ResolvedScopeFor(int businessId)
    {
        var scope = new BusinessScope();
        scope.Resolve(BusinessId.From(businessId));
        return scope;
    }

    private static int Refuse(TextWriter output, string reason)
    {
        output.WriteLine();
        output.WriteLine($"Nayax connection migration - REFUSED: {reason}");
        output.WriteLine();
        return 1;
    }

    /// <summary>
    /// Prints the report an operator reviews before authorising an apply, and again afterwards.
    ///
    /// Nothing printed here is a secret. The operator id is a remote identity (the Lynx API's
    /// <c>OperatorID</c> path parameter) and is shown so the operator can confirm which account is
    /// being stored; the access token is never printed, not even as a length or a prefix, and
    /// whether the stored one matches the configured one is reported as a plain yes/no.
    /// </summary>
    public static void Write(NayaxConnectionMigrationResult result, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(output);

        var mode = result.DryRun ? "DRY RUN (nothing will be written)" : "APPLY";
        output.WriteLine();
        output.WriteLine($"Nayax connection migration - {mode}");
        output.WriteLine(new string('-', 78));

        if (!result.Succeeded)
        {
            output.WriteLine($"FAILED ({result.Outcome}): {result.Message}");
            output.WriteLine($"Database             : {Describe(result.DatabaseState)}");
            output.WriteLine();
            return;
        }

        output.WriteLine($"Business id          : {Describe(result.BusinessId)}");
        output.WriteLine($"Database             : {Describe(result.DatabaseState)}");
        output.WriteLine($"Configured operator  : {Describe(result.ConfiguredOperatorId)}");
        output.WriteLine($"Stored operator      : {Describe(result.StoredOperatorId)}");
        output.WriteLine($"Stored token matches : {DescribeMatch(result.StoredTokenMatchesConfigured)}");
        output.WriteLine("Access token         : never printed, never logged");
        output.WriteLine();
        output.WriteLine($"{"",-21}{"Before",-20}{"After",-20}");
        output.WriteLine(
            $"{"Status",-21}{Describe(result.StatusBefore),-20}{Describe(result.StatusAfter),-20}");
        output.WriteLine(
            $"{"Credential revision",-21}{Describe(result.CredentialRevisionBefore),-20}"
                + $"{Describe(result.CredentialRevisionAfter),-20}");
        output.WriteLine();
        output.WriteLine($"Change               : {result.Change}");
        output.WriteLine();
        output.WriteLine(result.Message);
        output.WriteLine();
    }

    private static string Describe(int? value) =>
        value is null ? "none" : value.Value.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Says in plain words whether this run changed anything, on success and on failure alike, so
    /// that a failure is never read as a partial write and a failed commit is never read as a
    /// refusal that left the database alone.
    /// </summary>
    private static string Describe(NayaxConnectionMigrationDatabaseState state) =>
        state switch
        {
            NayaxConnectionMigrationDatabaseState.Changed => "changed, as reported below",
            NayaxConnectionMigrationDatabaseState.Unknown =>
                "UNKNOWN - the commit failed; it holds either the whole change or none of it. Run "
                    + "--dry-run to see which.",
            _ => "unchanged - nothing was written",
        };

    private static string Describe(NayaxConnectionStatus? status) =>
        status is null ? "none" : status.Value.ToString();

    private static string Describe(string? value) => string.IsNullOrEmpty(value) ? "none" : value;

    private static string DescribeMatch(bool? matches) =>
        matches switch
        {
            true => "yes",
            false => "no",
            null => "nothing stored",
        };
}
