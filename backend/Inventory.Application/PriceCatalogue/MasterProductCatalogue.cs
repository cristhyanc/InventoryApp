namespace Inventory.Application.PriceCatalogue;

/// <summary>
/// One bounded, searchable page of the shared master-product catalogue (issue #1023, parent #817):
/// what the Super Admin Master products page is built from.
///
/// <para>It owns exactly one decision, and it is deliberately not the client's: how big a page is and
/// which one this is, through <see cref="MasterProductCataloguePaging"/>. Everything else - the order,
/// the search - is fixed by the port behind it, and the answer reports the page that was actually
/// served rather than the one that was asked for.</para>
///
/// <para>This is a read. Nothing here creates, edits or deletes a master product: that is still the
/// Super Admin review's work alone (issue #928), and the create, edit and delete of a master product
/// are separate issues that follow this one.</para>
/// </summary>
public sealed class ListMasterProducts
{
    private readonly IMasterProductCatalogueStore _store;

    public ListMasterProducts(IMasterProductCatalogueStore store)
    {
        _store = store;
    }

    public async Task<MasterProductCataloguePage> Handle(
        MasterProductCatalogueQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var (page, pageSize) = MasterProductCataloguePaging.Resolve(query.Page, query.PageSize);

        // Blank is the same as absent: a search box a person cleared must not narrow the catalogue to
        // the products whose brand contains a space.
        var search = string.IsNullOrWhiteSpace(query.Search) ? null : query.Search.Trim();

        var result = await _store
            .ListAsync(
                new MasterProductCatalogueFilter(search, (page - 1) * pageSize, pageSize),
                cancellationToken)
            .ConfigureAwait(false);

        return new MasterProductCataloguePage(result.Products, result.TotalCount, page, pageSize);
    }
}

/// <summary>
/// One master product of the shared catalogue with its supplier listings (issue #1023): the detail
/// behind a row of the Master products page.
///
/// <para>It is a read of the catalogue and of nothing else: no mapping, no business and no business's
/// product is read to build it, and <see langword="null"/> - an id no master product has - is the
/// endpoint's <c>404</c> rather than an empty product.</para>
/// </summary>
public sealed class GetMasterProduct
{
    private readonly IMasterProductCatalogueStore _store;

    public GetMasterProduct(IMasterProductCatalogueStore store)
    {
        _store = store;
    }

    public Task<MasterProductDetail?> Handle(long masterProductId, CancellationToken cancellationToken) =>
        _store.FindAsync(masterProductId, cancellationToken);
}
