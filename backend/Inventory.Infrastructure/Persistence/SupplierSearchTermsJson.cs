using System.Text.Json;
using System.Text.Json.Serialization;
using Inventory.Domain.PriceCatalogue;
using Inventory.Domain.Suppliers;

namespace Inventory.Infrastructure.Persistence;

/// <summary>
/// The stored form of a master product's supplier search terms (issue #1138): the
/// <c>MasterProducts.SupplierSearchTerms</c> column's JSON, written by the Master Products edit and
/// read by that page and by the collector work list.
///
/// <para>The column holds <see langword="null"/> rather than an empty array when there are no
/// entries, so "nobody configured anything" is one stored state. What is written has already passed
/// <see cref="SupplierSearchTermsPolicy.Normalise"/>, and nothing else writes the column.</para>
/// </summary>
internal static class SupplierSearchTermsJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>The column value for <paramref name="entries"/>; <see langword="null"/> for none.</summary>
    internal static string? Serialize(IReadOnlyList<SupplierSearchTerms> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        if (entries.Count == 0)
        {
            return null;
        }

        return JsonSerializer.Serialize(
            entries.Select(entry => new StoredEntry(
                entry.SupplierPresetId,
                entry.PriceConnector,
                [.. entry.SearchPhrases],
                [.. entry.FlavourAliases])).ToList(),
            Options);
    }

    /// <summary>The entries a stored column value holds; none for <see langword="null"/> or blank.</summary>
    internal static IReadOnlyList<SupplierSearchTerms> Deserialize(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored))
        {
            return [];
        }

        var entries = JsonSerializer.Deserialize<List<StoredEntry>>(stored, Options) ?? [];

        return [.. entries.Select(entry => new SupplierSearchTerms(
            entry.SupplierPresetId,
            entry.PriceConnector,
            entry.SearchPhrases ?? [],
            entry.FlavourAliases ?? []))];
    }

    private sealed record StoredEntry(
        int? SupplierPresetId,
        SupplierPriceConnector? PriceConnector,
        List<string>? SearchPhrases,
        List<string>? FlavourAliases);
}
