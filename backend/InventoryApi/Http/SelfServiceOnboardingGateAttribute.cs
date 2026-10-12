using Inventory.Application.Onboarding;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace InventoryApi.Http;

/// <summary>
/// Answers an endpoint of the self-service onboarding feature (issue #541) with the framework's
/// generic <c>404</c> when <see cref="ISelfServiceOnboardingFlag"/> is off, <em>before</em> MVC
/// binds the request body.
///
/// <para><strong>Why before binding.</strong> <c>[ApiController]</c> answers a body it cannot bind -
/// malformed JSON, an empty body, a field of the wrong type - with its own automatic <c>400</c>, and
/// that happens before the action runs. A flag checked only by the use case is therefore never
/// reached by such a request, and a disabled deployment would answer it with a <c>400</c> that
/// describes the endpoint's contract: exactly the "the feature is here, merely switched off" signal
/// the bare <c>404</c> exists to withhold. A resource filter runs after authorization and before
/// model binding, so this gate decides first for every body alike.</para>
///
/// <para><strong>It is not a second flag.</strong> It reads the same Application-layer port the
/// use case and <c>GET /api/me/account-state</c> read, so the three cannot disagree; the use case
/// keeps its own check as well, so no caller of it depends on this attribute being present.</para>
///
/// <para><strong>It removes nothing that runs before it.</strong> Authentication, the delegated
/// <c>access_as_user</c> scope and <c>BusinessScopeMiddleware</c> all decide before any MVC filter,
/// so a disabled endpoint still answers <c>401</c> and <c>403</c> exactly as an enabled one does.
/// The <c>404</c> it returns is the plain <see cref="NotFoundResult"/>, which <c>[ApiController]</c>
/// renders as the same detail-less problem body any unmatched route gets.</para>
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class SelfServiceOnboardingGateAttribute : Attribute, IResourceFilter
{
    public void OnResourceExecuting(ResourceExecutingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var onboarding = context.HttpContext.RequestServices.GetRequiredService<ISelfServiceOnboardingFlag>();

        if (!onboarding.IsEnabled)
        {
            context.Result = new NotFoundResult();
        }
    }

    public void OnResourceExecuted(ResourceExecutedContext context)
    {
    }
}
