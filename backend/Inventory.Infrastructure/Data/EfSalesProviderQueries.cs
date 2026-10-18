using Inventory.Domain.Providers;
using Inventory.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Infrastructure.Data;

/// <summary>
/// How a read of a provider identity asks which provider issued it, beside the
/// <see cref="AppDbContext"/> whose entities it is written against (issue #759, parent #329 decision
/// P1 as revised on 18 October 2026).
///
/// <para><strong>Why anything has to ask.</strong> A business has zero or one sales provider, and
/// <c>Product.ExternalProductId</c>/<c>Category.ExternalCategoryId</c> hold *that* provider's id -
/// the column itself does not say which system issued it. So a lookup that turns a Nayax id into a
/// local row resolves nothing unless the owning business's provider is Nayax: the day a second
/// provider exists, the same column holds its ids, and reading one as the other would resolve a sale
/// onto the wrong product. The provider used to be a column of every link row; it is now one fact
/// about the business.</para>
///
/// <para>There are two shapes of that question, because there are two kinds of caller.
/// <see cref="ProductsIdentifiedBy"/> and <see cref="CategoriesIdentifiedBy"/> are *queries*: the
/// provider is checked against the owning business of each row, in the same statement that reads the
/// id, so a lookup stays one round trip and needs no assumption about the context's scope.
/// <see cref="CurrentBusinessSellsThroughAsync"/> is a *guard*, for a writer that must refuse to run
/// at all for a business whose provider is not its own.</para>
///
/// <para><c>Businesses</c> is not tenant-owned and carries no query filter, so the business is named
/// explicitly in both: by the row's own <c>BusinessId</c> in the queries, and by the context's
/// resolved scope in the guard - never by a value a request supplied. The guard fails closed: a
/// caller with no resolved business reports no provider, exactly as it reads no tenant-owned row.
/// The queries stay subject to the ordinary tenant query filter on <c>Products</c>/<c>Categories</c>,
/// which is what scopes them.</para>
///
/// <para>It is <c>internal</c> because every caller is an Infrastructure persistence adapter, the
/// same reason <see cref="EfNayaxSalesQueries"/> is.</para>
/// </summary>
internal static class EfSalesProviderQueries
{
    /// <summary>
    /// The current business's products that carry an external id issued by
    /// <paramref name="provider"/>: the rows whose id column is set and whose business sells through
    /// that provider. Untracked, because every caller of it is a lookup.
    /// </summary>
    public static IQueryable<Product> ProductsIdentifiedBy(AppDbContext db, ExternalProvider provider) =>
        db.Products
            .AsNoTracking()
            .Where(product =>
                product.ExternalProductId != null
                && db.Businesses.Any(business =>
                    business.Id == product.BusinessId && business.SalesProvider == provider));

    /// <summary>The category half of <see cref="ProductsIdentifiedBy"/>.</summary>
    public static IQueryable<Category> CategoriesIdentifiedBy(AppDbContext db, ExternalProvider provider) =>
        db.Categories
            .AsNoTracking()
            .Where(category =>
                category.ExternalCategoryId != null
                && db.Businesses.Any(business =>
                    business.Id == category.BusinessId && business.SalesProvider == provider));

    /// <summary>
    /// Whether the current business's sales provider is <paramref name="provider"/> - the question a
    /// writer of that provider's ids has to answer before it writes one. <see langword="false"/>
    /// when the business has no provider, when no business is resolved, or when no such business row
    /// exists.
    /// </summary>
    public static async Task<bool> CurrentBusinessSellsThroughAsync(
        AppDbContext db, ExternalProvider provider, CancellationToken cancellationToken)
    {
        if (db.CurrentBusinessId is not { } businessId)
        {
            return false;
        }

        return await db.Businesses
            .AsNoTracking()
            .Where(business => business.Id == businessId)
            .Select(business => business.SalesProvider)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false) == provider;
    }
}
