namespace Inventory.Application.Reporting.Transactions;

/// <summary>Filters the transaction sales report. The service restricts page sizes to 50, 100, or 250.</summary>
public record TransactionSalesFilterDto(
    DateTime? From = null,
    DateTime? To = null,
    long? MachineId = null,
    long? SiteId = null,
    long? ProductId = null,
    string? PaymentType = null,
    string? Status = "completed",
    string? CogsStatus = null,
    string? Search = null,
    int Page = 1,
    int PageSize = 50,
    string? SortBy = "date",
    bool SortDescending = true);
