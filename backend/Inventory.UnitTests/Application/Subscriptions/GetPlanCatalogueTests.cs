using Inventory.Application.Subscriptions;
using Inventory.Domain.Subscriptions;
using Xunit;

namespace InventoryApi.Tests.Application.Subscriptions;

/// <summary>
/// The Application read behind <c>GET /api/me/plan-catalogue</c> (issue #970).
///
/// <para>What each plan costs and includes is <c>PlanCatalogue</c>'s own fact and is pinned there.
/// What only this file can show is the property the HTTP boundary depends on: the use case hands
/// back that one definition unchanged and in its own order, so no second copy of a price, a storage
/// allowance or a machine allowance can appear between the catalogue and the page.</para>
/// </summary>
public class GetPlanCatalogueTests
{
    [Fact]
    public void Returns_the_one_catalogue_definition_unchanged_and_in_its_own_order()
    {
        var catalogue = new GetPlanCatalogue().Handle();

        Assert.Equal(PlanCatalogue.All, catalogue);
    }

    /// <summary>
    /// It takes no identifier and reads nothing, so two calls cannot differ: the catalogue is the
    /// same answer for every caller and every request, which is why the controller may map it
    /// without asking whose plan it is.
    /// </summary>
    [Fact]
    public void Answers_identically_however_often_it_is_asked()
    {
        var useCase = new GetPlanCatalogue();

        Assert.Equal(useCase.Handle(), useCase.Handle());
    }
}
