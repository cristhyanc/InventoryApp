namespace Inventory.Domain.NayaxSqs;

/// <summary>
/// How far a sale that arrived from the shared Nayax SQS queue has been checked against the
/// provider's own record of it (issue #1103, for issue #1104).
///
/// <para><strong>Every value is recorded, never derived.</strong> A sale the queue reader stored
/// carries <see cref="Unverified"/> from the moment it is inserted, in the same insert as the rest
/// of its provenance, so "this row came from the queue and nothing has confirmed it yet" is a stored
/// fact rather than something a later reader infers from a null column. A row that predates the
/// queue reader - every sale the <c>lastSales</c> sync or an uploaded export imported - carries no
/// value at all, which is what keeps "not from the queue" and "from the queue, unverified" apart.</para>
///
/// <para>Issue #1104 is what adds the verified and conflicting states and the comparison that
/// decides them. Nothing in issue #1103 moves a sale out of <see cref="Unverified"/>, and no
/// financial rule reads this column: stock consumption, costing and revenue continue to follow the
/// saved <c>TransactionStatusId</c> through <c>NayaxTransactionStatusClassifier</c>.</para>
///
/// <para>No value is <c>0</c>, for the reason <c>BusinessStatus</c> declares none: <c>0</c> is what
/// an unfilled integer column and a default-constructed enum both hold, so a value nothing wrote
/// must not look like a state somebody decided.</para>
/// </summary>
public enum NayaxSqsVerification
{
    /// <summary>
    /// The sale was stored from a queue message and has not been compared with the provider's own
    /// record of the transaction. The only value issue #1103 writes.
    /// </summary>
    Unverified = 10,
}
