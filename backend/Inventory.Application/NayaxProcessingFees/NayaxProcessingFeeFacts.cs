using Inventory.Domain.FinancialConfiguration;

namespace Inventory.Application.NayaxProcessingFees;

public sealed record NayaxProcessingFeeFacts(
    IReadOnlyList<ProcessingFeeReimbursement> Reimbursements,
    IReadOnlyList<CompletedCardTransaction> CompletedSales);

public interface INayaxProcessingFeeFactsProvider
{
    Task<NayaxProcessingFeeFacts> GetFactsAsync(
        DateTime fromDate,
        DateTime toDate,
        long? machineId,
        CancellationToken cancellationToken);
}
