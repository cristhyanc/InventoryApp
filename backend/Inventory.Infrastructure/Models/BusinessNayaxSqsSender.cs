using System.Text.Json.Serialization;

namespace Inventory.Infrastructure.Models;

/// <summary>
/// Which AWS IAM user sends one business's Nayax sales to the shared Restockroo SQS queue (issue
/// #1101, child of #1100, under the owner's decision of 5 November 2026: one shared queue, one
/// send-only IAM user per business, and messages attributed by the SQS <c>SenderId</c> system
/// attribute).
///
/// <para><strong>Tenant-owned, with nothing added</strong>, exactly as
/// <see cref="BusinessPlanGrant"/> is: which sender belongs to a business is a fact about that one
/// business, so <c>AppDbContext</c> gives it the business key, the index, the query filter and the
/// ownership stamp. It is not a platform table, and it is asserted as owned rather than declared as
/// one.</para>
///
/// <para><strong>Written only by a Super Admin, and never by the business.</strong> A business that
/// could choose its own sender id could claim another business's sales, which is the whole reason
/// this row is not on any member-facing surface. The platform-admin request that writes it carries a
/// <em>denied</em> <c>BusinessScope</c>, so it is written through the narrow write lease
/// <c>AppDbContext.BeginNayaxSqsSenderWrite</c> and read through the business-pinned
/// <c>IgnoreQueryFilters</c> statements of <c>EfPlatformNayaxSqsSenderStore</c> - both reviewed on
/// this issue (AGENTS.md § Tenant ownership and data isolation).</para>
///
/// <para><strong>At most one row per business, and one business per sender.</strong> A filtered
/// unique index over <see cref="AwsSenderId"/> <em>alone</em> is what makes "this id belongs to one
/// business" true without ever reading another business's row - the one declared exception to
/// "uniqueness is per business", because the value is set by a platform administrator rather than by
/// a business and the queue reader (#1103) matches it with no business to narrow the lookup by. The
/// index ignores nulls, so a removed sender id is immediately free for another business.</para>
///
/// <para><strong>Nothing deletes a row.</strong> "Remove" is an update that clears
/// <see cref="AwsSenderId"/>, switches <see cref="IsEnabled"/> off, increments
/// <see cref="Revision"/> and appends a <see cref="BusinessNayaxSqsSenderAudit"/> row - so the
/// history of what was recorded survives, and a later "Set" reuses this row with that history.</para>
///
/// <para><strong>The sender id is an identifier, never a secret.</strong> No AWS access key, secret
/// or session token is stored here or anywhere else in this application.</para>
/// </summary>
public class BusinessNayaxSqsSender : IBusinessOwned
{
    /// <summary>The row's own key, assigned by the database.</summary>
    public int Id { get; set; }

    /// <inheritdoc />
    [JsonIgnore]
    public int BusinessId { get; set; }

    /// <summary>
    /// The AWS IAM user id SQS reports as a message's <c>SenderId</c>, trimmed and otherwise exactly
    /// as the administrator entered it, or <c>null</c> when none is recorded - which is both a row
    /// that has never had one and a sender that was removed.
    ///
    /// <para>It is matched exactly, so it is stored exactly: no case folding and no normalised
    /// duplicate column. An administrator finds the value with
    /// <c>aws iam get-user --user-name &lt;user&gt; --query User.UserId</c>.</para>
    /// </summary>
    public string? AwsSenderId { get; set; }

    /// <summary>
    /// Whether this mapping is in force. It can be <see langword="true"/> only while
    /// <see cref="AwsSenderId"/> is not <c>null</c> - checked by
    /// <c>Inventory.Domain.NayaxSqs.NayaxSqsSenderPolicy</c> before anything is written, and backed
    /// by a database check constraint so no path can leave an enabled row with nothing to match on.
    /// </summary>
    public bool IsEnabled { get; set; }

    /// <summary>
    /// When a message from this sender was last accepted, or <c>null</c> for one that has never sent
    /// anything. Written by the queue reader of issue #1103 and never by this surface, which only
    /// reports it.
    /// </summary>
    public DateTime? LastMessageReceivedAtUtc { get; set; }

    /// <summary>
    /// How many times this surface has written the row, starting at <c>1</c> for the write that
    /// created it.
    ///
    /// <para>It is the whole of the optimistic concurrency check: every write presents the revision
    /// it read - or <c>null</c> to create the first row - the comparison happens inside the write
    /// transaction, and a write carrying any other value is refused with <c>409</c> rather than
    /// overwriting a decision somebody else committed in between. It is never a timestamp and never
    /// a provider row version: an integer this application increments is comparable, loggable and
    /// identical on every provider (issue #982's shape, reused).</para>
    /// </summary>
    public int Revision { get; set; }

    /// <summary>When the row was created, from the application clock.</summary>
    public DateTime CreatedAtUtc { get; set; }

    /// <summary>When it was last written, from the application clock.</summary>
    public DateTime UpdatedAtUtc { get; set; }
}
