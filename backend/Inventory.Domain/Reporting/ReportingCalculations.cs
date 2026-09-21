namespace Inventory.Domain.Reporting;

public static class ReportingCalculations
{
    public static decimal GrossProfit(decimal sales, decimal costOfGoods) => sales - costOfGoods;
    public static decimal MarginPercent(decimal sales, decimal costOfGoods) =>
        sales == 0m ? 0m : GrossProfit(sales, costOfGoods) / sales * 100m;
    public static decimal PercentageOf(decimal amount, decimal denominator) =>
        denominator == 0m ? 0m : amount / denominator * 100m;
    public static decimal GstFromInclusive(decimal amount) => amount * 10m / 110m;
    public static decimal GstFromExcluding(decimal amount) => amount * 10m / 100m;
    public static decimal Average(decimal amount, int count) => count == 0 ? 0m : amount / count;
}
