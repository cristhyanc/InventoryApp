namespace Inventory.Domain.NayaxSqs;

/// <summary>
/// What one piece of provider evidence about a sale <em>is</em> (issue #1104).
///
/// <para><strong>The two kinds answer different questions, and keeping them apart is the point.</strong>
/// A <see cref="Comparison"/> is the provider's own record of the same transaction, field by field. An
/// <see cref="Absence"/> is the much weaker statement that the provider's response for that machine
/// did not include the sale, recorded together with what it <em>did</em> include - because
/// <c>lastSales</c> promises no complete window and an absence is therefore never proof of anything
/// (see <see cref="NayaxSaleProviderAbsencePolicy"/>). Collapsing them into one kind would let an
/// absence be read with a comparison's weight.</para>
///
/// <para>"The latest evidence of the same kind" is also what decides whether a repeated observation
/// is written at all, so the kind is part of the deduplication rather than a label on it.</para>
///
/// <para>No value is <c>0</c>, for the reason <see cref="NayaxSqsVerification"/> declares none.</para>
/// </summary>
public enum NayaxSaleProviderEvidenceKind
{
    /// <summary>
    /// <c>lastSales</c> returned an item with the same <c>TransactionID</c>. The row stores that
    /// item's values exactly as received and each field's own comparison result.
    /// </summary>
    Comparison = 10,

    /// <summary>
    /// <c>lastSales</c> was read for the sale's machine and did not return the sale. The row stores
    /// what the response did return for that machine, the sale's own instants, and the two
    /// observations - bracketed and outside the receipt grace - that made it worth recording. It
    /// claims nothing about the response being complete.
    /// </summary>
    Absence = 20,
}
