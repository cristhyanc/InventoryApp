using System.Text;
using Inventory.Application.PriceCatalogue;
using Inventory.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Infrastructure.Persistence;

/// <summary>
/// The EF Core implementation of <see cref="IMasterProductSuggestionStore"/> (issue #1049, parent
/// #817): the one read a business makes of the shared master-product catalogue.
///
/// <para><strong>One statement over one platform table, and nothing else.</strong> It reads
/// <c>MasterProducts</c> - a brand, a name and a size are facts about a product and about no
/// business - and selects five columns of it. It touches no supplier listing, no price, no match
/// suggestion, no price-tracking mapping, no product and no business, so there is no statement here
/// in which a business id could appear and nowhere in the answer for one to travel.</para>
///
/// <para><strong>It needs no <c>IgnoreQueryFilters</c> and no write lease.</strong> A platform table
/// carries no business key and has no query filter, so this read answers the same catalogue whether
/// the request's <c>BusinessScope</c> is one business's or denied - and the central filters still
/// match none of anybody's products, mappings or businesses, because this adapter reads none. It
/// writes nothing at all: no <c>SaveChanges</c>, no transaction, no lease, so a lookup leaves every
/// master product exactly as it was (AGENTS.md § Tenant ownership and data isolation).</para>
///
/// <para><strong>The order is fixed and ends in the row's own key</strong> - brand, name, size, id -
/// because a page read a slice at a time has to come out in the same order on every request. No
/// caller chooses a sort, a predicate or a column list; the only things it may narrow by are the
/// validated search term and the bounded slice
/// <see cref="MasterProductSuggestionPaging"/> already resolved.</para>
/// </summary>
public sealed class EfMasterProductSuggestionStore : IMasterProductSuggestionStore
{
    /// <summary>
    /// The character <see cref="LikePattern"/> writes before a <c>%</c>, <c>_</c> or itself, so a
    /// character <c>LIKE</c> treats specially is matched literally when a person types it into the
    /// search box rather than acting as a wildcard. It is
    /// <see cref="EfMasterProductCatalogueStore"/>'s rule, because it is the same question asked of
    /// the same table by a different caller.
    /// </summary>
    private const char SearchEscapeCharacter = '\\';

    private readonly AppDbContext _db;

    public EfMasterProductSuggestionStore(AppDbContext db)
    {
        _db = db;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<MasterProductIdentity>> SearchAsync(
        MasterProductSuggestionFilter filter,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var pattern = $"%{LikePattern(filter.Search)}%";
        var escapeCharacter = SearchEscapeCharacter.ToString();

        return await _db.MasterProducts
            .AsNoTracking()
            .Where(master =>
                EF.Functions.Like(master.Brand, pattern, escapeCharacter)
                    || EF.Functions.Like(master.Name, pattern, escapeCharacter))
            .OrderBy(master => master.Brand)
            .ThenBy(master => master.Name)
            .ThenBy(master => master.SizeValue)
            .ThenBy(master => master.Id)
            .Skip(filter.Skip)
            .Take(filter.Take)
            .Select(master => new MasterProductIdentity(
                master.Id,
                master.Brand,
                master.Name,
                master.SizeValue,
                master.SizeUnit))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// <paramref name="value"/> with every character <c>LIKE</c> treats specially - <c>%</c>, <c>_</c>
    /// and <see cref="SearchEscapeCharacter"/> itself - preceded by the escape character.
    ///
    /// <para><c>LIKE</c> rather than <c>Contains</c>, which is <see cref="EfProductCatalogStore"/>'s
    /// reason exactly (issue #827): EF Core's SQLite provider translates <c>Contains</c> to
    /// <c>instr(…)</c>, which compares byte-for-byte, so a search typed in a different case than the
    /// stored brand or name would find nothing. SQLite's <c>LIKE</c> folds ASCII letter case by
    /// default - though it leaves non-ASCII letters unfolded - and the escape character keeps a
    /// literal <c>%</c> or <c>_</c> a person types from acting as a wildcard, which would otherwise
    /// let one keystroke match the whole catalogue.</para>
    /// </summary>
    private static string LikePattern(string value)
    {
        var pattern = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (character is '%' or '_' or SearchEscapeCharacter)
            {
                pattern.Append(SearchEscapeCharacter);
            }

            pattern.Append(character);
        }

        return pattern.ToString();
    }
}
