using Inventory.Domain.Exceptions;
using Inventory.Domain.PriceCatalogue;

namespace Inventory.Application.PriceCatalogue;

/// <summary>
/// The business lookup of the shared master products (issue #1049, parent #817): what a member who
/// may create products searches to find the product they are about to add, with the Name and unit
/// label to fill the form in with.
///
/// <para>It owns three decisions and no others. <strong>What a search term must be</strong> and
/// <strong>which bounded page this is</strong>, both through
/// <see cref="MasterProductSuggestionPaging"/> - the server's answer rather than the client's; and
/// <strong>what each row prefills</strong>, through the one Domain formatter
/// <see cref="MasterProductPrefill"/>, computed here so the lookup of this issue, the create of
/// #1051 and the autocomplete of #1052 cannot spell one product three ways. The order and the match
/// are the port's, and the business is the request pipeline's - this use case never names one.</para>
///
/// <para><strong>This is a read and it writes nothing.</strong> No master product, supplier listing,
/// match suggestion, price-tracking mapping or product is created, changed or removed by a lookup,
/// and nothing about any business is read to answer one.</para>
/// </summary>
public sealed class SuggestMasterProducts
{
    private readonly IMasterProductSuggestionStore _store;

    public SuggestMasterProducts(IMasterProductSuggestionStore store)
    {
        _store = store;
    }

    /// <returns>The bounded page of matches, with the page that was actually served.</returns>
    /// <exception cref="DomainValidationException">
    /// The search term is absent, blank, too short or too long. Nothing is read.
    /// </exception>
    public async Task<MasterProductSuggestionPage> Handle(
        MasterProductSuggestionQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        // Refused before anything is read, so a request that asked for nothing in particular never
        // reaches the catalogue at all.
        var search = MasterProductSuggestionPaging.RequireSearch(query.Search);

        var (page, pageSize) = MasterProductSuggestionPaging.Resolve(query.Page, query.PageSize);

        // One row more than the page is asked for, which is how "is there more to read?" is answered
        // without a second statement and without publishing how much of the shared catalogue a term
        // matches.
        var found = await _store
            .SearchAsync(
                new MasterProductSuggestionFilter(
                    search,
                    MasterProductSuggestionPaging.SkipFor(page, pageSize),
                    pageSize + 1),
                cancellationToken)
            .ConfigureAwait(false);

        var hasMore = found.Count > pageSize;

        var products = found
            .Take(pageSize)
            .Select(master => new MasterProductSuggestion(
                master.Id,
                master.Brand,
                master.Name,
                master.SizeValue,
                master.SizeUnit,
                MasterProductPrefill.ProductName(master.Brand, master.Name),
                MasterProductPrefill.UnitLabel(master.SizeValue, master.SizeUnit)))
            .ToList();

        return new MasterProductSuggestionPage(products, page, pageSize, hasMore);
    }
}
