using Inventory.Application.Costing;
using Inventory.Infrastructure.Models;
using Xunit;

namespace InventoryApi.Tests.Application.Costing;

/// <summary>
/// The Application <see cref="SaleCostStatus"/>/<see cref="SaleCostOrigin"/> convert to and from the
/// persisted <see cref="SaleCostingStatus"/>/<see cref="SaleCostSource"/> by a plain cast in the
/// temporary API-owned sale-costing adapter (issue #297), so they must stay identical
/// member-for-member and ordinal-for-ordinal; otherwise a cost's status or provenance would be
/// silently mislabelled.
/// </summary>
public class SaleCostEnumParityTests
{
    [Fact]
    public void Costing_status_enums_have_the_same_members_with_the_same_ordinals() =>
        Assert.Equal(Members<SaleCostingStatus>(), Members<SaleCostStatus>());

    [Fact]
    public void Cost_source_enums_have_the_same_members_with_the_same_ordinals() =>
        Assert.Equal(Members<SaleCostSource>(), Members<SaleCostOrigin>());

    private static List<(string Name, int Ordinal)> Members<T>() where T : struct, Enum =>
        Enum.GetValues<T>().Select(value => (value.ToString(), Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture))).ToList();
}
