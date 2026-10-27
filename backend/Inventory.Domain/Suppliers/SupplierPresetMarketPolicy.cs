namespace Inventory.Domain.Suppliers;

/// <summary>
/// Whether one platform supplier preset is a supplier record a given business may receive (issue
/// #935, third task of <see href="https://github.com/cristhyanc/InventoryApp/issues/817">#817</see>):
/// the state-matching rule, and nothing else.
///
/// <para>It is the counterpart of <see cref="SupplierPresetMasterPolicy"/>, and the split is the
/// same one that policy makes about itself. That one owns what a preset <em>claims</em> - a market,
/// a tax basis, a collection route - and every rule in it is about those three being consistent with
/// each other. This one owns one comparison between a claim and a <em>business</em>: the market the
/// preset recorded, against the Australian state or territory the business recorded (issue #933).
/// Neither reads or writes a supplier row, and neither knows which business is asking.</para>
///
/// <para><strong>Two facts decide it, and both have to hold.</strong> A record is offered only while
/// it is still suggested <em>and</em> serves the business: a record the Super Admin disabled is
/// offered to nobody however well its market matches, and a record that serves only Western
/// Australia is offered to nobody in Canberra however suggested it is. They are separate questions
/// because they fail for different reasons and are fixed in different places.</para>
///
/// <para><strong>A business with no recorded state receives nationwide records only.</strong> Every
/// business that existed before issue #933's column did has none, nothing backfills them, and a
/// state is never inferred - not from a time zone, which cannot tell New South Wales from the
/// Australian Capital Territory, and not from an address or an ABN (see
/// <c>Inventory.Domain.Tenancy.AustralianStates</c>). "Serves everywhere" is the one claim that
/// covers a business whose state nobody has recorded, which is also why a nationwide preset stores
/// no state list at all rather than all eight codes.</para>
///
/// <para><strong>A record that claims no market serves nobody.</strong> That is the fail-closed
/// default a row the supplier-master migration did not recognise carries, and reading it as "serves
/// everywhere" would hand every business a record whose market nobody has stated. It reads as
/// <em>Not set</em> on the Super Admin's page and is corrected by editing it there, where
/// <see cref="SupplierPresetMasterPolicy"/>'s rules apply in full.</para>
///
/// <para><strong>Nothing here removes anything.</strong> The rule decides only what a business may
/// <em>receive</em>: a copy it already holds is never withdrawn, hidden or relinked because the
/// business changed state or the platform disabled the record, because purchases may already
/// reference it (the owner's decision of 27 October 2026 on #817).</para>
/// </summary>
public static class SupplierPresetMarketPolicy
{
    /// <summary>
    /// What a caller who asked for a copy of a record this business already holds one of is told. It
    /// is the sentence behind the <c>409</c> a double-click, a second tab, or a state change racing
    /// an add is answered with - one copy per business per record, so the second request is told the
    /// outcome it wanted is already the case rather than being given a server error. It names the
    /// supplier list and no business, because a refusal is read by somebody inside one business.
    /// </summary>
    public const string AlreadyAddedMessage =
        "This supplier from the platform's supplier list has already been added.";

    /// <summary>
    /// Whether a record whose market is <paramref name="servesNationwide"/> /
    /// <paramref name="storedServedStates"/> serves a business trading in
    /// <paramref name="businessState"/>.
    /// </summary>
    /// <param name="servesNationwide">Whether the record claims the whole country.</param>
    /// <param name="storedServedStates">
    /// The record's stored served-state value, exactly as
    /// <see cref="SupplierPresetMasterPolicy.NormaliseServedStates"/> wrote it. It is read back
    /// through <see cref="SupplierPresetMasterPolicy.ReadServedStates"/> - the one reader there is -
    /// so an unrecognised code that only a direct database edit could produce is dropped and matches
    /// nothing, rather than being compared as a state this application cannot describe.
    /// </param>
    /// <param name="businessState">
    /// The business's recorded state or territory (<c>Business.State</c>), or <see langword="null"/>
    /// when none was ever recorded. It is compared ordinally against the canonical stored codes,
    /// because both sides are already the one canonical spelling their own write paths produced.
    /// </param>
    public static bool ServesState(
        bool servesNationwide,
        string? storedServedStates,
        string? businessState)
    {
        if (servesNationwide)
        {
            return true;
        }

        return businessState is not null
            && SupplierPresetMasterPolicy
                .ReadServedStates(storedServedStates)
                .Contains(businessState, StringComparer.Ordinal);
    }

    /// <summary>
    /// Whether the record is one this business may receive: still suggested, and serving it. This is
    /// the single rule the approval's copy, the state change's top-up and the "Available to add"
    /// list all read, so the three cannot disagree about which records a business is entitled to.
    /// </summary>
    public static bool IsOfferedTo(
        bool isEnabled,
        bool servesNationwide,
        string? storedServedStates,
        string? businessState) =>
        isEnabled && ServesState(servesNationwide, storedServedStates, businessState);
}
