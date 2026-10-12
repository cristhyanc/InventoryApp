using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Xunit;

namespace InventoryApi.Tests.Controllers;

/// <summary>
/// The shipped state of issue #541: a host that has not switched <c>SelfServiceOnboarding:Enabled</c>
/// on at all, which is the only state source control carries and the state production stays in until
/// a human has run #500's migration command there and verified #329's deployed changes.
///
/// <para>Its own host, deliberately. The flag is read once at startup, so "on" and "off" cannot be
/// two states of one host - and a test class that could flip it would not be testing the thing a
/// deployment actually does.</para>
///
/// <para>What is asserted here is that "off" means <em>no such endpoint</em>: the framework's own
/// generic <c>404</c>, carrying no detail of any kind, whatever the submission is and whoever
/// submits it, and nothing written. A disabled feature that answered <c>400</c> for a malformed
/// body, or <c>409</c> for a caller who already belongs somewhere, would confirm both that the
/// feature exists and that this deployment chose not to run it.</para>
/// </summary>
public sealed class BusinessApplicationDisabledApiTests : IClassFixture<BusinessApplicationApiFactory.Disabled>
{
    private const string Uri = "/api/onboarding/business-applications";
    private const string ValidAbn = "51824753556";

    private readonly BusinessApplicationApiFactory.Disabled _factory;

    public BusinessApplicationDisabledApiTests(BusinessApplicationApiFactory.Disabled factory) =>
        _factory = factory;

    /// <summary>
    /// A well-formed application from an eligible caller is the framework's generic <c>404</c>, and
    /// creates no business and no membership: the person's account state is exactly what it was.
    /// </summary>
    [Fact]
    public async Task A_well_formed_application_is_not_found_and_writes_nothing()
    {
        await _factory.ResetAsync();
        var applicant = BusinessApplicationApiFactory.NewApplicantObjectId();

        var response = await _factory.Authenticated(applicant).PostAsJsonAsync(Uri, Application());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        AssertGenericNotFound(await response.Content.ReadAsStringAsync());

        Assert.Single(await _factory.BusinessesAsync());
        Assert.Empty(await _factory.MembershipsOfAsync(applicant));
        Assert.Equal("NoMembership", await _factory.AccountStateOfAsync(applicant));
    }

    /// <summary>
    /// And so is a malformed one, and one from a caller who already holds an active membership: the
    /// flag is checked before anything is validated or read, so the answer cannot be used to tell
    /// those cases apart - or to confirm that the feature is there at all.
    /// </summary>
    [Fact]
    public async Task The_same_404_answers_a_malformed_body_and_an_ineligible_caller()
    {
        await _factory.ResetAsync();

        var malformed = await _factory.Authenticated(BusinessApplicationApiFactory.NewApplicantObjectId())
            .PostAsync(Uri, new StringContent("{}", Encoding.UTF8, "application/json"));
        var ineligible = await _factory.Authenticated(BusinessApplicationApiFactory.ManagerObjectId)
            .PostAsJsonAsync(Uri, Application());

        Assert.Equal(HttpStatusCode.NotFound, malformed.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, ineligible.StatusCode);
        AssertGenericNotFound(await malformed.Content.ReadAsStringAsync());
        AssertGenericNotFound(await ineligible.Content.ReadAsStringAsync());

        Assert.Single(await _factory.BusinessesAsync());
    }

    /// <summary>
    /// A body MVC cannot bind - truncated JSON, a field of the wrong type, an empty body, or one with
    /// no media type - is the same generic <c>404</c>, not <c>[ApiController]</c>'s automatic
    /// <c>400</c> or <c>415</c>: the flag is decided before model binding, so a disabled deployment
    /// cannot be made to describe the endpoint's contract. Nothing is written and the caller's
    /// account state is unchanged.
    /// </summary>
    [Theory]
    [InlineData("malformed-json")]
    [InlineData("wrong-field-type")]
    [InlineData("empty-body")]
    [InlineData("no-content-type")]
    public async Task A_body_that_cannot_be_bound_is_not_found_and_writes_nothing(string kind)
    {
        await _factory.ResetAsync();
        var applicant = BusinessApplicationApiFactory.NewApplicantObjectId();

        var response = await _factory.Authenticated(applicant)
            .PostAsync(Uri, BusinessApplicationApiFactory.UnbindableBody(kind));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        AssertGenericNotFound(await response.Content.ReadAsStringAsync());

        Assert.Single(await _factory.BusinessesAsync());
        Assert.Empty(await _factory.MembershipsOfAsync(applicant));
        Assert.Equal("NoMembership", await _factory.AccountStateOfAsync(applicant));
    }

    /// <summary>
    /// And the gate does not run ahead of authentication or the delegated scope: an unbindable body
    /// from an unauthenticated caller is still <c>401</c>, and from a caller without the scope still
    /// <c>403</c>, so the order of the answers is the same whether the feature is on or off.
    /// </summary>
    [Theory]
    [InlineData("malformed-json")]
    [InlineData("wrong-field-type")]
    [InlineData("empty-body")]
    public async Task Authentication_still_decides_before_the_flag_for_a_body_that_cannot_be_bound(string kind)
    {
        await _factory.ResetAsync();
        var applicant = BusinessApplicationApiFactory.NewApplicantObjectId();

        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await _factory.CreateClient().PostAsync(Uri, BusinessApplicationApiFactory.UnbindableBody(kind)))
                .StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await _factory.WithoutScope(applicant).PostAsync(Uri, BusinessApplicationApiFactory.UnbindableBody(kind)))
                .StatusCode);

        Assert.Single(await _factory.BusinessesAsync());
        Assert.Empty(await _factory.MembershipsOfAsync(applicant));
    }

    /// <summary>
    /// The marker still removes the membership requirement and nothing else, even with the feature
    /// off: an unauthenticated request is <c>401</c> and one without the delegated scope is
    /// <c>403</c>, both decided before the endpoint runs. A disabled feature must not become a way
    /// to probe authentication either.
    /// </summary>
    [Fact]
    public async Task Authentication_and_the_delegated_scope_are_still_required()
    {
        await _factory.ResetAsync();

        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await _factory.CreateClient().PostAsJsonAsync(Uri, Application())).StatusCode);

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await _factory.WithoutScope(BusinessApplicationApiFactory.NewApplicantObjectId())
                .PostAsJsonAsync(Uri, Application())).StatusCode);
    }

    /// <summary>
    /// <c>GET /api/me/account-state</c> reports the flag as off, which is what the welcome screen
    /// reads to decide whether to offer "Start a new business" at all. One flag, one answer: the
    /// screen and the endpoint cannot disagree.
    /// </summary>
    [Fact]
    public async Task Account_state_reports_onboarding_as_disabled()
    {
        await _factory.ResetAsync();
        var applicant = BusinessApplicationApiFactory.NewApplicantObjectId();

        var state = JsonDocument.Parse(
                await (await _factory.Authenticated(applicant).GetAsync("/api/me/account-state"))
                    .Content.ReadAsStringAsync())
            .RootElement;

        Assert.Equal("NoMembership", state.GetProperty("state").GetString());
        Assert.False(state.GetProperty("onboardingEnabled").GetBoolean());
        Assert.Equal(JsonValueKind.Null, state.GetProperty("rejectionReason").ValueKind);
    }

    /// <summary>
    /// The existing business works exactly as it always has: switching a feature off is not a
    /// change to anybody's access.
    /// </summary>
    [Fact]
    public async Task The_existing_businesss_members_are_unaffected()
    {
        await _factory.ResetAsync();

        var response = await _factory.Authenticated(BusinessApplicationApiFactory.OwnerObjectId)
            .GetAsync("/api/me/access");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var access = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("Owner", access.GetProperty("role").GetString());
        Assert.Equal(
            BusinessApplicationApiFactory.ExistingBusinessName,
            access.GetProperty("businessName").GetString());
    }

    /// <summary>
    /// The framework's own generic <c>404</c> body and nothing more: a title of "Not Found", no
    /// <c>detail</c> at all, and no mention of onboarding, a flag, or a feature being switched off.
    /// A caller probing this path must not be able to tell a disabled deployment from one where the
    /// feature does not exist.
    /// </summary>
    private static void AssertGenericNotFound(string body)
    {
        var problem = JsonDocument.Parse(body).RootElement;

        Assert.Equal(404, problem.GetProperty("status").GetInt32());
        Assert.Equal("Not Found", problem.GetProperty("title").GetString());
        Assert.False(problem.TryGetProperty("detail", out _));
        Assert.DoesNotContain("onboarding", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("disabled", body, StringComparison.OrdinalIgnoreCase);
    }

    private static object Application() => new
    {
        name = "Coastal Vending",
        contactEmail = "owner@coastal.example.com",
        abn = ValidAbn,
        gstRegistered = true,
        timeZoneId = "Australia/Brisbane",
    };
}
