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
        public decimal? TodayNetRevenue { get; set; }
        public decimal? TwoWeeksAgoNetRevenue { get; set; }
        public decimal? LastWeekNetRevenue { get; set; }
        public decimal? CurrentWeekNetRevenue { get; set; }
        public decimal PreviousComparableWeekGrossRevenue { get; set; } = 0;
        public decimal? PreviousComparableWeekNetRevenue { get; set; }
        public decimal MonthToDateGrossRevenue { get; set; } = 0;
        public decimal? MonthToDateNetRevenue { get; set; }
        public string? ProfitabilityStatus { get; set; }
    }
}
