using System.Globalization;
using Inventory.Application.ConnectedMachines;
using Inventory.Application.Nayax;
using Inventory.Application.Subscriptions;
using Inventory.Application.Tenancy;
using Inventory.Application.Time;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure;
using Inventory.Infrastructure.Clock;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Nayax;
using Inventory.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace InventoryApi.Bootstrap;

/// <summary>
/// Records today's connected-machine count for every counted business that has not been counted yet
/// today (issue #978): the daily job of that issue, as a CLI mode of the published
/// <c>InventoryApi</c> executable.
///
/// <para><strong>Why a command rather than a timer inside the API.</strong> docs/architecture.md
/// § Azure Functions and background workloads is explicit about scheduled work under the current
/// decision: put the work in <c>Inventory.Application</c> as a use case, invoke it from the thinnest
/// possible adapter, and let the App Service WebJob of issue #333 - which already runs the published
/// executable on a cron inside the App Service this application runs in - be the schedule. That keeps
/// one writer on the single SQLite file, needs no second host, identity or configuration surface, and
/// makes the job runnable by hand with its output in front of a person. <strong>Scheduling it is a
/// human step</strong> and deliberately not part of this change: this command is the job, and when
/// it should run is an App Service decision an operator takes (README.md § Machines).</para>
///
/// From a source tree, with the SDK installed:
/// <code>
///   dotnet run --project backend/InventoryApi -- snapshot-connected-machines
/// </code>
///
/// From the deployed application, which is <c>dotnet publish</c> output and has no SDK or sources:
/// <code>
///   dotnet InventoryApi.dll snapshot-connected-machines
/// </code>
///
/// <para>It takes <strong>no arguments at all</strong>: no business, no date, no series, no count and
/// no cap. Which businesses are counted is the plan records' answer, the day and the instant come
/// from the clock, the series comes from each business's own stored connection, and the bound is
/// <see cref="ConnectedMachineSnapshotRunLimits.MaxFleetReadsPerRun"/> - a command line that could
/// name any of them would be a way to write billing evidence by hand. It applies no migration and
/// refuses to run against a schema with pending ones.</para>
///
/// <para>Exit code: <c>0</c> when the run completed, including a run that recorded nothing because
/// every business was already counted today; <c>1</c> when the run was refused, or when any
/// business's attempt failed - a scheduled run has nobody watching it, so a failure is visible in the
/// run's own result rather than only in a log line. Nothing is retried here: the next run is the
/// retry, and a business whose read failed keeps its last complete snapshot as a stale count.</para>
/// </summary>
public static class ConnectedMachineSnapshotCommand
{
    public const string CommandName = "snapshot-connected-machines";

    public static bool Matches(string[] args) =>
        args is { Length: > 0 } && string.Equals(args[0], CommandName, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Accepts the command name and nothing else. An unrecognised argument most likely means the
    /// operator believed this job takes something it does not - a business, a date or a limit - and
    /// guessing is not worth the convenience.
    /// </summary>
    public static bool TryParse(string[] args, out string error)
    {
        ArgumentNullException.ThrowIfNull(args);

        error = string.Empty;

        var unrecognised = args.Skip(1).FirstOrDefault();
        if (unrecognised is not null)
        {
            error = $"Unrecognised argument '{unrecognised}'. This job takes no arguments. "
                + $"Usage: {CommandName}";
            return false;
        }

        return true;
    }

    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        if (!TryParse(args, out var argumentError))
        {
            return Refuse(Console.Out, argumentError);
        }

        var builder = WebApplication.CreateBuilder(args);

        var protectionOptions = builder.Configuration
            .GetSection(NayaxTokenProtectionOptions.SectionName)
            .Get<NayaxTokenProtectionOptions>() ?? new NayaxTokenProtectionOptions();

        // Checked before anything is read: without a key no business's stored token can be decrypted,
        // so every attempt would fail one at a time and the run would report a hundred failures for
        // one missing setting.
        if (!protectionOptions.IsConfigured)
        {
            return Refuse(
                Console.Out,
                "No Nayax token encryption key is configured, so no business's stored credentials "
                    + $"can be read. Provision {NayaxTokenProtectionOptions.SectionName}:"
                    + $"{nameof(NayaxTokenProtectionOptions.ActiveKeyId)} and the matching "
                    + $"{NayaxTokenProtectionOptions.SectionName}:"
                    + $"{nameof(NayaxTokenProtectionOptions.Keys)} entry for this environment first. "
                    + "Nothing was read or written.");
        }

        var lynxOptions = builder.Configuration
            .GetSection(NayaxLynxOptions.SectionName)
            .Get<NayaxLynxOptions>() ?? new NayaxLynxOptions();

        try
        {
            Register(builder, protectionOptions, lynxOptions);
        }
        catch (InvalidOperationException configurationError)
        {
            // The same refusals the composition root makes at startup - an absent or non-https Nayax
            // base URL, an incomplete key section - reported with the setting named rather than as a
            // stack trace. Neither message carries key material.
            return Refuse(Console.Out, configurationError.Message);
        }

        using var host = builder.Build();
        using var rootScope = host.Services.CreateScope();
        var dbOptions = rootScope.ServiceProvider.GetRequiredService<DbContextOptions<AppDbContext>>();

        // Deliberately *not* an unrestricted context, for the reason `grandfather-business-plans` and
        // `migrate-nayax-connection` are not (AGENTS.md § Tenant ownership and data isolation). The
        // Businesses table carries no tenant query filter, so a denied context enumerates it while
        // reaching no tenant-owned row; every snapshot is then recorded through a scope resolved to
        // the one business it belongs to.
        await using var tenancyDb = new AppDbContext(dbOptions, new BusinessScope());

        var businessScopes = new List<IServiceScope>();

        try
        {
            var job = new ConnectedMachineSnapshotJob(
                tenancyDb,
                businessId => RecorderFor(host, businessId, businessScopes),
                line => Console.Out.WriteLine(line));

            var report = await job.RunAsync(cancellationToken);

            Write(report, Console.Out);

            return report.Succeeded && report.Failures == 0 ? 0 : 1;
        }
        finally
        {
            foreach (var businessScope in businessScopes)
            {
                businessScope.Dispose();
            }
        }
    }

    /// <summary>
    /// Everything the job needs, registered through the composition root's own extensions wherever
    /// one exists, so the job reads a credential exactly the way a request does and there is no
    /// second opinion about how a Nayax call is made.
    ///
    /// <para>Only what this job uses is registered - the connection, the subscription records, the
    /// snapshots, the clock, the token protector and the Nayax client - rather than the whole API: a
    /// command that composed every use case would be a command that fails to start over a dependency
    /// it never calls.</para>
    /// </summary>
    private static void Register(
        WebApplicationBuilder builder,
        NayaxTokenProtectionOptions protectionOptions,
        NayaxLynxOptions lynxOptions)
    {
        builder.Services.AddDbContext<AppDbContext>(options =>
        {
            options.UseSqlite(builder.Configuration.GetConnectionString("DefaultConnection")
                ?? "Data Source=inventory.db");
        });

        // The per-scope current business, published here by this job rather than by
        // BusinessScopeMiddleware, and resolved from the database's own business keys.
        builder.Services.AddScoped<BusinessScope>();
        builder.Services.AddScoped<IBusinessScope>(sp => sp.GetRequiredService<BusinessScope>());
        builder.Services.AddSingleton<IClock, SystemClock>();

        builder.Services.AddNayaxTokenProtection(protectionOptions);
        builder.Services.AddNayaxLynxClient(lynxOptions);

        builder.Services.AddScoped<INayaxConnectionStore, EfNayaxConnectionStore>();
        builder.Services.AddScoped<INayaxRequestCredentialProvider, NayaxRequestCredentialProvider>();
        builder.Services.AddScoped<IConnectedMachineSnapshotStore, EfConnectedMachineSnapshotStore>();
        builder.Services.AddScoped<IManualPlanGrantStore, EfManualPlanGrantStore>();
        builder.Services.AddScoped<IBillingSubscriptionStore, EfBillingSubscriptionStore>();
        builder.Services.AddScoped<IConnectedMachineSnapshotRecorder, RecordConnectedMachineSnapshot>();
    }

    /// <summary>
    /// The recorder for one business, in a scope of its own whose current business is that business.
    ///
    /// <para>The scope is what makes this job indistinguishable from a request at the persistence
    /// boundary: the context, the tenant query filters, the ownership stamp, the credential read and
    /// the Nayax call are all the ones a member of that business would get. The business id comes
    /// from the row the enumeration read, never from a command line.</para>
    /// </summary>
    private static IConnectedMachineSnapshotRecorder RecorderFor(
        IHost host,
        int businessId,
        List<IServiceScope> openedScopes)
    {
        var businessScope = host.Services.CreateScope();
        openedScopes.Add(businessScope);

        businessScope.ServiceProvider
            .GetRequiredService<BusinessScope>()
            .Resolve(BusinessId.From(businessId));

        return businessScope.ServiceProvider.GetRequiredService<IConnectedMachineSnapshotRecorder>();
    }

    private static int Refuse(TextWriter output, string reason)
    {
        output.WriteLine();
        output.WriteLine($"Connected-machine snapshots - REFUSED: {reason}");
        output.WriteLine();
        return 1;
    }

    /// <summary>
    /// Prints what the run did, per business and in total.
    ///
    /// <para>Nothing printed here is a secret: business keys and outcomes are the platform's own
    /// operational data, and no business name, operator id, token, machine id or count is shown. A
    /// count belongs to the business it is about, not to a job log.</para>
    /// </summary>
    public static void Write(ConnectedMachineSnapshotJobReport report, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(output);

        output.WriteLine();
        output.WriteLine("Connected-machine snapshots - daily run");
        output.WriteLine(new string('-', 78));

        if (report.Refusal is { } refusal)
        {
            output.WriteLine($"FAILED: {refusal}");
            output.WriteLine();
            return;
        }

        output.WriteLine($"Businesses considered : {Count(report.Businesses.Count)}");
        output.WriteLine($"  snapshots recorded  : {Count(report.Recorded)}");
        output.WriteLine($"  skipped             : {Count(report.Skipped)}");
        output.WriteLine($"  failed              : {Count(report.Failures)}");
        output.WriteLine($"Nayax fleet reads     : {Count(report.FleetReads)}");
        output.WriteLine();

        foreach (var entry in report.Businesses)
        {
            output.WriteLine($"  business {Count(entry.BusinessId)}: {Describe(entry)}");
        }

        output.WriteLine();

        if (report.CapReached)
        {
            output.WriteLine(
                "The run stopped at its cap of "
                    + Count(ConnectedMachineSnapshotRunLimits.MaxFleetReadsPerRun)
                    + " fleet reads; the businesses it did not reach keep their last complete "
                    + "snapshot, reported as stale with its age.");
            output.WriteLine();
        }
    }

    private static string Describe(ConnectedMachineSnapshotJobEntry entry) =>
        entry switch
        {
            { Failed: true } => "the fleet read or the record failed; nothing was stored",
            { Outcome: ConnectedMachineSnapshotOutcome.Recorded } => "today's snapshot recorded",
            { Outcome: ConnectedMachineSnapshotOutcome.AlreadyRecorded } =>
                "already counted today for its current account; no call made",
            { Outcome: ConnectedMachineSnapshotOutcome.SupersededByAnotherRead } =>
                "another complete read recorded today's snapshot first; this one was discarded",
            { Outcome: ConnectedMachineSnapshotOutcome.IncompleteRead } =>
                "the fleet read was not complete; nothing was stored",
            { Outcome: ConnectedMachineSnapshotOutcome.PlanDoesNotCount } =>
                "its plan does not have connected machines counted; no call made",
            { Outcome: ConnectedMachineSnapshotOutcome.ConnectionUnavailable } =>
                "no usable Nayax connection; no call made",
            _ => "no outcome recorded",
        };

    private static string Count(int value) => value.ToString(CultureInfo.InvariantCulture);
}
