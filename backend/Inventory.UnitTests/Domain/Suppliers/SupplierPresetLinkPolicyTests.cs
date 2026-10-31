using Inventory.Domain.Suppliers;
using Xunit;

namespace InventoryApi.Tests.Domain.Suppliers;

/// <summary>
/// The one rule that decides what a business may do to a supplier it received from the platform's
/// supplier master (issue #934, second task of #817).
///
/// <para>Every assertion here is written against the link alone, because that is the whole of the
/// rule: a linked copy's identity - its name, its website and the link itself - is the master's, and
/// everything else about the row is the business's own.</para>
/// </summary>
public class SupplierPresetLinkPolicyTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData(7, true)]
    public void A_supplier_is_linked_exactly_when_it_carries_a_preset_id(int? presetId, bool expected) =>
        Assert.Equal(expected, SupplierPresetLinkPolicy.IsLinked(presetId));

    [Fact]
    public void An_unlinked_supplier_may_be_renamed_and_deleted_and_may_not_be_hidden()
    {
        Assert.True(SupplierPresetLinkPolicy.CanRename(isLinked: false));
        Assert.True(SupplierPresetLinkPolicy.CanDelete(isLinked: false));
        Assert.False(SupplierPresetLinkPolicy.CanHide(isLinked: false));
    }

    [Fact]
    public void A_linked_supplier_may_be_hidden_and_may_not_be_renamed_or_deleted()
    {
        Assert.False(SupplierPresetLinkPolicy.CanRename(isLinked: true));
        Assert.False(SupplierPresetLinkPolicy.CanDelete(isLinked: true));
        Assert.True(SupplierPresetLinkPolicy.CanHide(isLinked: true));
    }

    /// <summary>
    /// An unlinked supplier is refused nothing at all: it behaves exactly as it did before this
    /// issue, which is the half of the rule a later edit is most likely to break.
    /// </summary>
    [Fact]
    public void An_unlinked_supplier_update_is_never_refused()
    {
        Assert.Null(SupplierPresetLinkPolicy.RefuseUpdate(
            isLinked: false,
            storedName: "Coles",
            storedUrl: "https://coles.test",
            submittedName: "Something else entirely",
            submittedUrl: null));
        Assert.Null(SupplierPresetLinkPolicy.RefuseDelete(isLinked: false));
    }

    /// <summary>
    /// The contact details and - through the GST defaults resource, which never submits a name - the
    /// supplier's accounting settings are the business's own, so an update that leaves the name and
    /// the website exactly as they are stored goes through on a linked copy.
    /// </summary>
    [Theory]
    [InlineData("Coles", null)]
    [InlineData("Coles", "https://coles.test")]
    public void A_linked_supplier_update_that_changes_neither_the_name_nor_the_website_is_allowed(
        string name, string? url) =>
        Assert.Null(SupplierPresetLinkPolicy.RefuseUpdate(
            isLinked: true, storedName: name, storedUrl: url, submittedName: name, submittedUrl: url));

    [Fact]
    public void A_linked_supplier_may_not_be_renamed()
    {
        Assert.Equal(
            SupplierPresetLinkPolicy.RenameRefusedMessage,
            SupplierPresetLinkPolicy.RefuseUpdate(
                isLinked: true,
                storedName: "Coles",
                storedUrl: null,
                submittedName: "Coles Group",
                submittedUrl: null));

        // Case and surrounding whitespace are changes too: the stored name is the master's, to the
        // character, and "coles " is not the value this row carries.
        Assert.Equal(
            SupplierPresetLinkPolicy.RenameRefusedMessage,
            SupplierPresetLinkPolicy.RefuseUpdate(true, "Coles", null, "coles", null));
        Assert.Equal(
            SupplierPresetLinkPolicy.RenameRefusedMessage,
            SupplierPresetLinkPolicy.RefuseUpdate(true, "Coles", null, "Coles ", null));
    }

    /// <summary>
    /// The website is refused in both directions - set, changed and cleared - because all three are
    /// the stored value drifting from the master's.
    /// </summary>
    [Theory]
    [InlineData(null, "https://coles.test")]
    [InlineData("https://coles.test", null)]
    [InlineData("https://coles.test", "https://coles.example")]
    public void A_linked_suppliers_website_may_not_be_set_changed_or_cleared(string? stored, string? submitted) =>
        Assert.Equal(
            SupplierPresetLinkPolicy.WebsiteRefusedMessage,
            SupplierPresetLinkPolicy.RefuseUpdate(true, "Coles", stored, "Coles", submitted));

    /// <summary>
    /// A rename and a website change arriving together are reported as the rename: the name is the
    /// field an operator typed in first, and one refusal per request is what the HTTP boundary can
    /// put on a form.
    /// </summary>
    [Fact]
    public void A_rename_and_a_website_change_together_are_reported_as_the_rename() =>
        Assert.Equal(
            SupplierPresetLinkPolicy.RenameRefusedMessage,
            SupplierPresetLinkPolicy.RefuseUpdate(true, "Coles", null, "Coles Group", "https://coles.test"));

    [Fact]
    public void A_linked_supplier_may_not_be_deleted() =>
        Assert.Equal(
            SupplierPresetLinkPolicy.DeleteRefusedMessage,
            SupplierPresetLinkPolicy.RefuseDelete(isLinked: true));

    /// <summary>
    /// "Not used" is a linked copy's substitute for the delete it cannot have, so it is refused on a
    /// supplier the business added itself - which can simply be deleted.
    /// </summary>
    [Fact]
    public void Only_a_linked_supplier_may_be_hidden()
    {
        Assert.Null(SupplierPresetLinkPolicy.RefuseHide(isLinked: true));
        Assert.Equal(
            SupplierPresetLinkPolicy.HideRefusedMessage,
            SupplierPresetLinkPolicy.RefuseHide(isLinked: false));
    }

    /// <summary>
    /// The four refusals are distinct sentences. An operator reads one of them on a form, and two
    /// different refusals sharing a message would make "why can I not save this?" unanswerable.
    /// </summary>
    [Fact]
    public void The_four_refusals_are_four_different_messages() =>
        Assert.Equal(
            4,
            new HashSet<string>(StringComparer.Ordinal)
            {
                SupplierPresetLinkPolicy.RenameRefusedMessage,
                SupplierPresetLinkPolicy.WebsiteRefusedMessage,
                SupplierPresetLinkPolicy.DeleteRefusedMessage,
                SupplierPresetLinkPolicy.HideRefusedMessage,
            }.Count);
}
