using Microsoft.Data.Sqlite;

namespace Inventory.Infrastructure.Persistence;

/// <summary>
/// The one place the purchase create's per-business idempotency key index is recognised in a
/// database failure (issue #1247), the purchase's counterpart of
/// <see cref="PickListRefillWriteLock.IsDuplicateIdempotencyKey"/>.
///
/// <para>It lives in Infrastructure because the exception is provider-specific and a provider's
/// exception type must not cross into the Application layer (docs/architecture.md § Domain and
/// application error mapping). The adapter asks rather than catching broadly, so the index violation
/// becomes the same stable refusal the claim inside the write transaction produces, while any other
/// database failure keeps propagating untranslated. It is the last line of defence behind that
/// claim: a database this process did not serialise with would otherwise surface a <c>500</c> for a
/// refusal the contract answers with <c>409</c>.</para>
/// </summary>
internal static class PurchaseIdempotencyKeyIndex
{
    /// <summary>
    /// The columns of the per-business unique index on the key, as SQLite names them in a
    /// constraint-violation message. Both have to appear, so a violation of any other constraint on
    /// this table - issue #976's all-or-nothing document check, for one - is left untranslated.
    /// </summary>
    private static readonly string[] IndexColumns = ["Receipts.BusinessId", "Receipts.IdempotencyKey"];

    /// <summary>SQLITE_CONSTRAINT, the primary result code a unique-index violation reports.</summary>
    private const int SqliteConstraintViolation = 19;

    /// <summary>
    /// Whether <paramref name="exception"/> is the database refusing a second purchase under one
    /// business's idempotency key.
    /// </summary>
    public static bool IsViolatedBy(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is not SqliteException sqlite || sqlite.SqliteErrorCode != SqliteConstraintViolation)
            {
                continue;
            }

            if (IndexColumns.All(column => sqlite.Message.Contains(column, StringComparison.Ordinal)))
            {
                return true;
            }
        }

        return false;
    }
}
