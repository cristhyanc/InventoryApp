namespace Inventory.Domain.ReceiptScanning;

/// <summary>
/// Which of a receipt's printed dates is the purchase date (issue #1163, decided 10 November 2026).
///
/// <para><strong>The delivery date when one is printed, otherwise the receipt's own transaction or
/// invoice date.</strong> A purchase records stock arriving, and the inventory movement it creates is
/// dated by this value, so a delivery that arrived two days after the invoice was raised must not be
/// recorded on the invoice's day.</para>
///
/// <para><strong>An order date is never the purchase date</strong>, and that is why this policy takes
/// one at all: a receipt for an online order prints all three, and the only way "the order date is
/// not a candidate" can be a rule rather than an accident of which field a reader happened to fill in
/// is for the rule to be given the order date and to ignore it.</para>
/// </summary>
public static class ReceiptPurchaseDatePolicy
{
    /// <summary>
    /// The purchase date the receipt's printed dates imply, or <see langword="null"/> when it printed
    /// no usable date - in which case the operator dates the purchase, rather than this application
    /// dating it today.
    /// </summary>
    /// <param name="deliveryDate">The date the goods were delivered, supplied or picked up.</param>
    /// <param name="receiptDate">
    /// The receipt's own date: the transaction date on a docket, the invoice date on a tax invoice.
    /// </param>
    /// <param name="orderDate">
    /// The date the order was placed, which is deliberately never the answer.
    /// </param>
    public static DateOnly? Choose(DateOnly? deliveryDate, DateOnly? receiptDate, DateOnly? orderDate)
    {
        _ = orderDate;

        return deliveryDate ?? receiptDate;
    }
}
