using Inventory.Domain.Exceptions;

namespace Inventory.Domain.PriceCatalogue;

/// <summary>
/// What a stored barcode is the barcode <em>of</em> (issue #923, parent #817).
///
/// <para>The scope is stored beside the barcode rather than inferred from its length or its check
/// digit, because a retailer publishes both: the code on the can and the code on the carton the cans
/// come in. Scanning one and pricing the other is off by the pack size, so a barcode with no scope is
/// refused - see <see cref="SupplierListingPolicy.NormaliseBarcode"/>.</para>
///
/// <para>Numbered from 1, so a stored <c>0</c> is never read as the first declared scope.</para>
/// </summary>
public enum BarcodeScope
{
    /// <summary>The individual item - the code printed on the 375 ml can itself.</summary>
    Unit = 1,

    /// <summary>The outer pack - the code printed on the carton the cans are shipped in.</summary>
    Carton = 2,
}

/// <summary>
/// How confident the catalogue is that one supplier listing really is the master product it hangs
/// from (issue #923, parent #817).
///
/// <para>Four states, because the collector of <see cref="SupplierListingMatchStatus.Proposed"/>
/// listings is a search and not an oracle: a Super Admin confirms one (#928), and a listing whose
/// evidence disagrees with itself is flagged rather than quietly priced. Numbered from 1.</para>
/// </summary>
public enum SupplierListingMatchStatus
{
    /// <summary>The collector's own answer, not yet confirmed by a person.</summary>
    Proposed = 1,

    /// <summary>Confirmed - by a Super Admin (#928), or by an automatic match that was accepted (#941).</summary>
    Confirmed = 2,

    /// <summary>
    /// Flagged for a person to look at: the listing exists and something about it does not agree -
    /// a size that does not match the master product, a pack count that changed, a barcode that
    /// names something else.
    /// </summary>
    NeedsReview = 3,

    /// <summary>
    /// Refused by a person: this listing is not this product (#928).
    ///
    /// <para>It is kept rather than deleted, for the reason a rejected match suggestion is
    /// (<see cref="ProductMatchSuggestionStatus.Rejected"/>): a search finds the same shelf again
    /// every run, and a listing that was deleted would be rediscovered and proposed a second time.
    /// A refusal has to outlive the row it refused. A rediscovery therefore leaves it rejected -
    /// <see cref="PriceCollectorResultPolicy.DecideForKnownListing"/> is that rule - unless the
    /// listing's own identity changed, which makes it a new proposal rather than the thing somebody
    /// refused. Issue #937 is the first writer of this value, and only in that one direction: moving
    /// a listing <em>into</em> it is #928's.</para>
    /// </summary>
    Rejected = 4,
}

/// <summary>
/// The rules of one supplier listing (issue #923, parent #817): one product as one supplier publishes
/// it, with the supplier's own id for it, how many come in a pack, and the barcode it carries.
///
/// <para><strong>A listing belongs to a supplier master record and to exactly one of two owners.</strong>
/// It hangs either from a <see cref="MasterProductPolicy">master product</see> - the normal state - or,
/// while nothing has matched it yet, from the waiting key the search that found it was run for. Both
/// at once is a listing claiming to be two things, and neither is a listing nothing can ever find
/// again; <see cref="HasExactlyOneOwner"/> is that rule, and the database holds it too, because a row
/// in either condition is not something an application-level check can be trusted to have
/// prevented.</para>
///
/// <para><strong>Nothing here is a price.</strong> This issue stores what a supplier <em>lists</em>;
/// what it costs on a given day is the price-observation work of #925, which this policy knows nothing
/// about. No amount, GST decision or comparison passes through it.</para>
/// </summary>
public static class SupplierListingPolicy
{
    /// <summary>
    /// The longest accepted supplier product id or SKU, measured on the trimmed value. Generous,
    /// because it is whatever the supplier issues - a number, a code, a slug - and a retailer that
    /// issues a longer one must not need a migration.
    /// </summary>
    public const int MaxSupplierProductIdLength = 200;

    /// <summary>The longest accepted listing title - the supplier's own wording for the product.</summary>
    public const int MaxTitleLength = 400;

    /// <summary>
    /// The longest accepted variant - the supplier's own wording for <em>which</em> one this is
    /// ("Classic", "Zero Sugar"), read off the listing rather than inferred from the title (issue
    /// #937). Bounded like a master product's name, because that is what it describes.
    /// </summary>
    public const int MaxVariantLength = MasterProductPolicy.MaxNameLength;

    /// <summary>
    /// The longest accepted listing URL. The same bound a supplier's website carries, for the same
    /// reason: it is an address, not a document.
    /// </summary>
    public const int MaxUrlLength = 2048;

    /// <summary>
    /// The longest accepted barcode. A GTIN-14 is fourteen characters; the rest is room for a
    /// retailer's own longer internal code, because this column stores whatever the supplier
    /// published.
    /// </summary>
    public const int MaxBarcodeLength = 32;

    /// <summary>What a caller who submitted no supplier product id, or only whitespace, is told.</summary>
    public const string SupplierProductIdRequiredMessage =
        "A supplier listing needs the supplier's own product id or SKU.";

    /// <summary>What a caller who submitted a supplier product id that is too long is told.</summary>
    public const string SupplierProductIdTooLongMessage =
        "A supplier product id must be 200 characters or fewer.";

    /// <summary>
    /// What a caller who submitted a units-per-pack value that is not above zero is told. "Unknown"
    /// is a separate, accepted answer - see <see cref="NormaliseUnitsPerPack"/> - so zero is not a way
    /// of expressing it.
    /// </summary>
    public const string UnitsPerPackNotPositiveMessage =
        "Units per pack must be a whole number greater than zero, or unknown.";

    /// <summary>What a caller who submitted a barcode longer than <see cref="MaxBarcodeLength"/> is told.</summary>
    public const string BarcodeTooLongMessage = "A barcode must be 32 characters or fewer.";

    /// <summary>
    /// What a caller who submitted a barcode without saying what it is the barcode of is told. It is
    /// refused rather than defaulted to <see cref="BarcodeScope.Unit"/>, because a carton code read as
    /// a unit code prices a single can at the price of thirty.
    /// </summary>
    public const string BarcodeScopeRequiredMessage =
        "A barcode must say whether it is the unit's or the carton's.";

    /// <summary>
    /// What a caller who submitted a barcode scope with no barcode is told: a scope on its own
    /// describes nothing.
    /// </summary>
    public const string BarcodeRequiredForScopeMessage =
        "A barcode scope needs a barcode.";

    /// <summary>
    /// What a caller who chose a barcode scope that is not a declared one is told. Built from the
    /// vocabulary itself so it cannot drift from what is accepted.
    /// </summary>
    public static string UnknownBarcodeScopeMessage { get; } =
        "A barcode scope must be one of "
            + string.Join(", ", Enum.GetNames<BarcodeScope>())
            + ".";

    /// <summary>
    /// What a caller who gave a listing both a master product and a waiting key, or neither, is told.
    /// </summary>
    public const string OneOwnerRequiredMessage =
        "A supplier listing belongs to a master product or to a waiting key, not to both and not to "
            + "neither.";

    /// <summary>What a caller who submitted a variant that is too long is told.</summary>
    public const string VariantTooLongMessage = "A listing variant must be 200 characters or fewer.";

    /// <summary>
    /// What a caller who submitted an individual size without its unit, or a unit without a size, is
    /// told. The two travel together for the reason a barcode and its scope do: "375" of something
    /// nobody declared cannot be compared with anything, and a unit on its own describes nothing.
    /// </summary>
    public const string SizeAndUnitTogetherMessage =
        "A listing's individual size and its unit are recorded together, or neither is.";

    /// <summary>Whether <paramref name="scope"/> is one of the declared scopes.</summary>
    public static bool IsDeclaredBarcodeScope(BarcodeScope? scope) =>
        scope is null || Enum.IsDefined(scope.Value);

    /// <summary>
    /// Whether a listing has exactly one owner: a master product, or an active waiting key, never both
    /// and never neither.
    ///
    /// <para>The <em>origin</em> waiting key is deliberately not part of this rule. It records which
    /// search discovered the listing and is kept after the listing moves to a master product (#941),
    /// so a listing can carry a master product and an origin key at once without being a listing that
    /// claims to be two things. Folding the two columns into one would have forced a choice between
    /// losing that provenance and breaking this constraint.</para>
    /// </summary>
    public static bool HasExactlyOneOwner(long? masterProductId, string? waitingKey)
    {
        var hasMasterProduct = masterProductId is not null;
        var hasWaitingKey = !string.IsNullOrWhiteSpace(waitingKey);

        return hasMasterProduct != hasWaitingKey;
    }

    /// <summary>
    /// Refuses a listing that has both a master product and an active waiting key, or neither.
    /// </summary>
    /// <exception cref="DomainValidationException">
    /// The listing does not have exactly one owner. Nothing is stored.
    /// </exception>
    public static void RequireExactlyOneOwner(long? masterProductId, string? waitingKey)
    {
        if (!HasExactlyOneOwner(masterProductId, waitingKey))
        {
            throw new DomainValidationException(OneOwnerRequiredMessage);
        }
    }

    /// <summary>The supplier's product id as it is to be stored: trimmed, never empty.</summary>
    /// <exception cref="DomainValidationException">
    /// The id is blank, or longer than <see cref="MaxSupplierProductIdLength"/> once trimmed.
    /// </exception>
    public static string NormaliseSupplierProductId(string? supplierProductId)
    {
        if (string.IsNullOrWhiteSpace(supplierProductId))
        {
            throw new DomainValidationException(SupplierProductIdRequiredMessage);
        }

        var trimmed = supplierProductId.Trim();

        return trimmed.Length <= MaxSupplierProductIdLength
            ? trimmed
            : throw new DomainValidationException(SupplierProductIdTooLongMessage);
    }

    /// <summary>What a caller who submitted a title that is too long is told.</summary>
    public const string TitleTooLongMessage = "A listing title must be 400 characters or fewer.";

    /// <summary>
    /// The supplier's own wording as it is to be stored (issue #937): trimmed, with blank stored as
    /// <see langword="null"/>. It is evidence a reviewer reads and never a value anything derives a
    /// size, a pack count or a brand from, so nothing else is done to it.
    /// </summary>
    /// <exception cref="DomainValidationException">
    /// The title is longer than <see cref="MaxTitleLength"/> once trimmed.
    /// </exception>
    public static string? NormaliseTitle(string? title)
    {
        var trimmed = (title ?? string.Empty).Trim();

        if (trimmed.Length == 0)
        {
            return null;
        }

        return trimmed.Length <= MaxTitleLength
            ? trimmed
            : throw new DomainValidationException(TitleTooLongMessage);
    }

    /// <summary>
    /// The variant as it is to be stored (issue #937): trimmed, with blank stored as
    /// <see langword="null"/> so "the supplier did not say which one" has one spelling.
    /// </summary>
    /// <exception cref="DomainValidationException">
    /// The variant is longer than <see cref="MaxVariantLength"/> once trimmed.
    /// </exception>
    public static string? NormaliseVariant(string? variant)
    {
        var trimmed = (variant ?? string.Empty).Trim();

        if (trimmed.Length == 0)
        {
            return null;
        }

        return trimmed.Length <= MaxVariantLength
            ? trimmed
            : throw new DomainValidationException(VariantTooLongMessage);
    }

    /// <summary>
    /// The individual size and its unit as they are to be stored (issue #937): a positive size at
    /// the catalogue's own scale with its declared unit, or two <see langword="null"/>s when the
    /// supplier published neither.
    ///
    /// <para>The size is held to <see cref="MasterProductPolicy.NormaliseSize"/>'s rules, which are
    /// the only size rules in this catalogue: one listing's size is compared against a master
    /// product's, so a second scale beside it would be a second answer to "is this the same size?".
    /// </para>
    /// </summary>
    /// <exception cref="DomainValidationException">
    /// Only one of the two was given, the unit is not a declared one, or the size is not a size this
    /// catalogue stores. Nothing is stored.
    /// </exception>
    public static (decimal? SizeValue, MasterProductSizeUnit? SizeUnit) NormaliseIndividualSize(
        decimal? sizeValue,
        MasterProductSizeUnit? sizeUnit) => (sizeValue, sizeUnit) switch
        {
            (null, null) => (null, null),
            ({ } size, { } unit) => (MasterProductPolicy.NormaliseSize(size, unit), unit),
            _ => throw new DomainValidationException(SizeAndUnitTogetherMessage),
        };

    /// <summary>
    /// The units per pack as they are to be stored: a positive whole number, or
    /// <see langword="null"/> when the supplier does not say.
    ///
    /// <para><strong>Unknown stays unknown</strong> (issue #923). It is not defaulted to 1, which
    /// would state that a carton is a single and make a carton's price read as a single's; it is not
    /// defaulted to 0, which would divide; and it is not guessed from a title. A listing whose pack
    /// size nobody published is a listing whose per-unit price cannot be calculated, and that is a
    /// real answer - the same restraint the report availability model shows about a missing provider
    /// figure.</para>
    /// </summary>
    /// <exception cref="DomainValidationException">
    /// A value was given and it is not above zero. Nothing is stored.
    /// </exception>
    public static int? NormaliseUnitsPerPack(int? unitsPerPack) => unitsPerPack switch
    {
        null => null,
        > 0 => unitsPerPack,
        _ => throw new DomainValidationException(UnitsPerPackNotPositiveMessage),
    };

    /// <summary>
    /// The barcode and its scope as they are to be stored: the trimmed barcode with its scope, or two
    /// <see langword="null"/>s when the supplier published neither.
    ///
    /// <para><strong>A barcode is text and is never parsed</strong> (issue #923). Leading zeros are
    /// part of the code - <c>0012345678905</c> and <c>12345678905</c> are different barcodes - so this
    /// method trims the ends and changes nothing else: no numeric conversion, no re-formatting, no
    /// check-digit arithmetic and no zero-padding. Storing it as a number is the one mistake that
    /// cannot be undone later, because the lost digits are not recoverable.</para>
    ///
    /// <para><strong>A barcode and its scope travel together.</strong> Either both are present or
    /// neither is: a code with no scope cannot be compared safely, and a scope with no code describes
    /// nothing.</para>
    /// </summary>
    /// <exception cref="DomainValidationException">
    /// Only one of the two was given, the scope is not a declared one, or the barcode is longer than
    /// <see cref="MaxBarcodeLength"/> once trimmed. Nothing is stored.
    /// </exception>
    public static (string? Barcode, BarcodeScope? Scope) NormaliseBarcode(
        string? barcode,
        BarcodeScope? scope)
    {
        if (!IsDeclaredBarcodeScope(scope))
        {
            throw new DomainValidationException(UnknownBarcodeScopeMessage);
        }

        // Trim only, and only the ends: every other character is the supplier's own code, leading
        // zeros included. There is no conversion to a number anywhere on this path.
        var trimmed = (barcode ?? string.Empty).Trim();

        if (trimmed.Length == 0)
        {
            return scope is null
                ? (null, null)
                : throw new DomainValidationException(BarcodeRequiredForScopeMessage);
        }

        if (trimmed.Length > MaxBarcodeLength)
        {
            throw new DomainValidationException(BarcodeTooLongMessage);
        }

        return scope is null
            ? throw new DomainValidationException(BarcodeScopeRequiredMessage)
            : (trimmed, scope);
    }
}
