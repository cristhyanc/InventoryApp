using Inventory.Domain.Purchases;

namespace Inventory.Application.Purchases;

/// <summary>
/// The idempotency key one purchase create is claimed under, with the fingerprint of the request it
/// stands for (issue #1247). Both are written on the purchase the create records, so a later
/// request under the same key can be told apart from the same request arriving twice.
/// </summary>
public sealed record PurchaseIdempotencyClaim(string Key, string Fingerprint);

/// <summary>
/// What was already recorded under an idempotency key: the fingerprint stored beside it, and the
/// purchase it saved.
///
/// <para><see cref="Fingerprint"/> is nullable because the column is: a stored key with no
/// fingerprint is a state nothing in this application writes, and it is treated as "not the same
/// request" rather than as a match, which refuses a retry instead of replaying a purchase nobody
/// can show is the same one.</para>
/// </summary>
public sealed record PurchaseIdempotentCreate(string? Fingerprint, PurchaseRecord Purchase);

/// <summary>
/// The result of a create: the purchase, and whether this request wrote it.
///
/// <para><see cref="Replayed"/> is <see langword="true"/> when an earlier request under the same
/// idempotency key had already saved it, so this request wrote nothing at all - no purchase, no
/// line, no stock movement and no cost rebuild. The caller uses it to clean up the bytes and the
/// storage reservation its own attempt had staged for a document that is not going to be
/// recorded.</para>
/// </summary>
public sealed record PurchaseCreateOutcome(PurchaseRecord Purchase, bool Replayed);

/// <summary>
/// The one place the purchase create refuses an idempotency key it cannot store and turns the one
/// it accepts into a claim (issue #1247), beside <see cref="PurchaseInvoiceNumberSubmission"/>.
///
/// <para>It runs before any state changes - before <see cref="UploadPurchase"/> reserves storage,
/// saves the uploaded document or opens the write transaction - so an over-long key leaves the
/// purchase, its lines, its stock movements and the document storage exactly as they were.</para>
/// </summary>
public static class PurchaseIdempotencySubmission
{
    /// <summary>
    /// The claim for <paramref name="submitted"/> over <paramref name="content"/>, or
    /// <see langword="null"/> when no key was submitted - which is every client that predates this
    /// issue, and is the create that behaves exactly as it always has.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The submitted key is too long. It is this exception rather than a
    /// <c>DomainValidationException</c> because the purchase create endpoint answers its own
    /// refusals as a plain <c>400</c> message, the way the invoice-number rule beside it already
    /// does; the conflict a spent key produces is a <c>DomainConflictException</c>, because that one
    /// is about stored state and is mapped centrally to <c>409</c>.
    /// </exception>
    public static PurchaseIdempotencyClaim? Claim(string? submitted, PurchaseFingerprintContent content)
    {
        ArgumentNullException.ThrowIfNull(content);

        if (!PurchaseIdempotencyPolicy.IsWithinLimit(submitted))
            throw new InvalidOperationException(PurchaseIdempotencyPolicy.KeyTooLongMessage);

        return PurchaseIdempotencyPolicy.Normalize(submitted) is { } key
            ? new PurchaseIdempotencyClaim(key, PurchaseCreateFingerprint.Compute(content))
            : null;
    }

    /// <summary>
    /// Whether <paramref name="recorded"/> is what <paramref name="claim"/> is asking to record
    /// again. Ordinal, and a missing stored fingerprint never matches.
    /// </summary>
    public static bool IsSameRequest(PurchaseIdempotencyClaim claim, PurchaseIdempotentCreate recorded)
    {
        ArgumentNullException.ThrowIfNull(claim);
        ArgumentNullException.ThrowIfNull(recorded);

        return recorded.Fingerprint is { } stored
            && string.Equals(stored, claim.Fingerprint, StringComparison.Ordinal);
    }
}
