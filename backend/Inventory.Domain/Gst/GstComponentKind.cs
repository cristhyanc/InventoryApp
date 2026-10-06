namespace Inventory.Domain.Gst;

/// <summary>
/// Which of a purchase's three kinds of GST component this is (parent issue #62, "Fee types").
///
/// The kind is what decides which rule may classify a component at all, so it is a named vocabulary
/// rather than a flag: a product line reads the product's rule and then the supplier's product-line
/// default, while a delivery or package charge reads only the supplier's matching fee default and
/// never a product rule or the product-line default
/// (<see cref="HistoricalGstClassificationPolicy"/>, issue #433).
/// </summary>
public enum GstComponentKind
{
    /// <summary>One purchased product line.</summary>
    ProductLine = 0,

    /// <summary>The purchase's delivery charge.</summary>
    DeliveryCharge = 1,

    /// <summary>The purchase's package charge.</summary>
    PackageCharge = 2,
}
