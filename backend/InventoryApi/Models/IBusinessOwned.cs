using System.Text.Json.Serialization;

namespace InventoryApi.Models;

/// <summary>
/// Marks a persisted entity as owned by exactly one <see cref="Business"/> (issue #64).
///
/// Implementing this is the whole opt-in: <c>AppDbContext</c> discovers every implementation in
/// the model and gives it a business key, an index, a global query filter, and SaveChanges
/// enforcement, so ownership cannot be forgotten for one entity or applied inconsistently
/// through scattered controller filters.
///
/// <see cref="BusinessId"/> is never accepted from route, query, form, or JSON input. It is
/// stamped from the authenticated caller's resolved business on insert and is immutable
/// afterwards, so a record cannot be moved between businesses by an API payload.
/// </summary>
public interface IBusinessOwned
{
    [JsonIgnore]
    int BusinessId { get; set; }
}
