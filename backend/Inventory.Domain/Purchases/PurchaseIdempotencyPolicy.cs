namespace Inventory.Domain.Purchases;

/// <summary>
/// The deterministic rules about the idempotency key a purchase create may carry (issue #1247),
/// following the pick list's <c>Inventory.Domain.Stock.PickListRefillPolicy</c>.
///
/// <para><strong>Why a purchase needs one.</strong> A create records the purchase, its lines, a
/// restock movement per line and the cost rebuild those movements cause. A phone that loses the
/// response and retries, or an operator who presses Save twice on one receipt, would record all of
/// that a second time - doubling the stock and the inventory value of every product on the receipt.
/// A key makes the second request a question about the first rather than a second purchase.</para>
///
/// <para><strong>The key is optional, and that is deliberate.</strong> Every client that existed
/// before this issue sends none, and a create with no key behaves exactly as it always has: nothing
/// is claimed, nothing is compared and nothing is stored in the key's column. Only a request that
/// carries one gets the guarantee, which is why the stored column is nullable and its unique index
/// is filtered to the rows that have one.</para>
///
/// <para><strong>What the two answers are.</strong> The same key with the same content is the retry
/// the key exists for: the first purchase is returned and nothing at all is written. The same key
/// with <em>different</em> content is refused with <see cref="KeyReusedMessage"/> - the repository
/// owner's decision of 14 November 2026 - because recording it would be exactly the second purchase
/// the key was sent to prevent, and silently answering with the first one would hide a purchase the
/// operator believes they saved. What "the same content" means is
/// <see cref="PurchaseCreateFingerprint"/>'s answer, not this policy's.</para>
/// </summary>
public static class PurchaseIdempotencyPolicy
{
    /// <summary>
    /// The longest key the contract accepts, and the stored column's length. A key is a
    /// client-chosen opaque string - a UUID in this application's own client - and bounding it keeps
    /// an unbounded value out of an indexed column. It is the pick list's limit, because it is the
    /// same kind of value sent by the same browser.
    /// </summary>
    public const int MaximumKeyLength = 128;

    /// <summary>What a caller is told when the submitted key is longer than that.</summary>
    public static readonly string KeyTooLongMessage =
        $"An idempotency key cannot be longer than {MaximumKeyLength} characters.";

    /// <summary>
    /// What a caller is told when the key they sent already saved a <em>different</em> purchase.
    /// Reloading is the only safe way forward: the purchase under that key is already recorded, so
    /// the form on screen is a second, unsaved purchase whose key is spent, and the operator has to
    /// see what was actually stored before deciding what to do.
    /// </summary>
    public const string KeyReusedMessage =
        "This purchase was already saved with different details. Reload the page.";

    /// <summary>
    /// The value to hold a caller to: the trimmed key, or <see langword="null"/> when nothing but
    /// whitespace was submitted - which is the same request as sending no key at all, so a blank
    /// form field never claims the empty string for a whole business.
    /// </summary>
    public static string? Normalize(string? submitted) =>
        string.IsNullOrWhiteSpace(submitted) ? null : submitted.Trim();

    /// <summary>
    /// Whether <paramref name="submitted"/> is short enough to store, judged on the value
    /// <see cref="Normalize"/> would produce, so surrounding whitespace never makes an acceptable
    /// key too long.
    /// </summary>
    public static bool IsWithinLimit(string? submitted) => (Normalize(submitted)?.Length ?? 0) <= MaximumKeyLength;
}
