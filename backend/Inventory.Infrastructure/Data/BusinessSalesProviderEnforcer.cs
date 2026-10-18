using Inventory.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Infrastructure.Data;

/// <summary>
/// Refuses to change a <see cref="Business.SalesProvider"/> that is already set (issue #759, parent
/// #329 decision P1 as revised on 18 October 2026).
///
/// It is enforced centrally on <c>SaveChanges</c>, beside <see cref="BusinessStatusEnforcer"/> and
/// the other business enforcers, and for a reason of the same kind: the sales provider is the issuer
/// of every external id the business's products and categories hold, so changing it would silently
/// reinterpret stored identifiers as another system's - a Nayax product id read as a Cantaloupe one
/// is not a mistake any report would show. Switching provider is a separate design (#761) that has
/// to decide what happens to those ids, the sales already imported and the recorded provider
/// coverage; until it exists, the column is write-once.
///
/// <para>Claiming an unset provider is not a change and is allowed: that is what storing the first
/// provider's credentials does, through the conditional <c>UPDATE ... WHERE SalesProvider IS NULL</c>
/// in <see cref="Persistence.EfNayaxConnectionStore"/>. Clearing a set provider back to <c>null</c>
/// is refused too - "no provider" after ids have been issued is not a state this application can
/// represent honestly.</para>
///
/// <para>The refusal is an <see cref="InvalidOperationException"/> rather than a
/// <c>DomainValidationException</c>, for the same reason the status enforcer's is: nothing a caller
/// submits can reach here, because no route, query, body or header names a sales provider and no
/// endpoint writes one. Reaching it is a programming error, so the HTTP boundary answers it with its
/// generic <c>500</c>. The message names only the two providers, which are not anybody's data.</para>
/// </summary>
internal static class BusinessSalesProviderEnforcer
{
    public static void Enforce(AppDbContext context)
    {
        foreach (var entry in context.ChangeTracker.Entries<Business>())
        {
            if (entry.State is not EntityState.Modified)
            {
                // An inserted business may state its provider: a business created with one is not a
                // business whose provider changed. Nothing in the application does that today.
                continue;
            }

            var property = entry.Property(business => business.SalesProvider);
            if (property.OriginalValue is not { } original || original == property.CurrentValue)
            {
                continue;
            }

            throw new InvalidOperationException(
                $"A business's sales provider cannot be changed once it is set: this business's is "
                    + $"{original} and the save would make it "
                    + $"{property.CurrentValue?.ToString() ?? "no provider"}. Every external id its "
                    + "products and categories hold was issued by the stored provider, so changing "
                    + "it is a separate, reviewed migration (#761). Nothing was saved.");
        }
    }
}
