using Inventory.Application.Reporting.Shared;
using Xunit;

namespace InventoryApi.Tests.Application.Reporting.Shared;

public class ReportingQualityTests
{
    [Fact]
    public void Each_flag_passes_through_as_the_identically_named_dto_field_when_true()
    {
        var quality = ReportingQuality.Quality(
            missingStatus: true, historicalCostUnavailable: true, gstClassificationMissing: true,
            commissionNotPersisted: true, containsUnmappedProducts: true);

        Assert.True(quality.MissingStatus);
        Assert.True(quality.HistoricalCostUnavailable);
        Assert.True(quality.GstClassificationMissing);
        Assert.True(quality.CommissionNotPersisted);
        Assert.True(quality.ContainsUnmappedProducts);
    }

    [Fact]
    public void Each_flag_passes_through_as_the_identically_named_dto_field_when_false()
    {
        var quality = ReportingQuality.Quality(
            missingStatus: false, historicalCostUnavailable: false, gstClassificationMissing: false,
            commissionNotPersisted: false, containsUnmappedProducts: false);

        Assert.False(quality.MissingStatus);
        Assert.False(quality.HistoricalCostUnavailable);
        Assert.False(quality.GstClassificationMissing);
        Assert.False(quality.CommissionNotPersisted);
        Assert.False(quality.ContainsUnmappedProducts);
    }

    [Fact]
    public void Flags_are_independent_of_one_another()
    {
        var quality = ReportingQuality.Quality(
            missingStatus: true, historicalCostUnavailable: false, gstClassificationMissing: true,
            commissionNotPersisted: false, containsUnmappedProducts: true);

        Assert.True(quality.MissingStatus);
        Assert.False(quality.HistoricalCostUnavailable);
        Assert.True(quality.GstClassificationMissing);
        Assert.False(quality.CommissionNotPersisted);
        Assert.True(quality.ContainsUnmappedProducts);
    }

    [Fact]
    public void With_no_report_specific_notes_only_the_four_standard_disclaimers_are_present()
    {
        var quality = ReportingQuality.Quality(false, false, false, false, false);

        Assert.Equal(4, quality.Notes!.Count);
        Assert.Contains(quality.Notes, note => note.Contains("Nayax status IDs are stored raw"));
        Assert.Contains(quality.Notes, note => note.Contains("Historical COGS uses the persisted sale cost"));
        Assert.Contains(quality.Notes, note => note.Contains("Commission is calculated from effective-dated"));
        Assert.Contains(quality.Notes, note => note.Contains("GST classification is not persisted on sales"));
    }

    [Fact]
    public void Multiple_report_specific_notes_are_appended_as_separate_ordered_entries_not_joined()
    {
        var quality = ReportingQuality.Quality(false, false, false, false, false,
            notes: new List<string> { "First specific note.", "Second specific note." });

        Assert.Equal(6, quality.Notes!.Count);
        Assert.Equal("First specific note.", quality.Notes[4]);
        Assert.Equal("Second specific note.", quality.Notes[5]);
        Assert.DoesNotContain(quality.Notes, note => note.Contains("First specific note. Second specific note."));
    }

    [Fact]
    public void Empty_or_null_report_specific_notes_are_not_added_as_blank_entries()
    {
        var quality = ReportingQuality.Quality(false, false, false, false, false,
            notes: new List<string> { "", "A real note." });

        Assert.Equal(5, quality.Notes!.Count);
        Assert.DoesNotContain(quality.Notes, string.IsNullOrEmpty);
        Assert.Equal("A real note.", quality.Notes.Last());
    }

    [Fact]
    public void No_notes_argument_still_returns_only_the_standard_disclaimers()
    {
        var quality = ReportingQuality.Quality(false, false, false, false, false, notes: null);

        Assert.Equal(4, quality.Notes!.Count);
    }

    [Fact]
    public void Conditional_with_no_notes_returns_an_empty_note_list_so_a_report_can_hide_the_section()
    {
        var quality = ReportingQuality.Conditional(false, false, false, false, false);

        Assert.Empty(quality.Notes!);
    }

    [Fact]
    public void Conditional_returns_only_the_callers_own_notes_and_none_of_the_standard_disclaimers()
    {
        var quality = ReportingQuality.Conditional(false, false, false, false, false,
            notes: new List<string> { "First specific note.", "Second specific note." });

        Assert.Equal(["First specific note.", "Second specific note."], quality.Notes!);
        Assert.DoesNotContain(quality.Notes!, note => note.Contains("Nayax status IDs are stored raw"));
        Assert.DoesNotContain(quality.Notes!, note => note.Contains("GST classification is not persisted on sales"));
    }

    [Fact]
    public void Conditional_skips_empty_notes_and_passes_every_flag_through_unchanged()
    {
        var quality = ReportingQuality.Conditional(
            missingStatus: true, historicalCostUnavailable: false, gstClassificationMissing: true,
            commissionNotPersisted: false, containsUnmappedProducts: true,
            notes: new List<string> { "", "A real note." });

        Assert.Equal(["A real note."], quality.Notes!);
        Assert.True(quality.MissingStatus);
        Assert.False(quality.HistoricalCostUnavailable);
        Assert.True(quality.GstClassificationMissing);
        Assert.False(quality.CommissionNotPersisted);
        Assert.True(quality.ContainsUnmappedProducts);
    }

    [Fact]
    public void Conditional_leaves_Quality_untouched_for_the_report_families_that_still_use_it()
    {
        ReportingQuality.Conditional(false, false, false, false, false);

        var quality = ReportingQuality.Quality(false, false, false, false, false);

        Assert.Equal(4, quality.Notes!.Count);
    }
}
