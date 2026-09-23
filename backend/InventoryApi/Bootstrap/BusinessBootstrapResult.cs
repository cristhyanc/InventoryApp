namespace InventoryApi.Bootstrap;

/// <summary>Why a bootstrap refused to run, or that it succeeded.</summary>
public enum BusinessBootstrapOutcome
{
    /// <summary>The run completed (or, in a dry run, verified) successfully.</summary>
    Succeeded = 0,

    /// <summary>
    /// The <c>BusinessBootstrap</c> configuration is missing, empty, or malformed. The operation
    /// changes nothing: inventing an identity here is the one thing it must never do.
    /// </summary>
    ConfigurationInvalid = 1,

    /// <summary>
    /// More than one Business already exists, so which one owns the unassigned data is a human
    /// decision rather than something to guess.
    /// </summary>
    AmbiguousExistingBusiness = 2,

    /// <summary>
    /// The before/after verification did not balance - a row count or a financial total moved.
    /// The transaction is rolled back and nothing is written.
    /// </summary>
    VerificationFailed = 3,

    /// <summary>The schema is not at the expected migration level yet.</summary>
    SchemaNotReady = 4,

    /// <summary>
    /// The business would end up owning data while having no active membership, so nobody could
    /// reach it. Every configured actor matched only a revoked membership, and reactivating one
    /// is a human decision the bootstrap must not make on their behalf.
    /// </summary>
    NoActiveMembership = 5,

    /// <summary>
    /// The business that would own the data is deactivated, so its records would be unreachable
    /// however many active members it has. Reactivating a business is a human decision.
    /// </summary>
    BusinessInactive = 6,
}

/// <summary>What one table looked like before and after the backfill touched it.</summary>
public sealed record BusinessBackfillTableReport(
    string TableName,
    long TotalRowsBefore,
    long UnassignedRowsBefore,
    long RowsAssigned,
    long TotalRowsAfter,
    long UnassignedRowsAfter);

/// <summary>
/// One named financial or inventory total, captured before and after the backfill.
///
/// The backfill only ever writes an ownership key, so every one of these must be identical on
/// both sides. They are the evidence that assigning ownership did not disturb money or stock -
/// the thing a reviewer of a high-risk migration actually needs to see.
/// </summary>
public sealed record BusinessBackfillTotalReport(string Name, decimal Before, decimal After)
{
    public bool IsPreserved => Before == After;
}

/// <summary>The complete, reviewable outcome of a bootstrap invocation.</summary>
public sealed record BusinessBootstrapResult
{
    public required BusinessBootstrapOutcome Outcome { get; init; }

    /// <summary>True when this was a dry run and the transaction was rolled back deliberately.</summary>
    public required bool DryRun { get; init; }

    /// <summary>Operator-facing explanation. Never contains an Entra identifier.</summary>
    public required string Message { get; init; }

    public Guid RunId { get; init; }

    public int? BusinessId { get; init; }

    /// <summary>True when this run created the Business row rather than reusing an existing one.</summary>
    public bool BusinessCreated { get; init; }

    /// <summary>Memberships this run added. Re-running an applied bootstrap adds none.</summary>
    public int MembershipsCreated { get; init; }

    public IReadOnlyList<BusinessBackfillTableReport> Tables { get; init; } = [];

    public IReadOnlyList<BusinessBackfillTotalReport> Totals { get; init; } = [];

    public long TotalRowsAssigned => Tables.Sum(table => table.RowsAssigned);

    public bool AllTotalsPreserved => Totals.All(total => total.IsPreserved);

    public bool Succeeded => Outcome == BusinessBootstrapOutcome.Succeeded;
}
