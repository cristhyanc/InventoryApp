using Inventory.Application.Access;

namespace InventoryApi.Adapters.Mapping;

/// <summary>
/// Whether the current request may be shown the business's financial figures (issue #698).
///
/// <para>Issue #527 established that the week's revenue and the value of the business's inventory at
/// cost are not sent to a role without <c>Dashboard.ViewFinancials</c>, and issue #681 closed the
/// endpoint that reported that valuation on its own. The machine and product endpoints carry the
/// same kind of figure - six periods of gross revenue, their direct profit, the profitability status
/// and each product's inventory value - while existing for restocking work an Operator does, so they
/// cannot be refused wholesale the way an endpoint with an <c>AuthorizeAttribute</c> can. The
/// response is projected without those figures instead, and this is the one thing that says
/// which.</para>
///
/// <para><see cref="Redacted"/> is the default value deliberately: the enum's zero is the shape that
/// withholds, so a projection reached without a decision having been made fails closed rather than
/// publishing a figure.</para>
/// </summary>
public enum FinancialFigureVisibility
{
    /// <summary>The caller may not be shown the figures; they are withheld as <c>null</c>.</summary>
    Redacted = 0,

    /// <summary>The caller holds <c>Dashboard.ViewFinancials</c> and is given the full response.</summary>
    Visible = 1,
}

/// <summary>
/// Resolves <see cref="FinancialFigureVisibility"/> for the current request.
/// </summary>
public static class FinancialFigureVisibilityResolution
{
    /// <summary>
    /// Asks the one capability service what the current request may do. It goes through
    /// <see cref="CurrentCapabilities"/> rather than naming a role, so what these endpoints withhold,
    /// what the Dashboard summary withholds, what the capability policies enforce and what
    /// <c>GET /api/me/access</c> reports cannot drift apart - and a support session is answered by
    /// its own fixed capability set (issue #551), which grants <c>Dashboard.ViewFinancials</c>, with
    /// no extra rule written here.
    /// </summary>
    /// <exception cref="Inventory.Application.Tenancy.BusinessAccessDeniedException">
    /// The caller has neither a usable membership nor a support session, so there is nothing to
    /// decide the response shape from. A request cannot reach this - the controllers' own
    /// <c>Inventory.Operate</c> policy refuses such a caller first - and it fails closed if one ever
    /// could.
    /// </exception>
    public static async Task<FinancialFigureVisibility> ForCurrentRequestAsync(
        this CurrentCapabilities capabilities, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(capabilities);

        return await capabilities
            .AllowsAsync(Capability.DashboardViewFinancials, cancellationToken)
            .ConfigureAwait(false)
            ? FinancialFigureVisibility.Visible
            : FinancialFigureVisibility.Redacted;
    }
}
