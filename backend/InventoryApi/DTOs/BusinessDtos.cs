namespace InventoryApi.DTOs;

/// <summary>
/// The signed-in operator's own business as <c>GET /api/business/current</c> returns it (issue
/// #499): what it is called, and the IANA time zone the frontend must render instants and resolve
/// calendar inputs in.
///
/// It deliberately carries no business identifier: the frontend has no use for one, and a tenant
/// key in a response is a key a later request could try to send back.
/// </summary>
public sealed record CurrentBusinessResponse(string Name, string TimeZoneId);
