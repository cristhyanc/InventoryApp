using Inventory.Domain.Exceptions;
using Inventory.Domain.PriceCatalogue;

namespace Inventory.Application.PriceCatalogue;

/// <summary>
/// The server-enforced bounds on one business lookup of the shared master-product catalogue
/// (issue #1049, parent #817): what a search term must be, and how big a page is.
///
/// <para><strong>Every bound here is the server's and not the client's.</strong> This is the one
/// member-facing read of the master products that takes a caller-supplied search term and page at
/// all, so what a request may ask for is decided in one place: a search shorter than
/// <see cref="MinSearchLength"/> is refused rather than answered with the catalogue, a search longer
/// than <see cref="MaxSearchLength"/> is refused rather than truncated into a search for something
/// else, a missing or absurd page size becomes a bounded one, and the page actually served is
/// reported back.</para>
///
/// <para>The page size is <c>MasterProductCataloguePaging</c>'s rule with the issue's own numbers -
/// ten by default and twenty at most, because this answers a picker on a form rather than an
/// administrator's catalogue page. The search term is <em>required</em>, which is the one place these
/// bounds are stricter than that surface's: the Super Admin may browse the whole catalogue, and a
/// business may only look something up in it.</para>
/// </summary>
public static class MasterProductSuggestionPaging
{
    /// <summary>The page size used when the client does not ask for one.</summary>
    public const int DefaultPageSize = 10;

    /// <summary>The largest page the server will serve, whatever the client asks for.</summary>
    public const int MaxPageSize = 20;

    /// <summary>
    /// The shortest accepted search term, measured on the trimmed value. One character would match a
    /// large part of the catalogue and say nothing about what the person is looking for, which is a
    /// bounded page of an arbitrary slice rather than a lookup.
    /// </summary>
    public const int MinSearchLength = 2;

    /// <summary>
    /// The longest accepted search term, measured on the trimmed value. It is
    /// <see cref="MasterProductPolicy.MaxNameLength"/> rather than a number of its own: the longest
    /// stored value a search could usefully equal is a master product's name, so anything longer
    /// cannot match a row at all.
    /// </summary>
    public const int MaxSearchLength = MasterProductPolicy.MaxNameLength;

    /// <summary>
    /// What a caller who submitted no search term, only whitespace, or fewer than
    /// <see cref="MinSearchLength"/> characters is told. One sentence for all three, because they are
    /// one problem: nothing was asked for.
    /// </summary>
    public const string SearchTooShortMessage =
        "A master product search needs at least 2 characters.";

    /// <summary>
    /// What a caller who submitted more than <see cref="MaxSearchLength"/> characters is told. It is
    /// refused rather than truncated: a truncated search is a search for something else.
    /// </summary>
    public const string SearchTooLongMessage =
        "A master product search must be 200 characters or fewer.";

    /// <summary>The page number and page size the server will actually serve for a requested pair.</summary>
    public static (int Page, int PageSize) Resolve(int? page, int? pageSize) =>
        (page is > 0 ? page.Value : 1,
            pageSize is > 0 ? Math.Min(pageSize.Value, MaxPageSize) : DefaultPageSize);

    /// <summary>
    /// The search term as the store receives it: trimmed, and never blank or out of bounds.
    /// </summary>
    /// <exception cref="DomainValidationException">
    /// The term is absent, blank, shorter than <see cref="MinSearchLength"/> or longer than
    /// <see cref="MaxSearchLength"/> once trimmed. Nothing is read.
    /// </exception>
    public static string RequireSearch(string? search)
    {
        var trimmed = (search ?? string.Empty).Trim();

        if (trimmed.Length < MinSearchLength)
        {
            throw new DomainValidationException(SearchTooShortMessage);
        }

        return trimmed.Length <= MaxSearchLength
            ? trimmed
            : throw new DomainValidationException(SearchTooLongMessage);
    }

    /// <summary>
    /// How many rows a resolved page skips, as a number a database can be given.
    ///
    /// <para>Computed in 64-bit arithmetic and then bounded, because the product of a page number a
    /// client chose and a page size does not fit a 32-bit integer: a page number near
    /// <see cref="int.MaxValue"/> would otherwise overflow into a <em>negative</em> offset, which is
    /// not an empty page at all. Asking for a page past the end of the catalogue answers nothing,
    /// which is the honest answer to it.</para>
    /// </summary>
    public static int SkipFor(int page, int pageSize) =>
        (int)Math.Min((long)(page - 1) * pageSize, int.MaxValue - (long)pageSize);
}

/// <summary>
/// What a business asked the shared catalogue for (issue #1049): a search term, and which bounded
/// page of the matches it wants.
///
/// <para>Three values, and <em>none</em> of them is a business: the business is the caller's own,
/// resolved from their membership by the request pipeline, and there is no field here for one. There
/// is also no sort, no predicate, no column list and no master product id - the only things a caller
/// may say are what they are looking for and which page of the answer they are on.</para>
/// </summary>
public sealed record MasterProductSuggestionQuery(
    string? Search = null,
    int? Page = null,
    int? PageSize = null);

/// <summary>
/// The resolved query the persistence port is given: a validated, trimmed search term and the
/// requested page as a bounded <see cref="Skip"/>/<see cref="Take"/>.
///
/// <para>The store applies these and nothing else. It decides no bound, no default and no sort, and
/// it adds no business predicate, because the one table it reads is a platform table that belongs to
/// no business at all (AGENTS.md § Tenant ownership and data isolation).</para>
/// </summary>
public sealed record MasterProductSuggestionFilter(string Search, int Skip, int Take);

/// <summary>
/// One master product as the <em>catalogue</em> knows it, which is all the store answers with
/// (issue #1049): the key and the four values that say what the product is.
///
/// <para>Deliberately no prefill: the two prefill strings are
/// <see cref="MasterProductPrefill"/>'s answer and are computed once, in the use case, so a second
/// adapter cannot produce a different Name for the same row. And deliberately nothing else at all -
/// no normalised key, no instants, no supplier listing, no price and nothing about any business,
/// because none of those is read by the statement behind this port.</para>
/// </summary>
public sealed record MasterProductIdentity(
    long Id,
    string Brand,
    string Name,
    decimal SizeValue,
    MasterProductSizeUnit SizeUnit);

/// <summary>
/// One master product as a business may see it (issue #1049): its safe identity, and the two values
/// a product form is filled in with.
///
/// <para><strong>This is the reviewed limit of a member-facing platform-table read</strong> (the
/// owner's decision of 1 November 2026): the id, the brand, the name and the size - and the prefill
/// computed from exactly those. Nothing else about the shared catalogue travels here: no normalised
/// key, no instants, no supplier listing, no match suggestion, no price and no mapping; and nothing
/// about any business can, because a master product carries no business identity at all. Widening it
/// is a tenancy decision needing its own reviewed issue (AGENTS.md § Tenant ownership and data
/// isolation).</para>
/// </summary>
/// <param name="Id">The master product's own key, which #1051's create will name.</param>
/// <param name="Brand">The brand.</param>
/// <param name="Name">The name or variant.</param>
/// <param name="SizeValue">The size of one item.</param>
/// <param name="SizeUnit">What that size is measured in.</param>
/// <param name="ProductName">The product Name to prefill: <c>{Brand} {Name}</c>, single-spaced.</param>
/// <param name="UnitLabel">The unit label to prefill: the size and its unit code, with no space.</param>
public sealed record MasterProductSuggestion(
    long Id,
    string Brand,
    string Name,
    decimal SizeValue,
    MasterProductSizeUnit SizeUnit,
    string ProductName,
    string UnitLabel);

/// <summary>
/// One bounded page of suggestions (issue #1049). <see cref="Page"/>/<see cref="PageSize"/> are the
/// resolved values the caller actually received, which may differ from what it requested.
///
/// <para><see cref="HasMore"/> rather than a total count, deliberately. A picker needs to know
/// whether to narrow the search; how many products the shared catalogue holds for a term is a fact
/// about the catalogue that no business needs, and it is one more statement for the database to make
/// on a keystroke.</para>
/// </summary>
public sealed record MasterProductSuggestionPage(
    IReadOnlyList<MasterProductSuggestion> Products,
    int Page,
    int PageSize,
    bool HasMore);
