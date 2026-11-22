namespace Inventory.Domain.Legal;

/// <summary>
/// Which act of agreement an acceptance record describes (issue #1259): the two moments the
/// published Terms of Use say somebody agrees to them - "when registering or joining a business".
///
/// <para>The values are spaced like <c>BusinessStatus</c>'s and <c>BusinessRole</c>'s, so a further
/// context can be declared between two of them without renumbering a persisted value, and there is
/// deliberately no <c>0</c>: <c>0</c> is what an unfilled integer column and a default-constructed
/// enum both hold, and an acceptance record whose context defaulted would claim an agreement nobody
/// placed.</para>
/// </summary>
public enum LegalAcceptanceContext
{
    /// <summary>The person agreed while submitting their own business application.</summary>
    BusinessApplication = 10,

    /// <summary>The person agreed while redeeming an invite link to join a business.</summary>
    InviteRedemption = 20,
}
