using System.Diagnostics;
using Inventory.Application.Subscriptions;
using Inventory.Domain.Subscriptions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace InventoryApi.Auth;

/// <summary>
/// The stable shape of a plan refusal (issue #967): <c>403</c> with the code
/// <see cref="ErrorCode"/> and the plan that would include the feature.
///
/// <para><strong>Why <c>403</c>.</strong> A plan refusal really is "you may not do this": the
/// business has not bought the feature, and no retry, reconnection or role change makes the request
/// succeed. That is deliberately different from the two Nayax <em>connection</em> failures, which
/// stay <c>409 Conflict</c> because they conflict with the business's own configuration and a
/// signed-in member is perfectly entitled to the feature once it is reconnected
/// (<c>NayaxConnectionExceptionHandler</c> explains that choice).</para>
///
/// <para><strong>What it says, and what it does not.</strong> The required plan is what the business
/// would have to be on, which is its own commercial fact rather than anybody's data, and the feature
/// is published so a client knows which lock to show (issue #979). It carries no role, no capability,
/// no business id, no price and nothing about which record decided the current plan - a refusal is
/// not the place to publish an entitlement, which <c>GET /api/me/access</c> already reports to the
/// caller's own business.</para>
/// </summary>
public static class PlanRequirement
{
    /// <summary>
    /// The stable code a client branches on. It is the same code wherever a plan refuses something -
    /// a report, an export, Site Commissions, the sales file import and the Owner's Nayax credential
    /// actions - so a client learns one word for one answer.
    /// </summary>
    public const string ErrorCode = "plan_required";

    /// <summary>The title every plan refusal carries.</summary>
    public const string Title = "This business's plan does not include this feature";

    /// <summary>
    /// The caller-safe sentence a plan refusal carries. It names the plan that includes the feature,
    /// because that is the only thing a member can act on.
    /// </summary>
    public static string Message(SubscriptionPlan requiredPlan) =>
        $"This business's plan does not include this feature. The {requiredPlan} plan includes it.";

    /// <summary>
    /// The refusal as an action result, so the endpoint filter below and any later refusal produce
    /// exactly one shape.
    /// </summary>
    /// <param name="feature">The feature that was refused.</param>
    /// <param name="httpContext">The request being refused, for its trace id.</param>
    public static ObjectResult Refusal(PlanFeature feature, HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        var requiredPlan = PlanFeaturePolicy.RequiredPlan(feature);
        var detail = Message(requiredPlan);

        var problemDetails = new ProblemDetails
        {
            Type = "https://tools.ietf.org/html/rfc9110#section-15.5.4",
            Title = Title,
            Detail = detail,
            Status = StatusCodes.Status403Forbidden,
        };
        problemDetails.Extensions["traceId"] = Activity.Current?.Id ?? httpContext.TraceIdentifier;
        // Kept beside the standard `detail` for the reason every other handled failure keeps it: the
        // existing frontend error handling reads `error.message`.
        problemDetails.Extensions["message"] = detail;
        problemDetails.Extensions["code"] = ErrorCode;
        problemDetails.Extensions["requiredPlan"] = requiredPlan.ToString();
        problemDetails.Extensions["planFeature"] = PlanFeatures.Name(feature);

        return new ObjectResult(problemDetails)
        {
            StatusCode = StatusCodes.Status403Forbidden,
            ContentTypes = { "application/problem+json" },
        };
    }
}

/// <summary>
/// Declares the plan feature an endpoint needs: <c>[RequirePlan(PlanFeature.SiteCommissions)]</c>
/// (issue #967).
///
/// <para><strong>It names a feature, never a plan.</strong> Which plan a feature needs is
/// <see cref="PlanFeaturePolicy"/>'s single answer, so a controller cannot come to disagree with the
/// catalogue, with another endpoint of the same feature, or with the report export that serves the
/// same figures in a file.</para>
///
/// <para><strong>It is an MVC authorization filter, not an authorization policy, and that is the
/// point.</strong> The criterion is that a plan is evaluated <em>after</em> the role requirement: a
/// second <see cref="Microsoft.AspNetCore.Authorization.AuthorizeAttribute"/> would be evaluated
/// together with the capability policy by one middleware, in no defined order, and a caller who holds
/// neither could be told about the plan instead of simply being refused. An MVC filter runs after the
/// authorization middleware has already finished, so a role refusal stays exactly the bare <c>403</c>
/// it is today and a plan is only ever asked about for a caller who was allowed through. It also runs
/// before model binding and before the action, so nothing is read, written or sent upstream for a
/// refused request - which is what makes "a refused save leaves stored credentials unchanged" a
/// property of the pipeline rather than of the use case.</para>
///
/// <para>Requirements accumulate down the endpoint exactly as capability requirements do, so a
/// controller-level feature plus an action-level one means that action needs both.</para>
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class RequirePlanAttribute : Attribute, IFilterFactory
{
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="feature"/> is not a declared <see cref="PlanFeature"/>. The attribute is
    /// constructed while MVC discovers the endpoint, so a controller naming one fails startup rather
    /// than quietly requiring a feature nothing can satisfy.
    /// </exception>
    public RequirePlanAttribute(PlanFeature feature)
    {
        if (!PlanFeatures.IsDeclared(feature))
        {
            throw new ArgumentOutOfRangeException(
                nameof(feature), feature, "Only a declared plan feature can be required.");
        }

        Feature = feature;
    }

    /// <summary>The feature this endpoint requires, for the endpoint plan-requirement inventory test.</summary>
    public PlanFeature Feature { get; }

    /// <inheritdoc />
    /// <remarks>
    /// Not reusable: the filter holds the request's own scoped <see cref="CurrentPlan"/>, which is
    /// what keeps the plan read on the request's persistence scope and memoised per request.
    /// </remarks>
    public bool IsReusable => false;

    /// <inheritdoc />
    public IFilterMetadata CreateInstance(IServiceProvider serviceProvider) =>
        ActivatorUtilities.CreateInstance<PlanRequirementFilter>(serviceProvider, Feature);
}

/// <summary>
/// Declares that an endpoint's required feature depends on which report it was asked for (issue
/// #967). It exists for exactly one endpoint, <c>GET /api/reports/{report}/export</c>, which serves
/// every report family through one route, and it resolves the feature through
/// <see cref="ReportPlanRequirements"/> - the same table the report actions' own declared features
/// are asserted against, so a report and its export cannot need different plans.
///
/// <para>A report name this application does not recognise resolves to the most restricted feature,
/// because the export's row builder falls through to product profitability for one.</para>
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class RequirePlanPerReportAttribute : Attribute, IFilterFactory
{
    /// <summary>The export route's parameter the report name is read from.</summary>
    public const string ReportRouteValueName = "report";

    /// <inheritdoc />
    public bool IsReusable => false;

    /// <inheritdoc />
    public IFilterMetadata CreateInstance(IServiceProvider serviceProvider) =>
        ActivatorUtilities.CreateInstance<ReportPlanRequirementFilter>(serviceProvider);
}

/// <summary>
/// Refuses a request whose business's plan does not include the declared feature (issue #967).
/// </summary>
internal sealed class PlanRequirementFilter : IAsyncAuthorizationFilter
{
    private readonly PlanFeature _feature;
    private readonly CurrentPlan _plan;
    private readonly ILogger<PlanRequirementFilter> _logger;

    public PlanRequirementFilter(
        PlanFeature feature,
        CurrentPlan plan,
        ILogger<PlanRequirementFilter> logger)
    {
        _feature = feature;
        _plan = plan;
        _logger = logger;
    }

    public Task OnAuthorizationAsync(AuthorizationFilterContext context) =>
        PlanRequirementEnforcement.ApplyAsync(context, _feature, _plan, _logger);
}

/// <summary>
/// The export's variant: the same refusal, with the feature taken from the report the route named
/// (issue #967).
/// </summary>
internal sealed class ReportPlanRequirementFilter : IAsyncAuthorizationFilter
{
    private readonly CurrentPlan _plan;
    private readonly ILogger<ReportPlanRequirementFilter> _logger;

    public ReportPlanRequirementFilter(CurrentPlan plan, ILogger<ReportPlanRequirementFilter> logger)
    {
        _plan = plan;
        _logger = logger;
    }

    public Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var report = context.RouteData
            .Values[RequirePlanPerReportAttribute.ReportRouteValueName]?
            .ToString();

        return PlanRequirementEnforcement.ApplyAsync(
            context, ReportPlanRequirements.FeatureOf(report), _plan, _logger);
    }
}

/// <summary>
/// The one enforcement step both filters above perform, so there is exactly one place a plan refuses
/// an endpoint.
/// </summary>
internal static class PlanRequirementEnforcement
{
    internal static async Task ApplyAsync(
        AuthorizationFilterContext context,
        PlanFeature feature,
        CurrentPlan plan,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (await plan.AllowsAsync(feature, context.HttpContext.RequestAborted).ConfigureAwait(false))
        {
            return;
        }

        // Which feature was refused and what it needs is worth logging; who the caller was is not,
        // and belongs with the request's own correlation id. The same division the capability
        // handler's refusal log makes.
        logger.LogWarning(
            "Plan feature {PlanFeature} denied: the business's plan does not include it. "
                + "Required plan: {RequiredPlan}.",
            PlanFeatures.Name(feature),
            PlanFeaturePolicy.RequiredPlan(feature));

        context.Result = PlanRequirement.Refusal(feature, context.HttpContext);
    }
}
