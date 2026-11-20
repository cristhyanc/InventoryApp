namespace Inventory.Domain.Providers;

/// <summary>
/// The rules of the Dashboard's "Nayax products are selling but aren't in your products" alert
/// (issue #1352): how far back a sale is evidence that a provider product is missing locally, and
/// how many of the missing products the alert names.
///
/// <para><strong>Why there is a window at all.</strong> The alert exists to be acted on - the
/// operator opens Import products and links or creates the rows - so it has to go quiet by itself
/// for a provider product the business has decided not to stock. A one-off sale from a year ago is
/// not work waiting to be done, and an alert that counted it would nag for ever and be dismissed
/// rather than read. Thirty calendar dates including today is the window the repository owner chose
/// on issue #1352; it is a count of <em>business calendar dates</em> rather than of hours, so the
/// window's first day is a date and not an instant 720 hours ago, and a daylight-saving change
/// inside it cannot move that first day.</para>
///
/// <para>It holds no clock and no database: the business date it is given comes from
/// <c>IBusinessCalendar</c> in the Application layer, which is what turns the returned date back
/// into the UTC instant the stored sales are compared against.</para>
/// </summary>
public static class UnimportedProviderProductAttentionPolicy
{
    /// <summary>
    /// How many business calendar dates the alert looks back over, today included. Thirty dates
    /// means the earliest counted sale is on <c>today - 29</c>.
    /// </summary>
    public const int WindowDays = 30;

    /// <summary>
    /// How many of the missing provider products the alert names. The rest are "and N more": the
    /// alert is a prompt to open Import products, which lists every one of them.
    /// </summary>
    public const int MaxExamples = 3;

    /// <summary>
    /// The first business calendar date whose sales the alert counts: <see cref="WindowDays"/> dates
    /// before <paramref name="businessToday"/> and including it, so exactly thirty dates are counted
    /// and the boundary is a date comparison rather than a subtraction of hours.
    /// </summary>
    public static DateTime FirstBusinessDate(DateTime businessToday) =>
        businessToday.Date.AddDays(-(WindowDays - 1));
}
