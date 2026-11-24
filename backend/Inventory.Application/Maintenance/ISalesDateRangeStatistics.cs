namespace Inventory.Application.Maintenance;

/// <summary>What one call to <see cref="ISalesDateRangeStatistics.CollectIfStaleAsync"/> did (issue #1287).</summary>
public enum SalesDateRangeStatisticsOutcome
{
    /// <summary>Statistics were collected for the sales date-range index.</summary>
    Collected,

    /// <summary>
    /// The recorded statistics still describe the table, so nothing was collected and nothing took
    /// the write lock.
    /// </summary>
    AlreadyCurrent,

    /// <summary>
    /// There is too little sales history for statistics to change any plan. What a brand-new
    /// database answers.
    /// </summary>
    NotWorthCollecting,

    /// <summary>
    /// The database could not be read or written for this - the write lock was held past the busy
    /// timeout, most often. Nothing was collected, the caller is unaffected, and the next trigger
    /// decides again from the state it finds.
    /// </summary>
    Unavailable,
}

/// <summary>
/// What one call did, with the figures an operator-facing log line reports.
/// </summary>
/// <param name="Outcome">What happened.</param>
/// <param name="RecordedSales">
/// How many sales the statistics said the table held when the decision was made, or
/// <see langword="null"/> when there were none recorded.
/// </param>
/// <param name="CurrentSales">
/// How many sales the table holds now, or <see langword="null"/> when that could not be read.
/// </param>
/// <param name="Duration">How long the collection itself took. Zero when nothing was collected.</param>
/// <param name="FailureType">
/// The type name of what went wrong, for <see cref="SalesDateRangeStatisticsOutcome.Unavailable"/>
/// and never anything else. The type name only: a database message can quote the parameter values of
/// the statement that failed.
/// </param>
public sealed record SalesDateRangeStatisticsResult(
    SalesDateRangeStatisticsOutcome Outcome,
    long? RecordedSales = null,
    long? CurrentSales = null,
    TimeSpan Duration = default,
    string? FailureType = null);

/// <summary>
/// Keeps the SQLite planner statistics the sales date-range index of issue #1287 needs (the
/// repository owner's decision of 24 November 2026).
///
/// <para><strong>Why the application has to do this at all.</strong>
/// <c>IX_NayaxSales_BusinessId_MachineAuthorizationTime</c> is the right index for the fourteen
/// date-range sales reads in <c>Inventory.Infrastructure</c>, but those reads do not name their
/// business in SQL - the central tenant filter is translated as
/// <c>(@unrestricted OR "BusinessId" = @business)</c>. SQLite reaches the index for them by
/// <em>skip-scan</em>, which it enables only for an index that has planner statistics. Without them
/// the planner keeps scanning every sale the business has ever stored, which is what audit #1226
/// measured at 145-226 ms per figure.</para>
///
/// <para><strong>It is a port because the statement is this provider's.</strong> Collecting is
/// <c>ANALYZE</c> over one named index and reading the recorded size is a <c>sqlite_stat1</c> row;
/// neither belongs in a use case. What the use case and the startup step know is "keep the sales
/// date-range statistics current if they are not", which is this one method.</para>
///
/// <para><strong>It reads and writes nothing about any business.</strong> No method takes a business,
/// a predicate, a column list, a page or a sort; nothing tenant-owned is readable through it; and the
/// two numbers that cross the boundary are a count of rows and a count of rows. Collecting changes
/// which plan SQLite picks and never which rows it returns - every business's sales, and every
/// financial figure computed from them, are exactly what they were.</para>
///
/// <para><strong>Nothing here decides when.</strong> That is
/// <see cref="Inventory.Domain.Maintenance.SalesDateRangeStatisticsPolicy"/>'s, from the two numbers
/// the adapter reads, and the two triggers that ask: API startup, and the completion of a background
/// sales history import (issue #1304), the one path that can add a quarter of a million sales at
/// once.</para>
/// </summary>
public interface ISalesDateRangeStatistics
{
    /// <summary>
    /// Collects the statistics if the policy says they are missing or stale, and does nothing
    /// otherwise.
    ///
    /// <para><strong>It never throws for a database failure</strong>, because neither of its callers
    /// may fail for one: statistics are an optimisation, so a held write lock must not stop the API
    /// from starting or turn a completed import into a failed one. A failure is reported as
    /// <see cref="SalesDateRangeStatisticsOutcome.Unavailable"/> and tried again at the next trigger.
    /// Cancellation is not a failure and does propagate, so a host that is stopping stops.</para>
    /// </summary>
    Task<SalesDateRangeStatisticsResult> CollectIfStaleAsync(CancellationToken cancellationToken);
}
