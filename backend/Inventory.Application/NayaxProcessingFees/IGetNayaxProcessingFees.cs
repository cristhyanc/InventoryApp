using Inventory.Application.Reporting.Shared;

namespace Inventory.Application.NayaxProcessingFees;

public interface IGetNayaxProcessingFees
{
    Task<NayaxProcessingFeeResult> Handle(
        DateTime fromDate,
        DateTime toDate,
        long? machineId,
        CancellationToken cancellationToken);
}
