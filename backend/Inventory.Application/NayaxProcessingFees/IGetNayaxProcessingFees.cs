using Inventory.Application.Reporting.Shared;

namespace Inventory.Application.NayaxProcessingFees;

public interface IGetNayaxProcessingFees
{
    /// <summary>
    /// Fees for an inclusive range of calendar dates, the form every report asks for.
    /// </summary>
    Task<NayaxProcessingFeeResult> Handle(
        DateTime fromDate,
        DateTime toDate,
        long? machineId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Fees for a period bounded by business days, the form the machine
    /// dashboard asks for: the fees charged belong to exactly the sales the period's revenue counts
    /// (issue #310).
    /// </summary>
    Task<NayaxProcessingFeeResult> HandleBusinessPeriod(
        NayaxProcessingFeeBusinessPeriod period,
        long? machineId,
        CancellationToken cancellationToken);
}
