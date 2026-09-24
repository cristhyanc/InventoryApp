using System.Globalization;

namespace Inventory.Domain.Tenancy;

/// <summary>
/// The application-owned business (tenant) identifier.
///
/// This is deliberately <em>not</em> the Microsoft Entra directory tenant id: an Entra directory
/// is an identity boundary, a Business is the vending-business data-ownership boundary, and the
/// two are not the same thing (issue #64). A <see cref="BusinessId"/> only ever originates from
/// an application-owned membership record resolved from the authenticated actor - never from
/// route, query, form, or JSON input.
/// </summary>
public readonly record struct BusinessId
{
    private BusinessId(int value)
    {
        Value = value;
    }

    public int Value { get; }

    /// <summary>
    /// Wraps a persisted business key. Zero and negative values are not valid identifiers, so
    /// <c>default(BusinessId)</c> can never be mistaken for a real business.
    /// </summary>
    public static BusinessId From(int value) =>
        value > 0
            ? new BusinessId(value)
            : throw new ArgumentOutOfRangeException(nameof(value), value, "A business id must be a positive persisted key.");

    public static bool TryFrom(int value, out BusinessId businessId)
    {
        if (value <= 0)
        {
            businessId = default;
            return false;
        }

        businessId = new BusinessId(value);
        return true;
    }

    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);
}
