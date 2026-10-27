namespace Inventory.Domain.Suppliers;

/// <summary>
/// The one rule for a business supplier that came from the platform's supplier master (issue #934,
/// second task of <see href="https://github.com/cristhyanc/InventoryApp/issues/817">#817</see>).
///
/// <para>Since issue #849 an approved business receives its own <c>Supplier</c> row per enabled
/// preset. Until this rule existed those rows were indistinguishable from the ones the business
/// typed in itself, so a copy could be renamed, re-pointed at another website or deleted - and a
/// price comparison that reads the master could no longer tell which row it was about. The link is
/// what fixes that, and this policy is the whole of what the link means:</para>
///
/// <list type="bullet">
/// <item>the <strong>name</strong>, the <strong>website</strong> and the link itself are the
/// master's, so a linked copy refuses a change to any of them;</item>
/// <item>a linked copy <strong>cannot be deleted</strong>, because deleting it would silently undo
/// the link and the business would get the preset again on no later event;</item>
/// <item>a linked copy the business does not buy from is marked <strong>"Not used"</strong> instead,
/// which is reversible and is the substitute for the delete it cannot have;</item>
/// <item>everything else about the row - the contact details and the supplier GST defaults of issue
/// #430 - is the business's own accounting settings and is untouched by this rule.</item>
/// </list>
///
/// <para><strong>An unlinked supplier is refused nothing.</strong> A supplier the business created
/// itself behaves exactly as it did before this issue: it is renamed, re-pointed and deleted
/// freely, and it is never hidden, because deleting it is the thing "Not used" stands in for.</para>
///
/// <para>This is the single source of both halves of the rule: the refusals the write paths return
/// and the <c>canRename</c>/<c>canDelete</c> eligibility the supplier responses publish for a client
/// to render. A second copy of either in a controller, an adapter or an Angular component would be
/// free to disagree with this one.</para>
/// </summary>
public static class SupplierPresetLinkPolicy
{
    /// <summary>What a caller who tried to rename a linked copy is told.</summary>
    public const string RenameRefusedMessage =
        "This supplier came from the platform's supplier list, so its name cannot be changed.";

    /// <summary>What a caller who tried to set, change or clear a linked copy's website is told.</summary>
    public const string WebsiteRefusedMessage =
        "This supplier came from the platform's supplier list, so its website cannot be changed.";

    /// <summary>
    /// What a caller who tried to delete a linked copy is told. It names the alternative, because
    /// the operator's actual intention - "we do not buy from this one" - has an answer.
    /// </summary>
    public const string DeleteRefusedMessage =
        "This supplier came from the platform's supplier list, so it cannot be deleted. "
            + "Mark it \"Not used\" to hide it instead.";

    /// <summary>
    /// What a caller who tried to mark a supplier the business created itself as "Not used" is
    /// told. It names the alternative for the same reason the delete refusal does.
    /// </summary>
    public const string HideRefusedMessage =
        "Only a supplier that came from the platform's supplier list can be marked \"Not used\". "
            + "Delete a supplier you added yourself instead.";

    /// <summary>
    /// Whether a supplier is a copy of a preset, which is the single fact every rule below reads.
    /// A supplier that carries no preset id is the business's own row and nothing here constrains
    /// it.
    /// </summary>
    public static bool IsLinked(int? supplierPresetId) => supplierPresetId is not null;

    /// <summary>Whether this supplier's name may be changed.</summary>
    public static bool CanRename(bool isLinked) => !isLinked;

    /// <summary>Whether this supplier's website may be set, changed or cleared.</summary>
    public static bool CanChangeWebsite(bool isLinked) => !isLinked;

    /// <summary>Whether this supplier may be deleted.</summary>
    public static bool CanDelete(bool isLinked) => !isLinked;

    /// <summary>
    /// Whether this supplier may be marked "Not used". Deliberately the opposite of
    /// <see cref="CanDelete"/> rather than an unconditional yes: hiding is what a linked copy has
    /// instead of a delete, and a supplier the business can simply delete has no use for a second,
    /// quieter way to take it out of the pickers.
    /// </summary>
    public static bool CanHide(bool isLinked) => isLinked;

    /// <summary>
    /// The refusal for an update, or <see langword="null"/> when the submitted values may be
    /// stored.
    /// </summary>
    /// <param name="isLinked">Whether the stored row carries a preset id.</param>
    /// <param name="storedName">The name the row currently holds.</param>
    /// <param name="storedUrl">The website the row currently holds, or <see langword="null"/>.</param>
    /// <param name="submittedName">The submitted name.</param>
    /// <param name="submittedUrl">
    /// The submitted website, already normalised by <c>SupplierUrlPolicy</c> - so a blank value
    /// arrives here as <see langword="null"/> and is compared as "no website" rather than as a
    /// change to an empty string.
    /// </param>
    /// <remarks>
    /// Only a <em>change</em> is refused. The supplier form submits the whole record, so an
    /// operator editing a phone number sends the stored name and website back unchanged, and
    /// refusing that would make a linked copy's contact details uneditable - which the owner's
    /// 27 October 2026 decision explicitly keeps editable. The comparison is ordinal: the stored
    /// value is the master's to the character, so a different capitalisation or a trailing space is
    /// a different name and is refused rather than quietly stored.
    /// </remarks>
    public static string? RefuseUpdate(
        bool isLinked,
        string storedName,
        string? storedUrl,
        string submittedName,
        string? submittedUrl)
    {
        if (!CanRename(isLinked) && !string.Equals(storedName, submittedName, StringComparison.Ordinal))
        {
            return RenameRefusedMessage;
        }

        return !CanChangeWebsite(isLinked) && !string.Equals(storedUrl, submittedUrl, StringComparison.Ordinal)
            ? WebsiteRefusedMessage
            : null;
    }

    /// <summary>
    /// The refusal for a delete, or <see langword="null"/> when the supplier may be deleted.
    /// </summary>
    public static string? RefuseDelete(bool isLinked) => CanDelete(isLinked) ? null : DeleteRefusedMessage;

    /// <summary>
    /// The refusal for setting or clearing "Not used", or <see langword="null"/> when the supplier
    /// may be hidden and shown again.
    /// </summary>
    public static string? RefuseHide(bool isLinked) => CanHide(isLinked) ? null : HideRefusedMessage;
}
