namespace InventoryApi.Models
{
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
