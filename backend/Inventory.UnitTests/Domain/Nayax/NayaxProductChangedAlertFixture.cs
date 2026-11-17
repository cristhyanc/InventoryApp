namespace InventoryApi.Tests.Domain.Nayax;

/// <summary>
/// One real item of a Nayax <c>GET /v1/machines/{MachineID}/lastAlerts</c> response containing a
/// "Machine Product Changed" alert (issue #1325), captured by the repository owner on 17 November
/// 2026. The operator name is replaced with <c>[operator]</c> and every id is changed; everything
/// else - the field names, the event code, the description and the exact <c>EventData</c> text,
/// including the single quote that opens before <c>has replaced product</c> and is never closed -
/// is verbatim. The Nayax developer portal documents <c>lastAlerts</c> but lists no event codes and
/// no <c>EventData</c> format, so this capture is the only evidence of the 510 contract.
/// </summary>
internal static class NayaxProductChangedAlertFixture
{
    public const string EventData =
        "[operator] 'has replaced product V Energy Drink Sugarfree Original 250ML with product Redbull Red Edition | Product MDB: 58";

    public const string Json = """
        {
            "MachineID": 900000001,
            "EventDateTimeVMC": "2026-11-15T15:33:46.723",
            "TransactionID": null,
            "EventLogID": 900000000001,
            "SiteID": 1,
            "EntityTypeID": 140,
            "EntityTypeName": "Machine",
            "DeviceID": 900000002,
            "EntityActorID": 900000003,
            "EventDateTimeGMT": "2026-11-15T04:33:46.723",
            "EventCode": 510,
            "EventSourceID": 2,
            "EventSourceName": "Web - Application",
            "EventGroupId": 20,
            "EventGroupName": "Stock",
            "EventCategoryId": 59,
            "EventCategoryName": "Inventory",
            "EventDescription": "Machine Product Changed",
            "EventData": "[operator] 'has replaced product V Energy Drink Sugarfree Original 250ML with product Redbull Red Edition | Product MDB: 58",
            "JSONData": null,
            "EventUserID": null
        }
        """;
}
