using Inventory.Application.Time;
using Inventory.Domain.Providers;

namespace Inventory.Application.CatalogueReview;

/// <summary>
/// One provider product the business is selling and does not hold a product for (issue #1352).
/// </summary>
/// <param name="NayaxProductId">
/// The provider's own product id, as the opaque <c>ExternalId</c> text a link stores
/// (<see cref="NayaxExternalId"/>). It is deliberately not a number and is never a local product
/// key: the whole point of this read is that no local row carries it.
/// </param>
/// <param name="ProductName">
/// The product name the most recent qualifying sale carried, or <see langword="null"/> when that
/// sale stored none. It is the provider's name for the product as of that sale and is never looked
/// up locally, because locally there is nothing to look up.
/// </param>
public sealed record UnimportedProviderProductExample(string NayaxProductId, string? ProductName);

/// <summary>
/// The Dashboard's "Nayax products are selling but aren't in your products" answer (issue #1352):
/// how many distinct provider products sold in the window carry a provider id no product of this
/// business holds, and the most recently sold few of them by name.
///
/// <para>It carries no amount, price, cost or quantity: it is a count of provider products and the
/// names the sales carried, so every member who may run the import is answered the same figures and
/// nothing about what the business took is published here.</para>
/// </summary>
/// <param name="UnimportedProductCount">
/// The number of <em>distinct</em> provider product ids, so two sales of the same product count
/// once.
/// </param>
/// <param name="Examples">
/// At most <see cref="UnimportedProviderProductAttentionPolicy.MaxExamples"/> of them, most recently
/// sold first.
/// </param>
public sealed record UnimportedProviderProductAttention(
    int UnimportedProductCount,
    IReadOnlyList<UnimportedProviderProductExample> Examples);

/// <summary>
/// Read port for the provider products this business is selling without holding a product for. It
/// reads the caller's own business only, through the central tenant query filters, and writes
/// nothing.
/// </summary>
public interface IUnimportedProviderProductReader
{
    /// <summary>
    /// The distinct provider product ids among this business's stored sales in the given window that
    /// no product of the business - retired products included - carries as its external product id,
    /// with at most <paramref name="maxExamples"/> of them named.
    /// </summary>
    /// <param name="fromUtc">The inclusive start of the window.</param>
    /// <param name="toUtc">The inclusive end of the window.</param>
    /// <param name="maxExamples">How many of them to name, most recently sold first.</param>
    /// <param name="cancellationToken">The request's cancellation token.</param>
    Task<UnimportedProviderProductAttention> ReadAsync(
        DateTime fromUtc, DateTime toUtc, int maxExamples, CancellationToken cancellationToken);
}

/// <summary>
/// The Dashboard's unimported-provider-product read (issue #1352).
///
/// <para><strong>Stored data only.</strong> It makes no Nayax call and starts no catalogue review:
/// the evidence that a provider product is missing locally is the business's own sales, which were
/// stored with no local product precisely because nothing carried the provider's id for them
/// (<c>NayaxSales.ProductId</c> null with <c>ProviderProductIdIsRaw</c> true). Those sales take no
/// stock and carry no cost, which is why somebody has to be told.</para>
///
/// <para><strong>It is not the sale resolution, and it does not go quiet when the sales are
/// resolved.</strong> What clears a provider product from this read is a <em>product</em> that
/// carries its id - the link or create that Import products performs. Attributing the old sales to
/// that product afterwards stays the Owner's separate, audited step (see
/// <see cref="ResolveLinkedProviderSales"/>), and a business that never takes it is still answered a
/// count of zero here, because there is nothing left to import.</para>
///
/// <para><strong>The window is the business's own calendar.</strong> The instant boundaries are
/// derived once, from one reading of the clock, through <see cref="IBusinessCalendar"/> - the
/// business's configured zone, which for this business is <c>Australia/Sydney</c> - so a
/// daylight-saving change inside the window does not move its first day and the behaviour is
/// testable at the boundary with a fixed clock. The rule itself is
/// <see cref="UnimportedProviderProductAttentionPolicy"/>.</para>
/// </summary>
public sealed class GetUnimportedProviderProductAttention
{
    private readonly IUnimportedProviderProductReader _reader;
    private readonly IClock _clock;
    private readonly IBusinessCalendar _calendar;

    public GetUnimportedProviderProductAttention(
        IUnimportedProviderProductReader reader, IClock clock, IBusinessCalendar calendar)
    {
        _reader = reader;
        _clock = clock;
        _calendar = calendar;
    }

    public Task<UnimportedProviderProductAttention> Handle(CancellationToken cancellationToken)
    {
        // Read once, so the window's first date and its "up to now" end are the same instant's
        // answer and a sale cannot fall outside both halves of one request's window.
        var nowUtc = _clock.UtcNow;
        var fromUtc = _calendar.StartOfBusinessDayUtc(
            UnimportedProviderProductAttentionPolicy.FirstBusinessDate(_calendar.ToBusinessDate(nowUtc)));

        return _reader.ReadAsync(
            fromUtc, nowUtc, UnimportedProviderProductAttentionPolicy.MaxExamples, cancellationToken);
    }
}
