using Inventory.Application.Time;
using Inventory.Domain.Purchases;

namespace Inventory.Application.Purchases;

/// <summary>
/// A purchase already recorded for the same supplier, with the same invoice number, on the same
/// business day (issue #1247).
/// </summary>
public sealed record PurchaseDuplicateMatch(int PurchaseId, string Title, DateTime PurchaseDate);

/// <summary>
/// Answers "does this look like a purchase that is already recorded?" for the Add Purchase form,
/// before it saves (issue #1247).
///
/// <para><strong>It is a warning and changes nothing.</strong> Nothing is written, nothing is
/// refused and the answer grants no permission: the operator saves anyway or does not, and the
/// create endpoint is unaffected by what this says. Blocking a duplicate outright is explicitly out
/// of the issue's scope; the only thing that refuses a second save is the idempotency key, which is
/// about one submission arriving twice rather than about two submissions describing one receipt.
/// </para>
///
/// <para><strong>The day is the business's own.</strong> A purchase date is stored as an instant, so
/// "the same date" is only meaningful in a calendar - and the calendar is the current business's
/// (<see cref="IBusinessCalendar"/>), never the API host's local time, exactly as every other
/// date-shaped decision in this application. The submitted instant is converted to that business
/// date and the lookup runs over that day's half-open UTC range, so an operator entering a receipt
/// at 9am and again at 11pm is asked the same question both times.</para>
///
/// <para><strong>It answers nothing at all unless there is something to recognise a repeat
/// by</strong> - a supplier and a real invoice number
/// (<see cref="PurchaseDuplicateWarningPolicy"/>) - and then reads only the caller's own purchases,
/// through the ordinary tenant query filters.</para>
/// </summary>
public sealed class FindPossibleDuplicatePurchase
{
    private readonly IPurchaseStore _store;
    private readonly IBusinessCalendar _calendar;

    public FindPossibleDuplicatePurchase(IPurchaseStore store, IBusinessCalendar calendar)
    {
        _store = store;
        _calendar = calendar;
    }

    public async Task<PurchaseDuplicateMatch?> Handle(
        int? supplierId,
        string? invoiceNumber,
        DateTime? purchaseDate,
        CancellationToken cancellationToken)
    {
        if (purchaseDate is not { } instant) return null;
        if (PurchaseDuplicateWarningPolicy.MatchableInvoiceNumber(supplierId, invoiceNumber) is not { } number)
            return null;

        var businessDate = _calendar.ToBusinessDate(instant);
        return await _store.FindPossibleDuplicateAsync(
            supplierId!.Value,
            number,
            _calendar.StartOfBusinessDayUtc(businessDate),
            _calendar.StartOfBusinessDayUtc(businessDate.AddDays(1)),
            cancellationToken);
    }
}
