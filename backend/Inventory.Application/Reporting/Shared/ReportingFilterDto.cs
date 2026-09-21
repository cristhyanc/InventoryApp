namespace Inventory.Application.Reporting.Shared;

/// <summary>Common inclusive date and machine filters used by reporting endpoints.</summary>
public record ReportingFilterDto(
    DateTime? From = null,
    DateTime? To = null,
    long? MachineId = null,
    string? FinancialYear = null)
{
    public DateTime? StartDate { get; init; } = From;
    public DateTime? EndDate { get; init; } = To;
    public long? MachineID { get; init; } = MachineId;
}

public record ReportFilterDto(
    DateTime? From = null,
    DateTime? To = null,
    long? MachineId = null,
    string? FinancialYear = null) : ReportingFilterDto(From, To, MachineId, FinancialYear);
