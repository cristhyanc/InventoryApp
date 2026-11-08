using Inventory.Domain.Exceptions;
using Inventory.Domain.Suppliers;

namespace Inventory.Domain.PriceCatalogue;

/// <summary>
/// One master product's search terms for one supplier, or for every supplier on one connector
/// (issue #1138): the phrases the collector searches with, in order, and the other words that
/// supplier uses for the product's flavour.
///
/// <para><strong>Exactly one target.</strong> <see cref="SupplierPresetId"/> names one supplier
/// master record; <see cref="PriceConnector"/> names every supplier collected on that route - one
/// JB Metro entry covers all three JB Metro sites. Never both and never neither.</para>
///
/// <para><strong>A phrase is a query, an alias is evidence.</strong> Each phrase is searched on its
/// own, never joined to another. An alias is only ever used to decide whether a result <em>is</em>
/// this product - <c>Plain</c> for Smith's <c>Original</c> at JB Metro - and is never typed into a
/// search, and it relaxes nothing else: the brand, the product type and the individual unit size
/// must still agree.</para>
/// </summary>
public sealed record SupplierSearchTerms(
    int? SupplierPresetId,
    SupplierPriceConnector? PriceConnector,
    IReadOnlyList<string> SearchPhrases,
    IReadOnlyList<string> FlavourAliases);

/// <summary>
/// What a Super Admin submitted for one entry (issue #1138), exactly as it was typed and before any
/// rule has been applied to it. Every list may be <see langword="null"/>, every text may be blank.
/// </summary>
public sealed record SupplierSearchTermsSubmission(
    int? SupplierPresetId,
    SupplierPriceConnector? PriceConnector,
    IReadOnlyList<string?>? SearchPhrases,
    IReadOnlyList<string?>? FlavourAliases);

/// <summary>
/// The rules of a master product's supplier search terms (issue #1138): what an entry may hold, how
/// its text is cleaned, and which entry a work item for one supplier receives.
///
/// <para><strong>Two phrases at most.</strong> The collector never searches more than two wordings
/// for one work item, so a third phrase could never be searched; it is refused rather than stored
/// and silently ignored. Phrases are trimmed, inner whitespace is collapsed, blanks are dropped and a
/// phrase that differs from an earlier one only by case is the same phrase - so no query is ever
/// repeated - and the order a person chose is kept.</para>
///
/// <para><strong>A supplier's own entry wins as a whole.</strong> When a master product has an
/// entry for the supplier preset being searched, that entry is the configuration; an entry for that
/// preset's connector is used only when there is none. The two are never merged, so what the
/// collector receives is always one entry a person wrote, never a combination nobody did.</para>
///
/// <para>It stores nothing and reads nothing, like every policy in this folder: whether a named
/// supplier preset exists is answered inside the master product's own write transaction.</para>
/// </summary>
public static class SupplierSearchTermsPolicy
{
    /// <summary>The most phrases one entry may hold: the collector's two-wording limit.</summary>
    public const int MaxSearchPhrasesPerEntry = 2;

    /// <summary>The most flavour aliases one entry may hold.</summary>
    public const int MaxFlavourAliasesPerEntry = 5;

    /// <summary>The most entries one master product may hold.</summary>
    public const int MaxEntriesPerMasterProduct = 20;

    /// <summary>The longest phrase stored, after trimming.</summary>
    public const int MaxSearchPhraseLength = 100;

    /// <summary>The longest flavour alias stored, after trimming.</summary>
    public const int MaxFlavourAliasLength = 60;

    public const string TargetRequiredMessage =
        "Each supplier search entry must name either one supplier or one connector, not both.";

    public const string UnknownConnectorMessage =
        "A supplier search entry names a connector this application does not collect with.";

    public const string DuplicateTargetMessage =
        "A master product can hold only one supplier search entry for each supplier and each connector.";

    public const string EmptyEntryMessage =
        "A supplier search entry needs at least one search phrase or flavour alias. Remove the entry instead.";

    public const string UnknownSupplierPresetMessage =
        "A supplier search entry names a supplier that does not exist.";

    public static readonly string TooManyPhrasesMessage =
        $"A supplier search entry can hold at most {MaxSearchPhrasesPerEntry} search phrases, because the "
            + "collector never searches more than two wordings for one product at one supplier.";

    public static readonly string TooManyAliasesMessage =
        $"A supplier search entry can hold at most {MaxFlavourAliasesPerEntry} flavour aliases.";

    public static readonly string TooManyEntriesMessage =
        $"A master product can hold at most {MaxEntriesPerMasterProduct} supplier search entries.";

    public static readonly string PhraseTooLongMessage =
        $"A search phrase can be at most {MaxSearchPhraseLength} characters.";

    public static readonly string AliasTooLongMessage =
        $"A flavour alias can be at most {MaxFlavourAliasLength} characters.";

    public const string ControlCharacterMessage =
        "Search phrases and flavour aliases cannot contain line breaks or other control characters.";

    /// <summary>
    /// Validates and cleans a submitted set of entries, answering them in the order they were given.
    /// <see langword="null"/> and an empty list are both "no entries".
    /// </summary>
    /// <exception cref="DomainValidationException">
    /// Any entry breaks a rule above. Nothing is answered for half a submission.
    /// </exception>
    public static IReadOnlyList<SupplierSearchTerms> Normalise(
        IReadOnlyList<SupplierSearchTermsSubmission?>? submissions)
    {
        if (submissions is null || submissions.Count == 0)
        {
            return [];
        }

        if (submissions.Count > MaxEntriesPerMasterProduct)
        {
            throw new DomainValidationException(TooManyEntriesMessage);
        }

        List<SupplierSearchTerms> entries = [];
        HashSet<int> presets = [];
        HashSet<SupplierPriceConnector> connectors = [];

        foreach (var submission in submissions)
        {
            if (submission is null
                || (submission.SupplierPresetId is null) == (submission.PriceConnector is null))
            {
                throw new DomainValidationException(TargetRequiredMessage);
            }

            if (submission.PriceConnector is { } connector && !Enum.IsDefined(connector))
            {
                throw new DomainValidationException(UnknownConnectorMessage);
            }

            if (submission.SupplierPresetId is { } presetId && presetId <= 0)
            {
                throw new DomainValidationException(UnknownSupplierPresetMessage);
            }

            var added = submission.SupplierPresetId is { } preset
                ? presets.Add(preset)
                : connectors.Add(submission.PriceConnector!.Value);

            if (!added)
            {
                throw new DomainValidationException(DuplicateTargetMessage);
            }

            var phrases = CleanList(submission.SearchPhrases, MaxSearchPhraseLength, PhraseTooLongMessage);
            var aliases = CleanList(submission.FlavourAliases, MaxFlavourAliasLength, AliasTooLongMessage);

            if (phrases.Count > MaxSearchPhrasesPerEntry)
            {
                throw new DomainValidationException(TooManyPhrasesMessage);
            }

            if (aliases.Count > MaxFlavourAliasesPerEntry)
            {
                throw new DomainValidationException(TooManyAliasesMessage);
            }

            if (phrases.Count == 0 && aliases.Count == 0)
            {
                throw new DomainValidationException(EmptyEntryMessage);
            }

            entries.Add(new SupplierSearchTerms(
                submission.SupplierPresetId,
                submission.PriceConnector,
                phrases,
                aliases));
        }

        return entries;
    }

    /// <summary>
    /// The one entry a work item for <paramref name="supplierPresetId"/> receives: that preset's own
    /// entry, otherwise the entry for <paramref name="priceConnector"/>, otherwise none.
    /// </summary>
    public static SupplierSearchTerms? EffectiveFor(
        IReadOnlyList<SupplierSearchTerms>? entries,
        int supplierPresetId,
        SupplierPriceConnector? priceConnector)
    {
        if (entries is null || entries.Count == 0)
        {
            return null;
        }

        return entries.FirstOrDefault(entry => entry.SupplierPresetId == supplierPresetId)
            ?? (priceConnector is { } connector
                ? entries.FirstOrDefault(entry =>
                    entry.SupplierPresetId is null && entry.PriceConnector == connector)
                : null);
    }

    /// <summary>
    /// Trims each value, collapses its inner whitespace, drops blanks and drops a value that repeats
    /// an earlier one ignoring case, keeping the order given.
    /// </summary>
    private static List<string> CleanList(
        IReadOnlyList<string?>? values,
        int maxLength,
        string tooLongMessage)
    {
        List<string> cleaned = [];

        if (values is null)
        {
            return cleaned;
        }

        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            if (value.Any(char.IsControl))
            {
                throw new DomainValidationException(ControlCharacterMessage);
            }

            var collapsed = string.Join(' ', value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

            if (collapsed.Length > maxLength)
            {
                throw new DomainValidationException(tooLongMessage);
            }

            if (seen.Add(collapsed))
            {
                cleaned.Add(collapsed);
            }
        }

        return cleaned;
    }
}
