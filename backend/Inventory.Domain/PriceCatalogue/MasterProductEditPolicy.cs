using Inventory.Domain.Exceptions;

namespace Inventory.Domain.PriceCatalogue;

/// <summary>
/// The rules of managing one shared master product from the Master Products page (issue #1024, parent
/// #817): what counts as a change of <em>size</em>, when such a change is refused, and when an edit is
/// answering a page that has moved on.
///
/// <para><strong>It is <see cref="MasterProductPolicy"/>'s companion, not its replacement.</strong>
/// What a brand, a name and a size may <em>be</em>, and the normalised key two descriptions are
/// compared by, are that policy's and are unchanged - a master product created here is held to exactly
/// the rules the Super Admin review's own create is held to. What is here is only what managing an
/// <em>existing</em> row adds: the safeguard over its size and the refusal of a stale edit.</para>
///
/// <para><strong>The size safeguard is financial.</strong> A supplier listing is confirmed with a pack
/// quantity, and a per-unit comparison divides that pack's price by
/// <c>UnitsPerPack × the master product's size</c>. So the confirmed pack sizes of a product's
/// listings were confirmed against the size this row holds <em>now</em>: changing 375 ml to 600 ml
/// under a confirmed 30-pack would silently re-price that pack for every business tracking the
/// product, without anybody confirming anything. The size is therefore fixed while any confirmed
/// listing exists, and the way to correct it is to decide those listings in the Supplier Price Review
/// first. A brand or a name edit changes the shared catalogue's own <em>label</em> and no price was
/// ever divided by either, so it is always allowed.</para>
///
/// <para><strong>It stores nothing and reads nothing</strong>, like every policy in this folder: the
/// count of confirmed listings and the stored instant are read inside the edit's own write transaction
/// and handed here.</para>
/// </summary>
public static class MasterProductEditPolicy
{
    /// <summary>
    /// What a reviewer who tried to change the size of a product with confirmed supplier listings is
    /// told. It names the reason and the way forward, because "refused" alone would read as a bug.
    /// </summary>
    public const string SizeChangeRefusedMessage =
        "This master product's size cannot be changed while a supplier listing of it is confirmed: "
            + "those listings' pack sizes were confirmed against the current size, and every tracking "
            + "business's per-unit price would change. Correct the brand or the name here, and decide "
            + "the confirmed listings in Supplier Price Review before changing the size.";

    /// <summary>
    /// What a reviewer whose edit presented an instant the row no longer holds is told: somebody
    /// else - another tab, another administrator - changed it since this page was read.
    ///
    /// <para>A refusal rather than an overwrite, for the reason the plan grant's revision check
    /// refuses a stale write (issue #982): the alternative is the second save silently discarding the
    /// first, which on a row shared by every business is a correction nobody can see was lost.</para>
    /// </summary>
    public const string StaleEditMessage =
        "This master product has been changed since this page was read. Re-read the catalogue and "
            + "make the correction again.";

    /// <summary>
    /// What a caller whose edit carried no expected instant at all is told. A <em>validation</em>
    /// failure and not a conflict: the request is incomplete rather than stale, and the two answers
    /// must stay different because one tells the page to re-read and the other to send the field.
    /// </summary>
    public const string ExpectedUpdatedAtRequiredMessage =
        "An edit of a master product must carry the lastChangedAtUtc it is correcting.";

    /// <summary>
    /// Whether <paramref name="submittedValue"/>/<paramref name="submittedUnit"/> is a different size
    /// from <paramref name="storedValue"/>/<paramref name="storedUnit"/>.
    ///
    /// <para>The value is compared as a <see cref="decimal"/> rather than by its spelling, so
    /// <c>375</c>, <c>375.0</c> and <c>375.000</c> are one size and re-submitting an unchanged size is
    /// not an edit of it. The unit is half the comparison: 1 l and 1 each are not one size, and a unit
    /// is the whole of what makes a size comparable at all.</para>
    /// </summary>
    public static bool IsSizeChange(
        decimal storedValue,
        MasterProductSizeUnit storedUnit,
        decimal submittedValue,
        MasterProductSizeUnit submittedUnit) =>
        storedValue != submittedValue || storedUnit != submittedUnit;

    /// <summary>
    /// Refuses a size change while the master product has any confirmed supplier listing.
    /// </summary>
    /// <param name="sizeChanged"><see cref="IsSizeChange"/>'s answer for this edit.</param>
    /// <param name="confirmedListingCount">
    /// How many of the product's supplier listings are <see cref="SupplierListingMatchStatus.Confirmed"/>,
    /// counted inside the edit's own write transaction so the answer cannot be stale.
    /// </param>
    /// <exception cref="DomainConflictException">
    /// The size would change and at least one listing is confirmed. Nothing is written.
    /// </exception>
    public static void RequireSizeMayChange(bool sizeChanged, int confirmedListingCount)
    {
        if (sizeChanged && confirmedListingCount > 0)
        {
            throw new DomainConflictException(SizeChangeRefusedMessage);
        }
    }

    /// <summary>
    /// Refuses an edit that presents an instant the row no longer holds.
    /// </summary>
    /// <param name="expectedUpdatedAtUtc">The <c>UpdatedAtUtc</c> the editing page read.</param>
    /// <param name="storedUpdatedAtUtc">
    /// The one the row carries, read inside the edit's own write transaction - which is what makes
    /// the comparison authoritative rather than a guess about what happened in between.
    /// </param>
    /// <exception cref="DomainConflictException">They differ. Nothing is written.</exception>
    public static void RequireCurrent(DateTime expectedUpdatedAtUtc, DateTime storedUpdatedAtUtc)
    {
        if (expectedUpdatedAtUtc != storedUpdatedAtUtc)
        {
            throw new DomainConflictException(StaleEditMessage);
        }
    }

    /// <summary>The expected instant an edit must carry, refused when it carries none.</summary>
    /// <exception cref="DomainValidationException">
    /// The edit carried no expected instant. Nothing is written.
    /// </exception>
    public static DateTime RequireExpectedUpdatedAt(DateTime? expectedUpdatedAtUtc) =>
        expectedUpdatedAtUtc
            ?? throw new DomainValidationException(ExpectedUpdatedAtRequiredMessage);
}
