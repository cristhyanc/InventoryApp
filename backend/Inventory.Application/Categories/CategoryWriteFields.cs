namespace Inventory.Application.Categories;

/// <summary>
/// A category's editable fields, as seen by the Application layer (issue #736). A category has
/// nothing else: its products point at it, and its provider id is written only by a provider import
/// or the catalogue review (issue #738), never through this use case.
/// </summary>
public sealed record CategoryWriteFields(string? Name, string? Description);
