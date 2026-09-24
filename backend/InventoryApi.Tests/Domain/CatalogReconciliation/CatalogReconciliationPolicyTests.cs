using Inventory.Domain.CatalogReconciliation;
using Xunit;

namespace InventoryApi.Tests.Domain.CatalogReconciliation;

public class CatalogReconciliationPolicyTests
{
    [Fact]
    public void Identity_present_on_both_sides_with_the_same_name_is_Present()
    {
        var local = new[] { new LocalCatalogEntry(1, "Coke Zero") };
        var remote = new[] { new RemoteCatalogEntry(1, "Coke Zero") };

        var result = Assert.Single(CatalogReconciliationPolicy.Reconcile(local, remote));

        Assert.Equal(1, result.ExternalId);
        Assert.Equal(SourceReconciliationState.Present, result.State);
        Assert.Equal("Coke Zero", result.LocalName);
        Assert.Equal("Coke Zero", result.RemoteName);
        Assert.Null(result.Note);
    }

    [Fact]
    public void Name_differing_only_by_a_parenthetical_Nayax_suffix_still_counts_as_Present()
    {
        var local = new[] { new LocalCatalogEntry(1, "Coke Zero") };
        var remote = new[] { new RemoteCatalogEntry(1, "Coke Zero (1.50)") };

        var result = Assert.Single(CatalogReconciliationPolicy.Reconcile(local, remote));

        Assert.Equal(SourceReconciliationState.Present, result.State);
    }

    [Fact]
    public void Remote_identity_with_no_local_record_is_Added()
    {
        var local = Array.Empty<LocalCatalogEntry>();
        var remote = new[] { new RemoteCatalogEntry(7, "New Snack") };

        var result = Assert.Single(CatalogReconciliationPolicy.Reconcile(local, remote));

        Assert.Equal(7, result.ExternalId);
        Assert.Equal(SourceReconciliationState.Added, result.State);
        Assert.Null(result.LocalName);
        Assert.Equal("New Snack", result.RemoteName);
        Assert.NotNull(result.Note);
    }

    [Fact]
    public void Local_identity_no_longer_returned_by_Nayax_is_MissingRemotely_and_the_local_record_is_untouched()
    {
        var local = new[] { new LocalCatalogEntry(3, "Discontinued Chips") };
        var remote = Array.Empty<RemoteCatalogEntry>();

        var result = Assert.Single(CatalogReconciliationPolicy.Reconcile(local, remote));

        Assert.Equal(3, result.ExternalId);
        Assert.Equal(SourceReconciliationState.MissingRemotely, result.State);
        Assert.Equal("Discontinued Chips", result.LocalName);
        Assert.Null(result.RemoteName);
    }

    [Fact]
    public void A_missing_identity_that_reappears_in_a_later_snapshot_returns_to_Present()
    {
        var local = new[] { new LocalCatalogEntry(3, "Machine at Site A") };

        var whileMissing = Assert.Single(CatalogReconciliationPolicy.Reconcile(local, Array.Empty<RemoteCatalogEntry>()));
        Assert.Equal(SourceReconciliationState.MissingRemotely, whileMissing.State);

        var afterReappearing = Assert.Single(CatalogReconciliationPolicy.Reconcile(local, [new RemoteCatalogEntry(3, "Machine at Site A")]));
        Assert.Equal(SourceReconciliationState.Present, afterReappearing.State);
    }

    [Fact]
    public void Same_identity_with_a_meaningfully_different_name_on_each_side_is_MappingChanged()
    {
        var local = new[] { new LocalCatalogEntry(5, "Old Name") };
        var remote = new[] { new RemoteCatalogEntry(5, "New Name") };

        var result = Assert.Single(CatalogReconciliationPolicy.Reconcile(local, remote));

        Assert.Equal(SourceReconciliationState.MappingChanged, result.State);
        Assert.Equal("Old Name", result.LocalName);
        Assert.Equal("New Name", result.RemoteName);
        Assert.Contains("Old Name", result.Note);
        Assert.Contains("New Name", result.Note);
    }

    [Fact]
    public void Nayax_returning_two_entries_for_the_same_identifier_is_ConflictingIdentity()
    {
        var local = new[] { new LocalCatalogEntry(9, "Machine 9") };
        var remote = new[]
        {
            new RemoteCatalogEntry(9, "Machine 9"),
            new RemoteCatalogEntry(9, "Machine 9 Duplicate")
        };

        var result = Assert.Single(CatalogReconciliationPolicy.Reconcile(local, remote));

        Assert.Equal(SourceReconciliationState.ConflictingIdentity, result.State);
        Assert.Contains("2 entries", result.Note);
    }

    [Fact]
    public void Local_history_recording_more_than_one_name_for_an_identifier_is_ConflictingIdentity()
    {
        var local = new[] { new LocalCatalogEntry(9, "Machine 9 (renamed)", PriorNames: ["Machine 9"]) };
        var remote = new[] { new RemoteCatalogEntry(9, "Machine 9 (renamed)") };

        var result = Assert.Single(CatalogReconciliationPolicy.Reconcile(local, remote));

        Assert.Equal(SourceReconciliationState.ConflictingIdentity, result.State);
        Assert.Contains("Machine 9 (renamed)", result.Note);
        Assert.Contains("Machine 9", result.Note);
    }

    [Fact]
    public void Local_prior_names_that_are_only_case_variants_of_the_current_name_are_not_a_conflict()
    {
        var local = new[] { new LocalCatalogEntry(9, "Machine 9", PriorNames: ["machine 9", "MACHINE 9"]) };
        var remote = new[] { new RemoteCatalogEntry(9, "Machine 9") };

        var result = Assert.Single(CatalogReconciliationPolicy.Reconcile(local, remote));

        Assert.Equal(SourceReconciliationState.Present, result.State);
    }

    [Fact]
    public void Reconcile_produces_one_row_per_distinct_identifier_across_both_sides_ordered_by_identifier()
    {
        var local = new[]
        {
            new LocalCatalogEntry(2, "B"),
            new LocalCatalogEntry(1, "A")
        };
        var remote = new[]
        {
            new RemoteCatalogEntry(2, "B"),
            new RemoteCatalogEntry(3, "C")
        };

        var result = CatalogReconciliationPolicy.Reconcile(local, remote);

        Assert.Equal([1L, 2L, 3L], result.Select(entry => entry.ExternalId));
        Assert.Equal(SourceReconciliationState.MissingRemotely, result[0].State);
        Assert.Equal(SourceReconciliationState.Present, result[1].State);
        Assert.Equal(SourceReconciliationState.Added, result[2].State);
    }
}
