using Inventory.Domain.FinancialConfiguration;

namespace Inventory.Application.Commissions;

public sealed record RecordSiteCommissionPaymentInput(
    long SiteId,
    DateTime PeriodStart,
    DateTime PeriodEnd,
    DateTime PaymentDate,
    decimal Amount,
    string? Notes);

public sealed record RecordSiteCommissionPaymentResult(CommissionPayment? Payment, string? Error);

public sealed class RecordSiteCommissionPayment
{
    private const decimal Tolerance = 0.01m;
    private readonly IGetSiteCommissionReport _getReport;
    private readonly ISiteCommissionStore _store;

    public RecordSiteCommissionPayment(
        IGetSiteCommissionReport getReport,
        ISiteCommissionStore store)
    {
        _getReport = getReport;
        _store = store;
    }

    public async Task<RecordSiteCommissionPaymentResult> Handle(
        RecordSiteCommissionPaymentInput input,
        CancellationToken cancellationToken)
    {
        var report = await _getReport.Handle(input.PeriodStart, input.PeriodEnd, input.SiteId, cancellationToken);
        var row = report.Rows.SingleOrDefault();
        if (row is null)
            return new(null, "The site has no current Nayax machine assignment.");
        if (row.Paid + input.Amount > row.CommissionDue + Tolerance)
            return new(null, "Payment exceeds commission due for this period.");

        var payment = new CommissionPayment(
            0,
            input.SiteId,
            input.PeriodStart.Date,
            input.PeriodEnd.Date,
            input.PaymentDate.Date,
            input.Amount,
            input.Notes?.Trim(),
            default,
            default);
        return new(await _store.AddPaymentAsync(payment, cancellationToken), null);
    }
}
