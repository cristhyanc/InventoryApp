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
/// <para><strong>No state changes what counts, in this issue.</strong> Stock consumption, costing
/// and revenue continue to follow the saved <c>TransactionStatusId</c> through
/// <c>NayaxTransactionStatusClassifier</c>, so a <see cref="Conflict"/> and a
/// <see cref="NeedsInvestigation"/> row count exactly as they did before. Excluding a row and the
/// Owner decisions that could are issue #1105's.</para>
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
}
