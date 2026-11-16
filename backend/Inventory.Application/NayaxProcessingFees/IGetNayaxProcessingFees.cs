using Inventory.Application.Reporting.Shared;

namespace Inventory.Application.NayaxProcessingFees;

public interface IGetNayaxProcessingFees
{
    /// <summary>
    /// Fees for an inclusive range of calendar dates, with each sale bucketed by the UTC date of its
    /// instant. <b>No report asks for this any more</b>: issue #1252 moved the last two that did
    /// (machine profitability, through this overload, and Transaction Sales, through its own per-row
    /// rate lookup) onto <see cref="HandleBusinessPeriod"/>, because a sale in the first hours of a
    /// business day was being charged the previous day's rate. It remains as the direct expression of
    /// the date-range fee contract this engine implements; a money report must not use it.
    /// </summary>
    Task<NayaxProcessingFeeResult> Handle(
        DateTime fromDate,
        DateTime toDate,
        long? machineId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Fees for a period bounded by business days: the fees charged belong to exactly the sales the
    /// period's revenue counts, and each one is estimated at the rate effective on the business date
    /// that counted it (issue #310 for the machine dashboard, issue #1250 and issue #1252 for every
    /// money report).
    /// </summary>
    Task<NayaxProcessingFeeResult> HandleBusinessPeriod(
        NayaxProcessingFeeBusinessPeriod period,
        long? machineId,
        CancellationToken cancellationToken);
}
