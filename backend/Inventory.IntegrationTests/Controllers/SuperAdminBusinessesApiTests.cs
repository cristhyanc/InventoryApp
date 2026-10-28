using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Inventory.Domain.Suppliers;
using Inventory.Domain.Tenancy;
using Xunit;

namespace InventoryApi.Tests.Controllers;

/// <summary>
/// The Super Admin businesses API over real HTTP (issue #544): who may reach it, what it reports,
/// what each decision writes, and what it refuses.
///
/// <para>Everything here goes through the complete pipeline - authentication, <c>[Authorize]</c>,
/// the platform-admin policy, <c>[RequiredScope]</c>, <c>BusinessScopeMiddleware</c>'s independent
/// re-check of the same policy, the tenant query filters and <c>BusinessOwnershipEnforcer</c> - so
/// the authorization under test is the real one. Only the authentication is synthetic.</para>
/// </summary>
public class SuperAdminBusinessesApiTests : IClassFixture<SuperAdminBusinessesApiFactory>, IAsyncLifetime
{
    private readonly SuperAdminBusinessesApiFactory _factory;

    public SuperAdminBusinessesApiTests(SuperAdminBusinessesApiFactory factory)
    {
        _factory = factory;
    }

    public Task InitializeAsync() => _factory.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    #region Who may reach it

    /// <summary>
    /// The acceptance criterion "non-admins (including Owners) denied", asked of every endpoint on
    /// the surface. An Owner holds the strongest business role there is and still reaches none of
    /// it: a business role is not platform authority, and this controller declares no capability any
    /// role could satisfy.
    /// </summary>
    [Theory]
    [InlineData(SuperAdminBusinessesApiFactory.TradingOwnerObjectId)]
    [InlineData(SuperAdminBusinessesApiFactory.TradingManagerObjectId)]
    [InlineData(SuperAdminBusinessesApiFactory.ApplicantObjectId)]
    [InlineData(SuperAdminBusinessesApiFactory.StrangerObjectId)]
    public async Task Every_endpoint_refuses_a_caller_who_is_not_the_configured_administrator(string objectId)
    {
        var client = _factory.Authenticated(objectId);

        foreach (var response in await AllEndpointsAsync(client))
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        await AssertNothingWasDecidedAsync();
    }

    /// <summary>
    /// An anonymous request is refused by authentication with <c>401</c>, not turned into a
    /// <c>403</c> by the business-scope middleware: "you are not signed in" and "you are signed in
    /// and not the administrator" must stay different answers.
    /// </summary>
    [Fact]
    public async Task An_unauthenticated_request_is_unauthorized()
    {
        var client = _factory.CreateClient();

        foreach (var response in await AllEndpointsAsync(client))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    /// <summary>
    /// The delegated scope is required of the administrator too. Platform authority is an additional
    /// requirement on a delegated API call, never a replacement for one.
    /// </summary>
    [Fact]
    public async Task The_administrator_without_the_delegated_scope_is_refused()
    {
        var client = _factory.WithoutScope(SuperAdminBusinessesApiFactory.AdministratorObjectId);

        foreach (var response in await AllEndpointsAsync(client))
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        await AssertNothingWasDecidedAsync();
    }

    /// <summary>
    /// The bypass is of the membership requirement only. The administrator has no membership
    /// anywhere, so every business endpoint still refuses them - reaching this surface grants
    /// nothing else.
    /// </summary>
    [Fact]
    public async Task The_administrator_still_reaches_no_business_endpoint()
    {
        var client = _factory.AsAdministrator();

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/products")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/business/current")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/members")).StatusCode);
    }

    #endregion

    #region The directory

    /// <summary>
    /// The list: every business, pending applications first, with the ten declared fields and the
    /// application detail read off the rows.
    /// </summary>
    [Fact]
    public async Task The_directory_lists_every_business_with_pending_applications_first()
    {
        var body = await ReadJsonAsync(await _factory.AsAdministrator().GetAsync(ListUrl));

        var businesses = body.RootElement.EnumerateArray().ToList();
        Assert.Equal(3, businesses.Count);

        Assert.Equal(nameof(BusinessStatus.Pending), businesses[0].GetProperty("status").GetString());
        Assert.Equal(SuperAdminBusinessesApiFactory.PendingBusinessName, businesses[0].GetProperty("name").GetString());
        Assert.Equal(_factory.PendingBusinessId, businesses[0].GetProperty("businessId").GetInt32());
        Assert.Equal(
            SuperAdminBusinessesApiFactory.PendingContactEmail,
            businesses[0].GetProperty("contactEmail").GetString());
        Assert.Equal(SuperAdminBusinessesApiFactory.PendingAbn, businesses[0].GetProperty("abn").GetString());
        // The state the application recorded is shown when it is reviewed (issue #933), and a
        // business that recorded none reports it as unrecorded rather than as a guess.
        Assert.Equal(SuperAdminBusinessesApiFactory.PendingState, businesses[0].GetProperty("state").GetString());
        Assert.Equal(
            JsonValueKind.Null,
            businesses
                .Single(business => business.GetProperty("businessId").GetInt32() == _factory.RejectedBusinessId)
                .GetProperty("state")
                .ValueKind);
        Assert.Equal(1, businesses[0].GetProperty("activeMemberCount").GetInt32());
        Assert.False(businesses[0].GetProperty("nayaxConfigured").GetBoolean());
        Assert.Equal(
            SuperAdminBusinessesApiFactory.AppliedAt,
            DateTime.SpecifyKind(
                    businesses[0].GetProperty("appliedAtUtc").GetDateTime(),
                    DateTimeKind.Utc));
        Assert.Equal(JsonValueKind.Null, businesses[0].GetProperty("decidedAtUtc").ValueKind);

        var trading = businesses.Single(business =>
            business.GetProperty("businessId").GetInt32() == _factory.TradingBusinessId);
        Assert.Equal(2, trading.GetProperty("activeMemberCount").GetInt32());

        var rejected = businesses.Single(business =>
            business.GetProperty("businessId").GetInt32() == _factory.RejectedBusinessId);
        Assert.Equal(0, rejected.GetProperty("activeMemberCount").GetInt32());
        Assert.Equal(nameof(BusinessStatus.Rejected), rejected.GetProperty("status").GetString());
    }

    /// <summary>
    /// The acceptance criterion "no tenant data in responses", asserted structurally and by value:
    /// the response objects carry exactly the eleven declared properties, and a seeded product name and
    /// Nayax token ciphertext appear nowhere in the body - while the connection's existence is
    /// still reported as the yes/no it is meant to be.
    /// </summary>
    [Fact]
    public async Task The_directory_reports_no_tenant_data_and_no_credential()
    {
        const string ProductName = "Salted Caramel 40g";
        const string Ciphertext = "ciphertext-marker-that-must-never-be-served";
        await _factory.SeedTenantDataAsync(ProductName, Ciphertext);

        var response = await _factory.AsAdministrator().GetAsync(ListUrl);
        var raw = await response.Content.ReadAsStringAsync();
        var body = JsonDocument.Parse(raw);

        Assert.DoesNotContain(ProductName, raw, StringComparison.Ordinal);
        Assert.DoesNotContain(Ciphertext, raw, StringComparison.Ordinal);
        Assert.DoesNotContain("2002736764", raw, StringComparison.Ordinal);

        foreach (var business in body.RootElement.EnumerateArray())
        {
            Assert.Equal(
                [
                    "abn",
                    "activeMemberCount",
                    "appliedAtUtc",
                    "businessId",
                    "contactEmail",
                    "createdAtUtc",
                    "decidedAtUtc",
                    "name",
                    "nayaxConfigured",
                    "state",
                    "status",
                ],
                business.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        }

        Assert.True(
            body.RootElement.EnumerateArray()
                .Single(business => business.GetProperty("businessId").GetInt32() == _factory.TradingBusinessId)
                .GetProperty("nayaxConfigured")
                .GetBoolean());
    }

    [Fact]
    public async Task The_pending_count_reports_the_applications_waiting_for_a_decision()
    {
        var body = await ReadJsonAsync(await _factory.AsAdministrator().GetAsync(PendingCountUrl));

        Assert.Equal(1, body.RootElement.GetProperty("pendingCount").GetInt32());

        await _factory.SetBusinessStatusAsync(_factory.RejectedBusinessId, BusinessStatus.Pending);

        var again = await ReadJsonAsync(await _factory.AsAdministrator().GetAsync(PendingCountUrl));
        Assert.Equal(2, again.RootElement.GetProperty("pendingCount").GetInt32());
    }

    #endregion

    #region Approving

    /// <summary>
    /// The transition: the business becomes <c>Active</c> with a decision instant, the applicant
    /// keeps the active Owner membership that made the application theirs, and the audit event
    /// records the decision without a name, an address or an applicant.
    /// </summary>
    [Fact]
    public async Task Approving_a_pending_application_activates_the_business_and_is_audited()
    {
        var response = await _factory.AsAdministrator().PostAsync(ApproveUrl(_factory.PendingBusinessId), null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ReadJsonAsync(response);
        Assert.Equal(nameof(BusinessStatus.Active), body.RootElement.GetProperty("status").GetString());
        Assert.Equal(_factory.PendingBusinessId, body.RootElement.GetProperty("businessId").GetInt32());
        Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("rejectionReason").ValueKind);

        var business = await _factory.BusinessAsync(_factory.PendingBusinessId);
        Assert.Equal(BusinessStatus.Active, business.Status);
        Assert.NotNull(business.DecidedAtUtc);
        Assert.Equal(SuperAdminBusinessesApiFactory.AppliedAt, business.AppliedAtUtc);
        Assert.Null(business.RejectionReason);

        var applicant = await _factory.MembershipAsync(_factory.ApplicantMembershipId);
        Assert.True(applicant.IsActive);
        Assert.Equal(BusinessRole.Owner, applicant.Role);

        // Case-insensitively: ActorIdentity stores the canonical upper-case spelling of a GUID, so
        // the audit records the identity rather than the characters the header happened to carry.
        var audit = AuditEntry();
        Assert.Contains(
            SuperAdminBusinessesApiFactory.AdministratorObjectId,
            audit,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains($"business {_factory.PendingBusinessId};", audit, StringComparison.Ordinal);
        Assert.Contains("Pending -> Active", audit, StringComparison.Ordinal);
        Assert.Contains("reason recorded False", audit, StringComparison.Ordinal);
        Assert.DoesNotContain(
            SuperAdminBusinessesApiFactory.PendingBusinessName,
            audit,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            SuperAdminBusinessesApiFactory.PendingContactEmail,
            audit,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            SuperAdminBusinessesApiFactory.ApplicantObjectId,
            audit,
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The approved business's Owner is served, which is the whole point of the decision: before it
    /// their own business endpoint refuses them, and afterwards it answers.
    /// </summary>
    [Fact]
    public async Task The_applicant_reaches_their_business_only_once_it_is_approved()
    {
        var applicant = _factory.Authenticated(SuperAdminBusinessesApiFactory.ApplicantObjectId);

        Assert.Equal(HttpStatusCode.Forbidden, (await applicant.GetAsync("/api/business/current")).StatusCode);

        (await _factory.AsAdministrator().PostAsync(ApproveUrl(_factory.PendingBusinessId), null))
            .EnsureSuccessStatusCode();

        var current = await ReadJsonAsync(await applicant.GetAsync("/api/business/current"));
        Assert.Equal(
            SuperAdminBusinessesApiFactory.PendingBusinessName,
            current.RootElement.GetProperty("name").GetString());
    }

    /// <summary>
    /// The acceptance criterion "approve after the applicant joined elsewhere": their membership in
    /// the application is no longer active and they hold an active one in another business, so the
    /// approval is refused and nothing changes.
    /// </summary>
    [Fact]
    public async Task Approving_is_refused_once_the_applicant_has_joined_another_business()
    {
        await _factory.SetMembershipActiveAsync(_factory.ApplicantMembershipId, isActive: false);
        await using (var db = _factory.Database())
        {
            db.BusinessMemberships.Add(new Inventory.Infrastructure.Models.BusinessMembership
            {
                BusinessId = _factory.TradingBusinessId,
                DirectoryTenantId = SuperAdminBusinessesApiFactory.DirectoryTenantId,
                ObjectId = SuperAdminBusinessesApiFactory.ApplicantObjectId,
                Role = BusinessRole.Operator,
                IsActive = true,
                CreatedAtUtc = SuperAdminBusinessesApiFactory.CreatedAt,
                StatusChangedAtUtc = SuperAdminBusinessesApiFactory.CreatedAt,
            });
            await db.SaveChangesAsync();
        }

        var response = await _factory.AsAdministrator().PostAsync(ApproveUrl(_factory.PendingBusinessId), null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(
            BusinessDecisionPolicy.ApplicantUnavailableMessage,
            await DetailAsync(response));
        Assert.Equal(
            BusinessStatus.Pending,
            (await _factory.BusinessAsync(_factory.PendingBusinessId)).Status);
        Assert.Empty(_factory.LoggedEntries().Where(IsAuditEntry));
    }

    #endregion

    #region The approved business's own supplier copies (issue #849)

    /// <summary>
    /// The outcome the whole of issue #849 is about, asked of the real HTTP surface: a business that
    /// has just been approved holds its own <c>Supplier</c> row per <em>enabled</em> preset, with the
    /// name and website copied, and the copy marker stamped with the decision instant.
    /// </summary>
    [Fact]
    public async Task Approving_copies_the_enabled_presets_into_the_approved_business()
    {
        await _factory.ResetSupplierPresetsAsync();
        await _factory.SetSupplierPresetEnabledAsync("Costco", isEnabled: false);
        await _factory.SetSupplierPresetUrlAsync("Coles", "https://www.coles.com.au");

        var response = await _factory.AsAdministrator().PostAsync(ApproveUrl(_factory.PendingBusinessId), null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var copies = await _factory.SuppliersOfAsync(_factory.PendingBusinessId);
        Assert.Equal(["Big W", "Coles", "Woolworths"], copies.Select(supplier => supplier.Name).Order());
        Assert.Equal(
            "https://www.coles.com.au",
            copies.Single(supplier => supplier.Name == "Coles").Url);

        var business = await _factory.BusinessAsync(_factory.PendingBusinessId);
        Assert.Equal(business.DecidedAtUtc, business.SupplierPresetsCopiedAtUtc);
    }

    /// <summary>
    /// Zero enabled presets approves normally: the business trades with no suppliers, and the marker
    /// is still stamped, so a later approval attempt could not copy a second time.
    /// </summary>
    [Fact]
    public async Task Approving_with_no_enabled_preset_copies_nothing_and_still_records_the_copy()
    {
        await _factory.ResetSupplierPresetsAsync();
        foreach (var name in SuperAdminBusinessesApiFactory.ShippedSupplierPresetNames)
        {
            await _factory.SetSupplierPresetEnabledAsync(name, isEnabled: false);
        }

        (await _factory.AsAdministrator().PostAsync(ApproveUrl(_factory.PendingBusinessId), null))
            .EnsureSuccessStatusCode();

        Assert.Empty(await _factory.SuppliersOfAsync(_factory.PendingBusinessId));
        Assert.NotNull((await _factory.BusinessAsync(_factory.PendingBusinessId)).SupplierPresetsCopiedAtUtc);
    }

    /// <summary>
    /// A rejection equips nothing: the business is not starting to trade, so it receives no supplier
    /// and its marker stays <c>null</c>.
    /// </summary>
    [Fact]
    public async Task Rejecting_copies_no_preset_at_all()
    {
        await _factory.ResetSupplierPresetsAsync();

        (await _factory.AsAdministrator()
                .PostAsJsonAsync(RejectUrl(_factory.PendingBusinessId), new { reason = "Needs an ABN." }))
            .EnsureSuccessStatusCode();

        Assert.Empty(await _factory.SuppliersOfAsync(_factory.PendingBusinessId));
        Assert.Null((await _factory.BusinessAsync(_factory.PendingBusinessId)).SupplierPresetsCopiedAtUtc);
    }

    /// <summary>
    /// The other three paths that change a business's status copy nothing. A deactivation and a
    /// reactivation of the approved business leave its one set of copies exactly as it is, and the
    /// trading business - which was never approved through an application, exactly like one
    /// <c>bootstrap-business</c> adopted - receives nothing from either, nor a marker.
    /// </summary>
    [Fact]
    public async Task Deactivating_and_reactivating_copy_nothing()
    {
        await _factory.ResetSupplierPresetsAsync();
        var admin = _factory.AsAdministrator();

        (await admin.PostAsync(ApproveUrl(_factory.PendingBusinessId), null)).EnsureSuccessStatusCode();
        var copied = (await _factory.SuppliersOfAsync(_factory.PendingBusinessId))
            .Select(supplier => supplier.Id)
            .ToArray();
        var copiedAt = (await _factory.BusinessAsync(_factory.PendingBusinessId)).SupplierPresetsCopiedAtUtc;

        (await admin.PostAsync($"{ListUrl}/{_factory.PendingBusinessId}/deactivate", null))
            .EnsureSuccessStatusCode();
        (await admin.PostAsync($"{ListUrl}/{_factory.PendingBusinessId}/reactivate", null))
            .EnsureSuccessStatusCode();

        Assert.Equal(
            copied,
            (await _factory.SuppliersOfAsync(_factory.PendingBusinessId)).Select(supplier => supplier.Id));
        Assert.Equal(
            copiedAt,
            (await _factory.BusinessAsync(_factory.PendingBusinessId)).SupplierPresetsCopiedAtUtc);

        (await admin.PostAsync($"{ListUrl}/{_factory.TradingBusinessId}/deactivate", null))
            .EnsureSuccessStatusCode();
        (await admin.PostAsync($"{ListUrl}/{_factory.TradingBusinessId}/reactivate", null))
            .EnsureSuccessStatusCode();

        Assert.Empty(await _factory.SuppliersOfAsync(_factory.TradingBusinessId));
        Assert.Null((await _factory.BusinessAsync(_factory.TradingBusinessId)).SupplierPresetsCopiedAtUtc);
    }

    /// <summary>
    /// The copies are that business's own suppliers from the moment it starts trading: its Owner
    /// lists them through the ordinary supplier endpoints, under the ordinary supplier permission -
    /// and another business's Owner sees none of them.
    ///
    /// <para>Since issue #934 they are also <em>linked</em> copies, so what the Owner may do to one
    /// is narrower than it was: the contact details save, and the name, the website and the delete
    /// are refused with <c>400</c>. The list says so on each row, which is what this test pins at
    /// the HTTP boundary.</para>
    /// </summary>
    [Fact]
    public async Task The_copies_are_ordinary_suppliers_of_the_approved_business_and_nobody_elses()
    {
        await _factory.ResetSupplierPresetsAsync();

        (await _factory.AsAdministrator().PostAsync(ApproveUrl(_factory.PendingBusinessId), null))
            .EnsureSuccessStatusCode();

        var owner = _factory.Authenticated(SuperAdminBusinessesApiFactory.ApplicantObjectId);
        var listed = await ReadJsonAsync(await owner.GetAsync("/api/suppliers"));
        var names = listed.RootElement.EnumerateArray()
            .Select(supplier => supplier.GetProperty("name").GetString()!)
            .Order()
            .ToArray();
        Assert.Equal(
            SuperAdminBusinessesApiFactory.ShippedSupplierPresetNames.Order(),
            names);

        // Every copy reports itself as linked, locked against a rename and a delete, and in use.
        Assert.All(
            listed.RootElement.EnumerateArray(),
            supplier => Assert.Equal(
                (true, false, false, false),
                (supplier.GetProperty("isLinked").GetBoolean(),
                    supplier.GetProperty("isHidden").GetBoolean(),
                    supplier.GetProperty("canRename").GetBoolean(),
                    supplier.GetProperty("canDelete").GetBoolean())));

        var rows = listed.RootElement.EnumerateArray()
            .Select(supplier => (Id: supplier.GetProperty("id").GetInt32(), Name: supplier.GetProperty("name").GetString()!))
            .ToArray();

        // The name and the website are the master's, and the copy cannot be removed.
        var renamed = await owner.PutAsJsonAsync(
            $"/api/suppliers/{rows[0].Id}", new { name = "Renamed by the business" });
        Assert.Equal(HttpStatusCode.BadRequest, renamed.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.DeleteAsync($"/api/suppliers/{rows[1].Id}")).StatusCode);

        // The contact details are the business's own and still save, submitted with the stored name
        // as the Suppliers form sends them.
        var contacted = await owner.PutAsJsonAsync(
            $"/api/suppliers/{rows[0].Id}", new { name = rows[0].Name, phone = "0400 000 000" });
        Assert.Equal(HttpStatusCode.NoContent, contacted.StatusCode);

        // And "Not used" takes a copy out of the pickers without removing it.
        Assert.Equal(
            HttpStatusCode.NoContent,
            (await owner.PutAsJsonAsync($"/api/suppliers/{rows[1].Id}/not-used", new { isHidden = true })).StatusCode);

        var pickerList = await ReadJsonAsync(await owner.GetAsync("/api/suppliers"));
        Assert.DoesNotContain(
            rows[1].Id,
            pickerList.RootElement.EnumerateArray().Select(supplier => supplier.GetProperty("id").GetInt32()));

        var pageList = await ReadJsonAsync(await owner.GetAsync("/api/suppliers?includeHidden=true"));
        Assert.Equal(
            SuperAdminBusinessesApiFactory.ShippedSupplierPresetNames.Length,
            pageList.RootElement.EnumerateArray().Count());

        // Every copy is still there: nothing above removed a row.
        Assert.Equal(
            SuperAdminBusinessesApiFactory.ShippedSupplierPresetNames.Order(),
            (await _factory.SuppliersOfAsync(_factory.PendingBusinessId)).Select(supplier => supplier.Name).Order());

        // The platform's own list is untouched by any of it: the copying goes one way.
        Assert.Equal(
            SuperAdminBusinessesApiFactory.ShippedSupplierPresetNames.Order(),
            (await _factory.SupplierPresetsAsync()).Select(preset => preset.Name).Order());

        // And the trading business, which has its own suppliers and its own permissions, sees none
        // of the approved business's rows.
        var otherOwner = _factory.Authenticated(SuperAdminBusinessesApiFactory.TradingOwnerObjectId);
        var otherList = await ReadJsonAsync(await otherOwner.GetAsync("/api/suppliers"));
        Assert.Empty(otherList.RootElement.EnumerateArray());
    }

    /// <summary>
    /// <c>IsHidden</c> is a <c>bool</c>, so a body that omits it would otherwise bind to
    /// <c>false</c> indistinguishably from an explicit <c>isHidden: false</c> and silently leave a
    /// linked copy in the pickers instead of refusing the request.
    /// <c>JsonRequiredAttribute</c> refuses the under-posted body instead.
    /// </summary>
    [Fact]
    public async Task Not_used_without_the_isHidden_field_is_refused_and_changes_nothing()
    {
        await _factory.ResetSupplierPresetsAsync();
        (await _factory.AsAdministrator().PostAsync(ApproveUrl(_factory.PendingBusinessId), null))
            .EnsureSuccessStatusCode();

        var owner = _factory.Authenticated(SuperAdminBusinessesApiFactory.ApplicantObjectId);
        var listed = await ReadJsonAsync(await owner.GetAsync("/api/suppliers"));
        var supplierId = listed.RootElement.EnumerateArray().First().GetProperty("id").GetInt32();

        var response = await owner.PutAsJsonAsync($"/api/suppliers/{supplierId}/not-used", new { });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var reread = await ReadJsonAsync(await owner.GetAsync($"/api/suppliers/{supplierId}"));
        Assert.False(reread.RootElement.GetProperty("isHidden").GetBoolean());
    }

    /// <summary>
    /// A preset edited or disabled after the approval changes no copy: what the business holds is its
    /// own row, taken from the snapshot the approval read.
    /// </summary>
    [Fact]
    public async Task Editing_a_preset_after_the_approval_leaves_the_copies_alone()
    {
        await _factory.ResetSupplierPresetsAsync();

        (await _factory.AsAdministrator().PostAsync(ApproveUrl(_factory.PendingBusinessId), null))
            .EnsureSuccessStatusCode();

        await _factory.RenameSupplierPresetAsync("Coles", "Coles Group");
        await _factory.SetSupplierPresetUrlAsync("Coles Group", "https://www.colesgroup.example");
        await _factory.SetSupplierPresetEnabledAsync("Woolworths", isEnabled: false);

        var copies = await _factory.SuppliersOfAsync(_factory.PendingBusinessId);
        Assert.Equal(
            SuperAdminBusinessesApiFactory.ShippedSupplierPresetNames.Order(),
            copies.Select(supplier => supplier.Name).Order());
        Assert.All(copies, supplier => Assert.Null(supplier.Url));
    }

    #endregion

    #region Supplier records by business state, and "Available to add" (issue #935)

    /// <summary>
    /// The outcome issue #935 is about, at the HTTP boundary: the approval copies the records that
    /// serve the approved business's own recorded state - the nationwide ones and the ones naming
    /// that state - and not a record that serves somewhere else.
    ///
    /// <para>The pending application recorded <c>QLD</c>, so a Queensland record is copied and a
    /// Western Australian one is not, from the same platform list.</para>
    /// </summary>
    [Fact]
    public async Task Approving_copies_only_the_records_that_serve_the_business_s_state()
    {
        await _factory.ResetSupplierPresetsAsync();
        await _factory.SeedSupplierPresetAsync(
            "Brisbane Wholesale", servesNationwide: false, servedStates: SuperAdminBusinessesApiFactory.PendingState);
        await _factory.SeedSupplierPresetAsync("Perth Wholesale", servesNationwide: false, servedStates: "WA");
        await _factory.SeedSupplierPresetAsync(
            "Disabled Queensland",
            isEnabled: false,
            servesNationwide: false,
            servedStates: SuperAdminBusinessesApiFactory.PendingState);

        (await _factory.AsAdministrator().PostAsync(ApproveUrl(_factory.PendingBusinessId), null))
            .EnsureSuccessStatusCode();

        Assert.Equal(
            ["Big W", "Brisbane Wholesale", "Coles", "Costco", "Woolworths"],
            (await _factory.SuppliersOfAsync(_factory.PendingBusinessId))
                .Select(supplier => supplier.Name)
                .Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// The "Available to add" section and its one-click add, end to end as the business's own Owner:
    /// a record enabled after the approval is <strong>not</strong> pushed into the business, is
    /// offered to it, and becomes a linked copy when the Owner adds it. Adding it again is a
    /// <c>409</c> with one copy, and a record that serves another state is not offered and cannot be
    /// added by id.
    /// </summary>
    [Fact]
    public async Task A_record_enabled_later_is_offered_to_the_business_and_added_with_one_request()
    {
        await _factory.ResetSupplierPresetsAsync();
        (await _factory.AsAdministrator().PostAsync(ApproveUrl(_factory.PendingBusinessId), null))
            .EnsureSuccessStatusCode();

        // The platform adds two records afterwards. Neither reaches the business by itself.
        var brisbane = await _factory.SeedSupplierPresetAsync(
            "Brisbane Wholesale",
            "https://brisbane.example",
            servesNationwide: false,
            servedStates: SuperAdminBusinessesApiFactory.PendingState);
        var perth = await _factory.SeedSupplierPresetAsync(
            "Perth Wholesale", servesNationwide: false, servedStates: "WA");

        var owner = _factory.Authenticated(SuperAdminBusinessesApiFactory.ApplicantObjectId);
        Assert.Equal(
            SuperAdminBusinessesApiFactory.ShippedSupplierPresetNames.Length,
            (await _factory.SuppliersOfAsync(_factory.PendingBusinessId)).Count);

        // It is offered instead, with its id, name and website - and the Western Australian record
        // is not offered at all.
        var offered = await ReadJsonAsync(await owner.GetAsync("/api/suppliers/available-presets"));
        var row = Assert.Single(offered.RootElement.EnumerateArray());
        Assert.Equal(
            (brisbane, "Brisbane Wholesale", "https://brisbane.example"),
            (row.GetProperty("id").GetInt32(),
                row.GetProperty("name").GetString(),
                row.GetProperty("url").GetString()));

        // One click adds it, as this business's own linked copy.
        var added = await owner.PostAsync($"/api/suppliers/available-presets/{brisbane}", null);
        Assert.Equal(HttpStatusCode.Created, added.StatusCode);
        var copy = await ReadJsonAsync(added);
        Assert.Equal("Brisbane Wholesale", copy.RootElement.GetProperty("name").GetString());
        Assert.True(copy.RootElement.GetProperty("isLinked").GetBoolean());
        Assert.False(copy.RootElement.GetProperty("canRename").GetBoolean());
        Assert.False(copy.RootElement.GetProperty("canDelete").GetBoolean());

        var suppliers = await _factory.SuppliersOfAsync(_factory.PendingBusinessId);
        Assert.Equal(
            brisbane,
            suppliers.Single(supplier => supplier.Name == "Brisbane Wholesale").SupplierPresetId);

        // It is no longer on offer, a second add is refused as already added, and there is one copy.
        Assert.Empty(
            (await ReadJsonAsync(await owner.GetAsync("/api/suppliers/available-presets")))
                .RootElement.EnumerateArray());

        var again = await owner.PostAsync($"/api/suppliers/available-presets/{brisbane}", null);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal(SupplierPresetMarketPolicy.AlreadyAddedMessage, await DetailAsync(again));

        // A record that serves another state cannot be added by id either.
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await owner.PostAsync($"/api/suppliers/available-presets/{perth}", null)).StatusCode);

        Assert.Equal(
            SuperAdminBusinessesApiFactory.ShippedSupplierPresetNames.Length + 1,
            (await _factory.SuppliersOfAsync(_factory.PendingBusinessId)).Count);

        // And nothing of this reached the other business: it is offered the nationwide records it
        // has no copy of - it has none at all - and holds no supplier of its own.
        var otherOwner = _factory.Authenticated(SuperAdminBusinessesApiFactory.TradingOwnerObjectId);
        Assert.Equal(
            SuperAdminBusinessesApiFactory.ShippedSupplierPresetNames.Order(),
            (await ReadJsonAsync(await otherOwner.GetAsync("/api/suppliers/available-presets")))
                .RootElement.EnumerateArray()
                .Select(preset => preset.GetProperty("name").GetString()!)
                .Order());
        Assert.Empty(await _factory.SuppliersOfAsync(_factory.TradingBusinessId));
    }

    #endregion

    #region Refusing

    /// <summary>
    /// The transition: the business becomes <c>Rejected</c> with the reason and a decision instant,
    /// and the applicant's membership is released so they are free to apply again.
    /// </summary>
    [Fact]
    public async Task Rejecting_a_pending_application_records_the_reason_and_releases_the_applicant()
    {
        var response = await _factory.AsAdministrator()
            .PostAsJsonAsync(RejectUrl(_factory.PendingBusinessId), new { reason = "  Needs a registered ABN.  " });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ReadJsonAsync(response);
        Assert.Equal(nameof(BusinessStatus.Rejected), body.RootElement.GetProperty("status").GetString());
        Assert.Equal("Needs a registered ABN.", body.RootElement.GetProperty("rejectionReason").GetString());

        var business = await _factory.BusinessAsync(_factory.PendingBusinessId);
        Assert.Equal(BusinessStatus.Rejected, business.Status);
        Assert.Equal("Needs a registered ABN.", business.RejectionReason);
        Assert.NotNull(business.DecidedAtUtc);

        var applicant = await _factory.MembershipAsync(_factory.ApplicantMembershipId);
        Assert.False(applicant.IsActive);
        Assert.Equal(business.DecidedAtUtc, applicant.StatusChangedAtUtc);

        var audit = AuditEntry();
        Assert.Contains("Pending -> Rejected", audit, StringComparison.Ordinal);
        Assert.Contains("reason recorded True", audit, StringComparison.Ordinal);
        Assert.DoesNotContain("registered ABN", audit, StringComparison.Ordinal);
    }

    /// <summary>
    /// A rejection needs no body at all, and no reason. "No reason" has one stored representation,
    /// so an absent body, an absent field and a blank one are the same rejection.
    /// </summary>
    [Fact]
    public async Task Rejecting_without_a_body_is_still_a_rejection()
    {
        var response = await _factory.AsAdministrator().PostAsync(RejectUrl(_factory.PendingBusinessId), null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            JsonValueKind.Null,
            (await ReadJsonAsync(response)).RootElement.GetProperty("rejectionReason").ValueKind);

        var business = await _factory.BusinessAsync(_factory.PendingBusinessId);
        Assert.Equal(BusinessStatus.Rejected, business.Status);
        Assert.Null(business.RejectionReason);
        Assert.False((await _factory.MembershipAsync(_factory.ApplicantMembershipId)).IsActive);
        Assert.Contains("reason recorded False", AuditEntry(), StringComparison.Ordinal);
    }

    /// <summary>
    /// An over-long reason is refused with <c>400</c> and writes nothing: a reason nothing could
    /// store must not be silently truncated into the applicant's own explanation.
    /// </summary>
    [Fact]
    public async Task A_reason_longer_than_the_limit_is_refused_and_writes_nothing()
    {
        var response = await _factory.AsAdministrator().PostAsJsonAsync(
            RejectUrl(_factory.PendingBusinessId),
            new { reason = new string('x', BusinessDecisionPolicy.MaxRejectionReasonLength + 1) });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(BusinessDecisionPolicy.RejectionReasonTooLongMessage, await DetailAsync(response));
        await AssertNothingWasDecidedAsync();
    }

    #endregion

    #region Refusals both decisions share

    [Theory]
    [InlineData("approve")]
    [InlineData("reject")]
    public async Task An_unknown_business_is_not_found(string decision)
    {
        var response = await _factory.AsAdministrator().PostAsync($"{ListUrl}/999999/{decision}", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertNothingWasDecidedAsync();
    }

    /// <summary>
    /// Only a pending application may be decided: an already-trading business, an already-refused
    /// one and a deactivated one are each refused with the one sentence, and nothing changes.
    /// </summary>
    [Theory]
    [InlineData("approve", BusinessStatus.Active)]
    [InlineData("approve", BusinessStatus.Rejected)]
    [InlineData("approve", BusinessStatus.Deactivated)]
    [InlineData("reject", BusinessStatus.Active)]
    [InlineData("reject", BusinessStatus.Rejected)]
    [InlineData("reject", BusinessStatus.Deactivated)]
    public async Task Only_a_pending_application_may_be_decided(string decision, BusinessStatus status)
    {
        await _factory.SetBusinessStatusAsync(_factory.PendingBusinessId, status);

        var response = await _factory.AsAdministrator()
            .PostAsync($"{ListUrl}/{_factory.PendingBusinessId}/{decision}", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(BusinessDecisionPolicy.NotPendingMessage, await DetailAsync(response));
        Assert.Equal(status, (await _factory.BusinessAsync(_factory.PendingBusinessId)).Status);
        Assert.Empty(_factory.LoggedEntries().Where(IsAuditEntry));
    }

    /// <summary>Deciding twice is refused the second time, and the first decision stands.</summary>
    [Fact]
    public async Task Deciding_an_already_decided_application_is_refused()
    {
        (await _factory.AsAdministrator().PostAsync(ApproveUrl(_factory.PendingBusinessId), null))
            .EnsureSuccessStatusCode();
        var decided = await _factory.BusinessAsync(_factory.PendingBusinessId);

        var again = await _factory.AsAdministrator().PostAsync(RejectUrl(_factory.PendingBusinessId), null);

        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        var unchanged = await _factory.BusinessAsync(_factory.PendingBusinessId);
        Assert.Equal(BusinessStatus.Active, unchanged.Status);
        Assert.Equal(decided.DecidedAtUtc, unchanged.DecidedAtUtc);
        Assert.Null(unchanged.RejectionReason);
    }

    #endregion

    #region Helpers

    private const string ListUrl = "/api/super-admin/businesses";
    private const string PendingCountUrl = "/api/super-admin/businesses/pending-count";
    private const string SupportSessionsUrl = "/api/super-admin/support-sessions";

    private static string ApproveUrl(int businessId) => $"{ListUrl}/{businessId}/approve";

    private static string RejectUrl(int businessId) => $"{ListUrl}/{businessId}/reject";

    /// <summary>
    /// Every endpoint on the whole platform-admin business surface, called once, so an authorization
    /// assertion is about the controllers rather than whichever action a test happened to pick. The
    /// four lifecycle endpoints of issue #545 and the three support-session endpoints of issue #550
    /// are here for exactly that reason: an endpoint that writes a business's status, releases
    /// somebody's membership or grants somebody read access to another business must be refused for
    /// every caller who is not the configured administrator, and "every endpoint" has to keep meaning
    /// every endpoint.
    /// </summary>
    private async Task<IReadOnlyList<HttpResponseMessage>> AllEndpointsAsync(HttpClient client) =>
    [
        await client.GetAsync(ListUrl),
        await client.GetAsync(PendingCountUrl),
        await client.PostAsync(ApproveUrl(_factory.PendingBusinessId), null),
        await client.PostAsync(
            RejectUrl(_factory.PendingBusinessId),
            new StringContent("{\"reason\":\"no\"}", Encoding.UTF8, "application/json")),
        await client.PostAsync($"{ListUrl}/{_factory.TradingBusinessId}/deactivate", null),
        await client.PostAsync($"{ListUrl}/{_factory.TradingBusinessId}/reactivate", null),
        await client.GetAsync($"{ListUrl}/{_factory.TradingBusinessId}/members"),
        await client.PostAsync(
            $"{ListUrl}/{_factory.TradingBusinessId}/members/{_factory.TradingOwnerMembershipId}/release",
            null),
        await client.PostAsync(
            $"{SupportSessionsUrl}/{_factory.TradingBusinessId}",
            new StringContent("{\"reason\":\"looking\"}", Encoding.UTF8, "application/json")),
        await client.GetAsync($"{SupportSessionsUrl}/active"),
        await client.PostAsync($"{SupportSessionsUrl}/active/end", null),
    ];

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private static async Task<string?> DetailAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync())
            .RootElement
            .GetProperty("detail")
            .GetString();

    private static bool IsAuditEntry(string entry) =>
        entry.StartsWith("Platform business decision audit.", StringComparison.Ordinal);

    /// <summary>
    /// Any platform audit event at all - a decision's, a lifecycle action's (issue #545) or a support
    /// session's (issue #550) - so "nothing happened" can be asserted about the whole surface rather
    /// than about one of the three trails.
    /// </summary>
    private static bool IsPlatformAuditEntry(string entry) =>
        IsAuditEntry(entry)
        || entry.StartsWith("Platform business lifecycle audit.", StringComparison.Ordinal)
        || entry.StartsWith("Support session audit.", StringComparison.Ordinal);

    /// <summary>The one audit event the request under test emitted.</summary>
    private string AuditEntry() => Assert.Single(_factory.LoggedEntries().Where(IsAuditEntry));

    /// <summary>
    /// The pending application and the trading business are both untouched, no support session
    /// exists, and nothing was audited - the assertion every refusal shares. The trading business is
    /// here because the lifecycle and support-session endpoints above are called against it: a
    /// refused caller must have changed none of it and must not have been granted read access to it.
    /// </summary>
    private async Task AssertNothingWasDecidedAsync()
    {
        Assert.Empty(await _factory.SupportSessionsAsync());

        var business = await _factory.BusinessAsync(_factory.PendingBusinessId);
        Assert.Equal(BusinessStatus.Pending, business.Status);
        Assert.Null(business.DecidedAtUtc);
        Assert.Null(business.RejectionReason);
        Assert.True((await _factory.MembershipAsync(_factory.ApplicantMembershipId)).IsActive);

        Assert.Equal(
            BusinessStatus.Active,
            (await _factory.BusinessAsync(_factory.TradingBusinessId)).Status);
        Assert.True((await _factory.MembershipAsync(_factory.TradingOwnerMembershipId)).IsActive);

        Assert.Empty(_factory.LoggedEntries().Where(IsPlatformAuditEntry));
    }

    #endregion
}

/// <summary>
/// The shipped state, as issue #336's own suite asserts for the diagnostics API: with no platform
/// administrator configured, nobody reaches this surface at all - not an Owner, and not the actor
/// who is the administrator on a host that configures one (issue #544).
/// </summary>
public class SuperAdminBusinessesUnconfiguredApiTests
    : IClassFixture<SuperAdminBusinessesApiFactory.Unconfigured>, IAsyncLifetime
{
    private readonly SuperAdminBusinessesApiFactory.Unconfigured _factory;

    public SuperAdminBusinessesUnconfiguredApiTests(SuperAdminBusinessesApiFactory.Unconfigured factory)
    {
        _factory = factory;
    }

    public Task InitializeAsync() => _factory.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Theory]
    [InlineData(SuperAdminBusinessesApiFactory.AdministratorObjectId)]
    [InlineData(SuperAdminBusinessesApiFactory.TradingOwnerObjectId)]
    public async Task Nobody_reaches_the_surface_when_no_administrator_is_configured(string objectId)
    {
        var client = _factory.Authenticated(objectId);

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await client.GetAsync("/api/super-admin/businesses")).StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await client.PostAsync($"/api/super-admin/businesses/{_factory.PendingBusinessId}/approve", null))
                .StatusCode);

        // And the support-session surface of issue #550, which is the same policy and the same
        // marker: with no administrator configured, nobody may grant themselves read access to a
        // business either, and no session row is written.
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await client.PostAsync(
                $"/api/super-admin/support-sessions/{_factory.TradingBusinessId}",
                new StringContent("{\"reason\":\"looking\"}", Encoding.UTF8, "application/json")))
                .StatusCode);

        Assert.Equal(
            BusinessStatus.Pending,
            (await _factory.BusinessAsync(_factory.PendingBusinessId)).Status);
        Assert.Empty(await _factory.SupportSessionsAsync());
    }
}
