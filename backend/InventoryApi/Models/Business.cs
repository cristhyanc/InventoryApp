using System.Text.Json.Serialization;

namespace InventoryApi.Models;

/// <summary>
/// An application-owned business: the data-ownership boundary for this vending operation
/// (issue #64). Its <see cref="Id"/> is the tenant key the rest of the application scopes by.
///
/// It is not a Microsoft Entra directory tenant. Actors from an Entra directory are mapped onto
/// a business through explicit <see cref="BusinessMembership"/> rows, so the identity provider
/// never decides data ownership on its own.
/// </summary>
public class Business
{
    public int Id { get; set; }

    /// <summary>Operator-facing name of the business. Not an identifier; never used for lookup.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Deactivating a business denies access to its data without deleting the records, so an
    /// offboarded business fails closed rather than being dropped.
    /// </summary>
    public bool IsActive { get; set; } = true;

    public DateTime CreatedAtUtc { get; set; }

    [JsonIgnore]
    public virtual ICollection<BusinessMembership> Memberships { get; set; } = new List<BusinessMembership>();
}
