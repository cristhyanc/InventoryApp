using Inventory.Domain.Subscriptions;

namespace InventoryApi.Bootstrap;

/// <summary>
/// How a <c>grandfather-business-plans</c> run ended (issue #966).
/// </summary>
public enum BusinessPlanGrandfatheringOutcome
{
    /// <summary>
    /// The run did what it set out to do: a dry run reported a plan, or an apply granted every
    /// eligible business and recorded the completion marker.
    /// </summary>
    Succeeded = 10,

    /// <summary>
    /// The step had already completed, so this run changed nothing and reports that. The expected
    /// outcome of running it twice.
    /// </summary>
    AlreadyComplete = 20,

    /// <summary>
    /// The database has pending migrations, so the subscription tables may not exist or may not be
    /// at the expected shape. Nothing was read or written.
    /// </summary>
    SchemaNotReady = 30,
}

/// <summary>
/// One business as the run decided about it (issue #966).
/// </summary>
/// <param name="BusinessId">The business's own key, from the database the run read it in.</param>
/// <param name="Decision">
/// The eligibility rule's answer, from <see cref="GrandfatheringEligibilityPolicy.Decide"/>.
/// </param>
/// <param name="Granted">
/// Whether this run wrote the grandfathered grant. False for every ineligible business, and for an
/// eligible one that already held the grant - which is what a rerun reports.
/// </param>
/// <param name="AlreadyHeld">Whether the business already held the grandfathered grant.</param>
public sealed record BusinessPlanGrandfatheringEntry(
    int BusinessId,
    GrandfatheringEligibilityPolicy.Decision Decision,
    bool Granted,
    bool AlreadyHeld);

/// <summary>
/// What a <c>grandfather-business-plans</c> run did, or would do (issue #966). It is the value the
/// command prints and the value its tests assert on, so "a dry run writes nothing" and "a rerun
/// changes nothing" are checkable claims rather than prose.
/// </summary>
/// <param name="DryRun">Whether this was a dry run, which writes nothing at all.</param>
/// <param name="Outcome">How the run ended.</param>
/// <param name="Refusal">The operator-facing refusal, or <see langword="null"/> on success.</param>
/// <param name="CutoffUtc">
/// The cutoff the run decided eligibility against: the recorded one when there is one, and - on a
/// dry run with no record yet - the instant this run <em>would</em> record. Null only when the run
/// was refused before reading anything.
/// </param>
/// <param name="CutoffWasAlreadyRecorded">
/// Whether that cutoff came from the stored record rather than from this run's clock. It is what an
/// operator checks to see that a rerun is grandfathering by the original instant.
/// </param>
/// <param name="Businesses">
/// Every business the run considered, in business-id order, with the rule's decision about it.
/// </param>
/// <param name="CompletionMarkerWritten">
/// Whether this run wrote the completion marker. False for a dry run, for a refusal, and for a
/// rerun after completion - and it is the one thing that lets enforcement take effect.
/// </param>
public sealed record BusinessPlanGrandfatheringReport(
    bool DryRun,
    BusinessPlanGrandfatheringOutcome Outcome,
    string? Refusal,
    DateTime? CutoffUtc,
    bool CutoffWasAlreadyRecorded,
    IReadOnlyList<BusinessPlanGrandfatheringEntry> Businesses,
    bool CompletionMarkerWritten)
{
    /// <summary>Whether the run is one an operator may treat as clean.</summary>
    public bool Succeeded =>
        Outcome is BusinessPlanGrandfatheringOutcome.Succeeded
            or BusinessPlanGrandfatheringOutcome.AlreadyComplete;

    /// <summary>The businesses the rule grandfathers, whether or not they already held the grant.</summary>
    public IReadOnlyList<BusinessPlanGrandfatheringEntry> Eligible =>
        [.. Businesses.Where(entry =>
            entry.Decision == GrandfatheringEligibilityPolicy.Decision.Eligible)];

    /// <summary>The businesses this run granted the plan to.</summary>
    public IReadOnlyList<BusinessPlanGrandfatheringEntry> Granted =>
        [.. Businesses.Where(entry => entry.Granted)];

    /// <summary>The eligible businesses that already held the grant, so nothing was written.</summary>
    public IReadOnlyList<BusinessPlanGrandfatheringEntry> AlreadyHeld =>
        [.. Businesses.Where(entry => entry.AlreadyHeld)];

    /// <summary>The businesses the rule refused, each with the rule that refused it.</summary>
    public IReadOnlyList<BusinessPlanGrandfatheringEntry> NotEligible =>
        [.. Businesses.Where(entry =>
            entry.Decision != GrandfatheringEligibilityPolicy.Decision.Eligible)];
}
