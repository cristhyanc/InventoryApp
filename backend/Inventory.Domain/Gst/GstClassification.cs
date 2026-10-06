namespace Inventory.Domain.Gst;

/// <summary>
/// The GST status of one purchase component - a purchase line, or a purchase-level delivery or
/// package charge (parent issue #62's approved GST design).
///
/// <see cref="Unknown"/> is a real, persisted state and the default for everything the app has not
/// been told about: it contributes no input GST and stays visibly unresolved for bookkeeping
/// review. It must never be collapsed into <see cref="GstFree"/>, and GST must never be inferred
/// from an amount alone (AGENTS.md § Nayax processing fees and GST).
///
/// Spelled <c>GstFree</c> rather than <c>GSTFree</c> to match the repository's existing GST naming
/// (<c>GstFromInclusive</c>, <c>GstAccountingAidPolicy</c>) and the .NET acronym casing rules the
/// build enforces.
/// </summary>
public enum GstClassification
{
    /// <summary>Not classified. Contributes no input GST and is reported as unresolved.</summary>
    Unknown = 0,

    /// <summary>A GST-inclusive taxable component: its GST is the amount divided by 11.</summary>
    Taxable = 1,

    /// <summary>An explicitly GST-free component: it contributes $0 input GST.</summary>
    GstFree = 2,
}
