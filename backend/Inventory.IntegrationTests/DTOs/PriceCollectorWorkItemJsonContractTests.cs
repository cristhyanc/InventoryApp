using System.Text.Json;
using Inventory.Application.PriceCatalogue;
using InventoryApi.DTOs;
using Xunit;

namespace InventoryApi.Tests.DTOs;

/// <summary>
/// The JSON a collector reads for one work item (issue #1138): the work-item id and supplier it
/// always had, plus <c>searchTerms</c> - the one effective entry's phrases in search order and its
/// flavour aliases, or <c>null</c> when nothing is configured, which is the existing search.
/// </summary>
public class PriceCollectorWorkItemJsonContractTests
{
    private static readonly JsonSerializerOptions WebDefaults = new(JsonSerializerDefaults.Web);

    [Fact]
    public void A_work_item_without_configuration_publishes_null_search_terms() =>
        Assert.Equal(
            """{"workItemId":7001,"supplierPresetId":3,"searchTerms":null}""",
            JsonSerializer.Serialize(
                PriceCollectorWorkItemResponse.From(new PriceCollectorWorkItemHandle(7001, 3)),
                WebDefaults));

    [Fact]
    public void A_configured_work_item_publishes_its_phrases_in_order_and_its_aliases() =>
        Assert.Equal(
            """{"workItemId":7001,"supplierPresetId":7,"searchTerms":{"searchPhrases":["Smiths Plain","Smiths"],"flavourAliases":["Plain"]}}""",
            JsonSerializer.Serialize(
                PriceCollectorWorkItemResponse.From(new PriceCollectorWorkItemHandle(
                    7001,
                    7,
                    new PriceCollectorSearchTerms(["Smiths Plain", "Smiths"], ["Plain"]))),
                WebDefaults));
}
