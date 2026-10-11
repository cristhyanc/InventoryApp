namespace Inventory.Application.Access;

/// <summary>
/// One thing a member may do, as the agreed vocabulary of issue #521 (one slice of #501 under
/// #328) names it. This is the complete list: there is no "other", no wildcard and no capability
/// derived at runtime.
///
/// Declaration order is the order of the agreed table and is what
/// <c>GET /api/me/access</c> reports capabilities in, so the response is deterministic without
/// the client having to sort. The numeric values are an implementation detail - a capability is
/// never persisted and never crosses the wire as a number; <see cref="Capabilities.Name"/> owns
/// the one external spelling of each.
///
/// Issue #521 defines the vocabulary and reports it. <em>Enforcing</em> a capability on an
/// endpoint is issue #502's work and is deliberately not done here, so a member's access is
/// unchanged by this slice.
/// </summary>
public enum Capability
{
    /// <summary>See the dashboard, without its financial figures.</summary>
    DashboardView = 1,

    /// <summary>See the dashboard's financial figures: sales, profit and margin.</summary>
    DashboardViewFinancials = 2,

    /// <summary>See the pick list.</summary>
    PickListView = 3,

    /// <summary>
    /// Day-to-day inventory work: products, take inventory, categories, stock history, machines
    /// including Sync Restock, and sites.
    /// </summary>
    InventoryOperate = 4,

    /// <summary>Day-to-day purchasing work: purchases, supplier orders and suppliers.</summary>
    PurchasingOperate = 5,

    /// <summary>Configure a supplier's GST defaults.</summary>
    SuppliersManageGstDefaults = 6,

    /// <summary>Record and maintain operating expenses.</summary>
    ExpensesManage = 7,

    /// <summary>See the financial reports.</summary>
    ReportsView = 8,

    /// <summary>Run the sales, reimbursement and catalogue imports.</summary>
    ImportsRun = 9,

    /// <summary>Maintain site commission agreements and payments.</summary>
    SiteCommissionsManage = 10,

    /// <summary>Configure the business's Nayax integration.</summary>
    IntegrationManage = 11,

    /// <summary>Run the maintenance data repairs, including the sale-costing backfill.</summary>
    DataRepair = 12,

    /// <summary>Manage the business's members.</summary>
    MembersManage = 13,

    /// <summary>See the roles and what each one allows.</summary>
    RolesView = 14,

    /// <summary>Change the business's own record, including its time zone.</summary>
    BusinessManage = 15,
}
