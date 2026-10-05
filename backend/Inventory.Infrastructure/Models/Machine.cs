namespace Inventory.Infrastructure.Models
{
    /// <summary>
    /// The retired machine response shape. A machine has never been persisted - it is a live Nayax
    /// view with this business's own sales and costs resolved onto it - and since issue #302 no
    /// production code names this type: <c>MachinesController</c> serialises the API-owned
    /// <c>InventoryApi.DTOs.MachineResponse</c> instead. It is kept here, unreferenced by the
    /// application, as the reference value
    /// <c>InventoryApi.Tests.DTOs.MachineJsonContractTests</c> compares that response's serialised
    /// bytes against, so the wire contract is proven against the shape clients actually received
    /// rather than against another copy of the new response. Deleting it belongs to issue #153's
    /// legacy-structure removal, not to #302, which may not remove anything its acceptance criteria
    /// do not name. Do not give it new callers.
    /// </summary>
    public class Machine
    {
        public long MachineID { get; set; }
        public string? MachineName { get; set; }
        public string? MachineNumber { get; set; }
        public long? ActorID { get; set; }
        public decimal TodayGrossRevenue { get; set; } = 0;
        public decimal CurrentWeekGrossRevenue { get; set; } = 0;
        public decimal LastWeekGrossRevenue { get; set; } = 0;
        public decimal TwoWeeksAgoGrossRevenue { get; set; } = 0;
        public decimal? TodayDirectProfit { get; set; }
        public decimal? TwoWeeksAgoDirectProfit { get; set; }
        public decimal? LastWeekDirectProfit { get; set; }
        public decimal? CurrentWeekDirectProfit { get; set; }
        public decimal PreviousComparableWeekGrossRevenue { get; set; } = 0;
        public decimal? PreviousComparableWeekDirectProfit { get; set; }
        public decimal MonthToDateGrossRevenue { get; set; } = 0;
        public decimal? MonthToDateDirectProfit { get; set; }
        public string? ProfitabilityStatus { get; set; }
    }
}
