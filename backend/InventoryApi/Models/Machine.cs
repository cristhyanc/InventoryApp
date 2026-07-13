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
        public decimal TodayNetRevenue { get; set; } = 0;
        public decimal TwoWeeksAgoNetRevenue { get; set; } = 0;
        public decimal LastWeekNetRevenue { get; set; } = 0;
        public decimal CurrentWeekNetRevenue { get; set; } = 0;
    }
}
