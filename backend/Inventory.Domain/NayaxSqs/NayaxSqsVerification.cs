namespace Inventory.Domain.NayaxSqs;

/// <summary>
/// How far a sale that arrived from the shared Nayax SQS queue has been checked against the
/// provider's own record of it (issue #1103, extended by issue #1104).
///
/// <para><strong>Every value is recorded, never derived.</strong> A sale the queue reader stored
/// carries <see cref="Unverified"/> from the moment it is inserted, in the same insert as the rest
/// of its provenance, so "this row came from the queue and nothing has confirmed it yet" is a stored
/// fact rather than something a later reader infers from a null column. A row that predates the
/// queue reader - every sale the <c>lastSales</c> sync or an uploaded export imported - carries no
/// value at all, which is what keeps "not from the queue" and "from the queue, unverified" apart.
/// Issue #1104's hourly reconciliation adds rows that only <c>lastSales</c> delivered to that same
/// group: they carry no value either, because there is no queue delivery to verify.</para>
///
/// <para><strong>Issue #1104 is what moves a sale out of <see cref="Unverified"/>, and only on
/// provider evidence.</strong> The hourly reconciliation compares the business's own <c>lastSales</c>
/// with each stored queue row and records what it saw as a
/// <c>NayaxSaleProviderEvidence</c> row; the state and the evidence are written in one write-locked
/// transaction, so the state can never say something no evidence supports.
/// <see cref="NayaxSqsVerificationTransitions"/> is the whole of which moves are permitted.</para>
///
/// <para><strong>Two states change what counts (issue #1105).</strong> A completed sale is status
/// <c>12</c> <em>and</em> not <see cref="Conflict"/> or <see cref="OwnerExcluded"/> - see
/// <see cref="NayaxSaleCountingPolicy"/>, the one place that rule is written. Every other state,
/// <see cref="NeedsInvestigation"/> included, counts exactly as the status alone says: an absence
/// from <c>lastSales</c> is never proof, so it never excludes anything.</para>
///
/// <para>No value is <c>0</c>, for the reason <c>BusinessStatus</c> declares none: <c>0</c> is what
/// an unfilled integer column and a default-constructed enum both hold, so a value nothing wrote
/// must not look like a state somebody decided. The values are spaced so a further state can be
/// declared between two of them without renumbering one already persisted on a sale.</para>
/// </summary>
public enum NayaxSqsVerification
{
    /// <summary>
    /// The sale was stored from a queue message and has not been compared with the provider's own
    /// record of the transaction. The only value issue #1103 writes, and the state every compared
    /// sale starts from.
    /// </summary>
    Unverified = 10,

    /// <summary>
    /// Provider-verified: the business's own <c>lastSales</c> returned the same
    /// <c>TransactionID</c>, and the machine, the amount and the authorization instant were all
    /// compared and were equal. The currency and the product were either equal too or are recorded
    /// as not compared - a field nobody could compare is never counted as agreement.
    /// </summary>
    Verified = 20,

    /// <summary>
    /// An <strong>authoritative contradiction</strong>: the business's own <c>lastSales</c> returned
    /// the same <c>TransactionID</c> with a different machine, amount or product, and both values
    /// were present.
    ///
    /// <para>It is terminal for this reconciliation. Later matching evidence does not unsay a
    /// contradiction - it is appended beside it - and only an Owner decision (issue #1105) moves a
    /// row out of this state.</para>
    /// </summary>
    Conflict = 30,

    /// <summary>
    /// Something a person should look at, and deliberately not a contradiction about what was sold:
    /// the authorization instant or the currency differed, or <c>lastSales</c> did not return the
    /// sale at all while returning items on both sides of its instant, more than the receipt grace
    /// after the sale arrived.
    ///
    /// <para>An absence never means the sale is fake. Nayax documents <c>lastSales</c> as "the most
    /// recent sales" with no promised window, so a transaction missing from it is not evidence that
    /// the transaction did not happen (see <see cref="NayaxSaleProviderAbsencePolicy"/>).</para>
    /// </summary>
    NeedsInvestigation = 40,

    /// <summary>
    /// The business Owner reviewed the sale and decided it stands as stored (issue #1105): either
    /// "Keep the SQS values" on a <see cref="Conflict"/>, or "This sale is genuine" on a
    /// <see cref="NeedsInvestigation"/> row. It counts as a completed sale again.
    ///
    /// <para>It is <strong>never</strong> Nayax verification. It is shown as "Confirmed by owner",
    /// and only later provider evidence that contradicts the decided values - a comparison that
    /// differs from the one the decision relied on - moves it back to a flagged state.</para>
    /// </summary>
    OwnerConfirmed = 50,

    /// <summary>
    /// The business Owner chose "Use Nayax's values" on a <see cref="Conflict"/> (issue #1105): the
    /// stored machine, amount, instant and product were replaced from the provider's own record of
    /// the transaction. It counts as a completed sale again, and is shown as "Confirmed by owner",
    /// never as <see cref="Verified"/>.
    /// </summary>
    OwnerCorrected = 60,

    /// <summary>
    /// The business Owner chose "Don't count this sale" (issue #1105). The row stays stored and
    /// visible in status and data-quality reporting, and is excluded from every completed-sale
    /// total, from costing and from the machine-stock ledger exactly as a <see cref="Conflict"/> is.
    /// </summary>
    OwnerExcluded = 70,
}
