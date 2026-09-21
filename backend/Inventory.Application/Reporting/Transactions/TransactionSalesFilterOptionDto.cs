namespace Inventory.Application.Reporting.Transactions;

public record TransactionSalesFilterOptionDto(long? Id, string Name);

public record TransactionSalesFilterOptionsDto(
    IReadOnlyList<TransactionSalesFilterOptionDto> Sites,
    IReadOnlyList<TransactionSalesFilterOptionDto> Products);
