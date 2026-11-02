namespace InventoryApi.DTOs;

/// <summary>
/// One master product as the business lookup publishes it (issue #1049, parent #817): the safe
/// identity, and the two values a product form is filled in with.
///
/// <para><strong>This is the reviewed limit of a member-facing platform-table read, not a convenience
/// projection</strong> (the owner's decision of 1 November 2026). The master products are shared by
/// every business, so what a business endpoint may say about one is fixed: the id, the brand, the
/// name, the size - and the prefill computed from exactly those by
/// <c>Inventory.Domain.PriceCatalogue.MasterProductPrefill</c>. The normalised key, the instants,
/// every supplier listing, every observed price, every match suggestion and every mapping are
/// unreachable here, and widening this is a tenancy decision needing its own reviewed issue
/// (AGENTS.md § Tenant ownership and data isolation).</para>
///
/// <para>Nothing here says anything about any business, including the caller's own: a master product
/// carries no business identity at all, so there is nothing on it to leak and nowhere in this record
/// for such a value to travel.</para>
/// </summary>
/// <param name="Id">
/// The master product's own key, which is what the create of #1051 will name to link a product to it.
/// </param>
/// <param name="Brand">The brand.</param>
/// <param name="Name">The name or variant.</param>
/// <param name="SizeValue">The size of one item.</param>
/// <param name="SizeUnit">
/// The code that size is measured in - <c>ml</c>, <c>l</c>, <c>g</c>, <c>kg</c> or <c>each</c>. The
/// published code rather than the enum member name, so what a client renders is the vocabulary
/// <c>MasterProductPolicy.SizeUnitCode</c> defines rather than a programmer's label, exactly as the
/// matched master product of issue #938 publishes it.
/// </param>
/// <param name="ProductName">
/// The product Name to prefill: <c>{Brand} {Name}</c>, single-spaced. The server's own answer rather
/// than a string a client assembles, so one product is not named three ways by the lookup, the create
/// and the autocomplete.
/// </param>
/// <param name="UnitLabel">
/// The unit label to prefill: the size with trailing zeros dropped and then the unit code, with no
/// space - <c>45g</c>, <c>375ml</c>, <c>1.25l</c>, <c>1each</c>. It is a label the automatic matcher
/// can read back as this master product's own size, which is what makes a product created from it
/// matchable.
/// </param>
public record MasterProductSuggestionResponse(
    long Id,
    string Brand,
    string Name,
    decimal SizeValue,
    string SizeUnit,
    string ProductName,
    string UnitLabel);

/// <summary>
/// One bounded page of the business lookup, as
/// <c>GET /api/products/master-product-suggestions</c> answers it (issue #1049).
///
/// <para><see cref="Page"/> and <see cref="PageSize"/> are what the server actually served, which may
/// differ from what was asked for: the page size is the server's decision, so a client is told the
/// one it got rather than left to assume.</para>
///
/// <para><see cref="HasMore"/> rather than a total count, deliberately: a picker needs to know
/// whether to narrow the search, and how many products the shared catalogue holds for a term is a
/// fact about the catalogue that no business needs.</para>
/// </summary>
/// <param name="Products">The matches on this page, in the server's fixed order.</param>
/// <param name="Page">The page actually served, counting from one.</param>
/// <param name="PageSize">The page size actually served.</param>
/// <param name="HasMore">Whether matches remain after this page.</param>
public record MasterProductSuggestionPageResponse(
    IReadOnlyList<MasterProductSuggestionResponse> Products,
    int Page,
    int PageSize,
    bool HasMore);
