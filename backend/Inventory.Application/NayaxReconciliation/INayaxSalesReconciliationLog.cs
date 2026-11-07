namespace Inventory.Application.NayaxReconciliation;

/// <summary>
/// The hourly reconciliation's log sink, behind a port so the Application layer names what happened
/// rather than how it is written down (issue #1104) - the shape <c>INayaxSqsReaderLog</c> has.
///
/// <para><strong>Counts only.</strong> A business key and an outcome are the platform's own
/// operational data; a transaction id, an amount, a machine name, a currency or a product is that
/// business's sales data and belongs in its own rows rather than in a log an operator reads. That is
/// not a preference: a log line is the one artefact of this run that leaves the business's own
/// boundary.</para>
/// </summary>
public interface INayaxSalesReconciliationLog
{
    /// <summary>The runner started, before its first delay.</summary>
    void ReconciliationStarted();

    /// <summary>The runner's loop ended, which only cancellation does.</summary>
    void ReconciliationStopped();

    /// <summary>One sweep is about to run, for this many eligible businesses.</summary>
    void SweepStarted(int eligibleBusinesses);

    /// <summary>
    /// What one business's run did: its key, its outcome and the counts
    /// <see cref="NayaxSalesReconciliationResult"/> carries.
    /// </summary>
    void BusinessReconciled(int businessId, NayaxSalesReconciliationResult result);

    /// <summary>
    /// One business's run ended on something the use case did not turn into an outcome - which it
    /// should not, so this is the backstop that keeps one business's trouble from stopping the rest.
    /// The <em>type name</em> only: an exception message can quote what it was given.
    /// </summary>
    void BusinessFailed(int businessId, string failureType);

    /// <summary>
    /// The eligibility read itself failed, so this sweep ran for nobody. The next hour is the retry.
    /// </summary>
    void EligibilityReadFailed(string failureType);
}
