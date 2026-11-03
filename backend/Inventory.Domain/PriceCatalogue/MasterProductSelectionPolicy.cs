namespace Inventory.Domain.PriceCatalogue;

/// <summary>
/// The one rule that decides whether a product being created may be linked to the master product the
/// operator picked (issue #1051, parent #817): whether the operator still wants supplier prices
/// tracked at all, and whether the product they are creating is still the one the shared catalogue
/// described to them.
///
/// <para><strong>The link is a statement about identity, so the evidence has to be the identity
/// itself.</strong> The only thing this rule accepts is the submitted Name and unit label still being
/// the two values <see cref="MasterProductPrefill"/> computes from the selected master product.
/// Nothing fuzzy, nothing matched on the name alone, and <em>no client boolean</em>: a request saying
/// "this is master product 7" is believed only because the text it submitted is still master product
/// 7's own text. A person who edits either value has made a different product, and a different
/// product is not this one.</para>
///
/// <para><strong>The normalisation is the three differences that are not edits</strong>: whitespace
/// at the ends, a run of whitespace inside a value, and case. Those are exactly what
/// <see cref="MasterProductPrefill.ProductName"/> already folds away when it builds a Name, and what
/// a form, a copy-paste or a keyboard produces without anybody meaning anything by it. Everything
/// else is a change - including spacing the prefill never produces, such as <c>"45 g"</c> for
/// <c>"45g"</c> - because a size written differently is how a product gets matched to the wrong
/// shelf.</para>
///
/// <para><strong>It reads nothing and writes nothing.</strong> It is given a prefill and two
/// submitted strings and answers yes or no; which master products exist, which pairs a Super Admin
/// refused and what is stored for a business are the adapter's reads, decided inside the create's own
/// transaction.</para>
/// </summary>
public static class MasterProductSelectionPolicy
{
    /// <summary>
    /// The code a client branches on when the selected master product is no longer in the shared
    /// catalogue. Stable text, like <c>plan_required</c>: the sentence beside it may be reworded and
    /// this may not.
    /// </summary>
    public const string UnavailableCode = "master_product_unavailable";

    /// <summary>
    /// The code for a selection whose Name or unit label no longer agrees with the master product -
    /// because the catalogue was corrected after the lookup, or because the request was tampered
    /// with. The two are deliberately one answer: the server cannot tell them apart, and the operator
    /// does the same thing about either.
    /// </summary>
    public const string ChangedCode = "master_product_changed";

    /// <summary>
    /// The code for a <c>(waiting key, master product)</c> pair a Super Admin has refused or undone.
    /// A refusal outlives the refusing, which is the whole reason the suggestions are kept for ever
    /// (<see cref="ProductMatchSuggestionPolicy.BlocksAutomaticMatching"/>), and a business may not
    /// walk around it by picking the product by hand.
    /// </summary>
    public const string RefusedCode = "master_product_refused";

    /// <summary>What the operator is told about a master product that is no longer there.</summary>
    public const string UnavailableMessage =
        "The product you chose is no longer in the shared catalogue. Choose another one, or create "
            + "this product without the link.";

    /// <summary>
    /// What the operator is told when the two values no longer agree. It says what to do about it,
    /// because there are exactly two ways on: pick the product again, which refills both values, or
    /// create the product as typed with no link at all.
    /// </summary>
    public const string ChangedMessage =
        "The Name and unit label no longer match the product you chose from the shared catalogue. "
            + "Choose it again to fill them in afresh, or create this product without the link.";

    /// <summary>What the operator is told about a pair that has been refused.</summary>
    public const string RefusedMessage =
        "The product you chose has been ruled out for this Name and unit label. Choose another one, "
            + "or create this product without the link.";

    /// <summary>
    /// The master product the create is to link to, or <see langword="null"/> when there is none to
    /// link.
    ///
    /// <para><strong>Turning supplier price tracking off is the operator's opt-out, and it ignores
    /// the selection rather than arguing with it</strong> (issue #1051's third criterion). The link
    /// <em>is</em> the tracking row - there is no other place a product records which master product
    /// it is - so "do not track this product's supplier prices" and "do not link it" are one answer,
    /// and a product created that way is exactly the unlinked, untracked product every create made
    /// before this issue. Nothing is read, nothing is refused and no master product is looked up at
    /// all: a selection nobody is going to use is not a selection that has to still be valid.</para>
    /// </summary>
    public static long? SelectedMasterProductId(
        long? selectedMasterProductId,
        bool trackSupplierPrices) =>
        trackSupplierPrices ? selectedMasterProductId : null;

    /// <summary>
    /// Whether the submitted Name and unit label are still <paramref name="prefill"/>'s own, under
    /// the normalisation above. Both, always: the two values travel together because a Name under
    /// another size's label is a different product, and a label under another product's Name is not
    /// evidence of anything.
    /// </summary>
    /// <param name="prefill">
    /// The prefill recomputed from the <em>stored</em> master product, inside the create's own
    /// transaction - never the one the lookup answered with, which is as old as the form.
    /// </param>
    /// <param name="submittedName">The product Name the request is creating the product with.</param>
    /// <param name="submittedUnitLabel">The unit label it is creating the product with.</param>
    public static bool IsUnchangedPrefill(
        MasterProductPrefillText prefill,
        string? submittedName,
        string? submittedUnitLabel)
    {
        ArgumentNullException.ThrowIfNull(prefill);

        return Matches(prefill.ProductName, submittedName)
            && Matches(prefill.UnitLabel, submittedUnitLabel);
    }

    /// <summary>
    /// One value compared: whitespace trimmed and collapsed on both sides, then compared
    /// ordinal-ignore-case.
    ///
    /// <para>Ordinal rather than culture-aware for the reason the waiting key's fold is invariant: a
    /// label must mean the same thing on every machine that reads one, and a culture-sensitive
    /// comparison would link a product on one server and refuse it on another.</para>
    /// </summary>
    private static bool Matches(string expected, string? submitted) =>
        string.Equals(
            Collapse(expected),
            Collapse(submitted),
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The value with every run of whitespace reduced to one space and none at either end -
    /// <see cref="MasterProductPrefill.ProductName"/>'s own fold, so a Name that formatter produced
    /// compares equal to itself by construction.
    /// </summary>
    private static string Collapse(string? value) =>
        string.Join(
            ' ',
            (value ?? string.Empty).Split(
                (char[]?)null,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
}
