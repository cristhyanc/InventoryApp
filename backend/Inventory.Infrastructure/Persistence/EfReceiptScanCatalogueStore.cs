using Inventory.Application.ReceiptScanning;
using Inventory.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Infrastructure.Persistence;

/// <summary>
/// The EF Core implementation of <see cref="IReceiptScanCatalogueStore"/> (issue #1163): the one read
/// receipt scanning makes of the business's own data.
///
/// <para><strong>Two columns each, and nothing else.</strong> It selects an id and a name from
/// <c>Products</c> and from <c>Suppliers</c> - no cost, no stock, no price, no GST rule, no contact
/// detail - because everything it returns is sent to an external reader, and what is not selected
/// cannot leave. Two statements, both <c>AsNoTracking</c>, both read-only: no <c>SaveChanges</c>, no
/// transaction, no lease.</para>
///
/// <para><strong>The business is the request's own, through the ordinary tenant query filters.</strong>
/// Neither method takes a business id, a predicate, a column list, a page or a sort, and there is no
/// <c>IgnoreQueryFilters</c> here - so a scan is read against exactly the caller's own catalogue, and
/// nothing else can be asked for (AGENTS.md § Tenant ownership and data isolation).</para>
///
/// <para>Inactive products and suppliers the business has marked as not used are left out: a line
/// matched to one of them would suggest buying stock the business has retired or recording a purchase
/// against a supplier it has stopped using. The order is name then key, so two products of the same
/// name are presented to the reader in a stable order rather than the database's.</para>
/// </summary>
public sealed class EfReceiptScanCatalogueStore : IReceiptScanCatalogueStore
{
    private readonly AppDbContext _db;

    public EfReceiptScanCatalogueStore(AppDbContext db)
    {
        _db = db;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ReceiptScanProduct>> ListProductsAsync(CancellationToken cancellationToken) =>
        await _db.Products
            .AsNoTracking()
            .Where(product => product.IsActive)
            .OrderBy(product => product.Name)
            .ThenBy(product => product.Id)
            .Select(product => new ReceiptScanProduct(product.Id, product.Name))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<IReadOnlyList<ReceiptScanSupplier>> ListSuppliersAsync(CancellationToken cancellationToken) =>
        await _db.Suppliers
            .AsNoTracking()
            .Where(supplier => !supplier.IsHidden)
            .OrderBy(supplier => supplier.Name)
            .ThenBy(supplier => supplier.Id)
            .Select(supplier => new ReceiptScanSupplier(supplier.Id, supplier.Name))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
}
