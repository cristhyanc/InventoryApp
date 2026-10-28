using Inventory.Application.Businesses;
using Inventory.Application.Suppliers;
using Inventory.Application.Tenancy;
using Inventory.Domain.Exceptions;
using Inventory.Domain.Tenancy;
using InventoryApi.Tests.Application.Gst;
using InventoryApi.Tests.Application.Suppliers;
using InventoryApi.Tests.Application.Tenancy;
using InventoryApi.Tests.Application.Time;
using Xunit;

namespace InventoryApi.Tests.Application.Businesses;

/// <summary>
/// The two use cases behind the Owner's business settings (issue #548), without ASP.NET Core or EF
/// Core.
///
/// What they have to show here is where each decision comes from: the business from the caller's
/// resolved membership and never from the submission, the validation from the Domain policy before
/// anything is stored, and the audit event from the fields the write actually changed - recorded
/// after the write, for an identified actor, and never for a save that changed nothing.
/// </summary>
public class BusinessSettingsUseCaseTests
{
    private const int BusinessId = 7;
    private const string Sydney = "Australia/Sydney";
    private const string NewYork = "America/New_York";
    private const string ValidAbn = "51824753556";
    private const string Nsw = "NSW";
    private const string DirectoryTenantId = "3F2504E0-4F89-11D3-9A0C-0305E82C3301";
    private const string ObjectId = "9A0C0305-4F89-11D3-9A0C-0305E82C3301";

    private static readonly DateTime SavedAt = new(2026, 10, 11, 2, 30, 0, DateTimeKind.Utc);

    private static readonly BusinessSettings Stored =
        new("Vending Co", "owner@example.com", ValidAbn, true, Sydney, Nsw);

    private static BusinessSettingsSubmission Submission(
        string? name = "Vending Co",
        string? contactEmail = "owner@example.com",
        string? abn = ValidAbn,
        bool? gstRegistered = true,
        string? timeZoneId = Sydney,
        string? state = Nsw) =>
        new(name, contactEmail, abn, gstRegistered, timeZoneId, state);

    private static ActorIdentity Actor()
    {
        Assert.True(ActorIdentity.TryCreate(DirectoryTenantId, ObjectId, out var actor));
        return actor!;
    }

    [Fact]
    public async Task The_read_reports_the_settings_of_the_business_the_membership_resolved_to()
    {
        var store = new FakeBusinessSettingsStore(Stored);

        var settings = await new GetBusinessSettings(new FakeCurrentBusinessProvider(BusinessId), store)
            .Handle(CancellationToken.None);

        Assert.Equal(Stored, settings);
        Assert.Equal(Inventory.Domain.Tenancy.BusinessId.From(BusinessId), store.LastRequestedBusinessId);
    }

    [Fact]
    public async Task The_read_fails_closed_when_the_resolved_business_is_gone()
    {
        var store = new FakeBusinessSettingsStore(null);

        await Assert.ThrowsAsync<DomainConflictException>(
            () => new GetBusinessSettings(new FakeCurrentBusinessProvider(BusinessId), store)
                .Handle(CancellationToken.None));
    }

    [Theory]
    [InlineData(BusinessAccessDenialReason.MembershipMissing)]
    [InlineData(BusinessAccessDenialReason.BusinessInactive)]
    [InlineData(BusinessAccessDenialReason.RoleUnrecognised)]
    public async Task A_caller_with_no_usable_membership_reads_nothing(BusinessAccessDenialReason reason)
    {
        var store = new FakeBusinessSettingsStore(Stored);

        await Assert.ThrowsAsync<BusinessAccessDeniedException>(
            () => new GetBusinessSettings(FakeCurrentBusinessProvider.Denied(reason), store)
                .Handle(CancellationToken.None));

        Assert.Null(store.LastRequestedBusinessId);
    }

    /// <summary>
    /// The save stores the normalised values against the resolved business, and the submission it
    /// was handed carries no business of its own: there is nothing in it that could have chosen
    /// one.
    /// </summary>
    [Fact]
    public async Task The_save_stores_the_normalised_settings_against_the_resolved_business()
    {
        var store = new FakeBusinessSettingsStore(Stored);
        var audit = new RecordingAudit();

        var saved = await Create(store, audit).Handle(
            Submission(name: "  Renamed Co ", abn: "51 824 753 556", timeZoneId: NewYork),
            CancellationToken.None);

        Assert.Equal("Renamed Co", saved.Name);
        Assert.Equal(ValidAbn, saved.Abn);
        Assert.Equal(NewYork, saved.TimeZoneId);
        Assert.Equal(Inventory.Domain.Tenancy.BusinessId.From(BusinessId), store.LastSavedBusinessId);
    }

    [Fact]
    public async Task An_invalid_submission_is_refused_before_anything_is_stored()
    {
        var store = new FakeBusinessSettingsStore(Stored);
        var audit = new RecordingAudit();

        await Assert.ThrowsAsync<DomainValidationException>(
            () => Create(store, audit).Handle(Submission(contactEmail: "owner@"), CancellationToken.None));

        Assert.Null(store.LastSavedBusinessId);
        Assert.Empty(audit.Entries);
    }

    [Fact]
    public async Task A_caller_with_no_usable_membership_writes_nothing()
    {
        var store = new FakeBusinessSettingsStore(Stored);
        var audit = new RecordingAudit();

        await Assert.ThrowsAsync<BusinessAccessDeniedException>(
            () => Create(store, audit, FakeCurrentBusinessProvider.Denied()).Handle(
                Submission(),
                CancellationToken.None));

        Assert.Null(store.LastSavedBusinessId);
        Assert.Empty(audit.Entries);
    }

    /// <summary>
    /// An unauditable change is not made. A request whose token identifies nobody cannot reach this
    /// in practice - resolving the current business already requires an identified actor - and the
    /// ordering is what keeps the rule true rather than incidental.
    /// </summary>
    [Fact]
    public async Task A_change_that_could_not_be_attributed_is_refused_and_stores_nothing()
    {
        var store = new FakeBusinessSettingsStore(Stored);
        var audit = new RecordingAudit();

        await Assert.ThrowsAsync<BusinessAccessDeniedException>(
            () => Create(store, audit, actors: FakeAuthenticatedActorAccessor.Unidentifiable())
                .Handle(Submission(), CancellationToken.None));

        Assert.Null(store.LastSavedBusinessId);
        Assert.Empty(audit.Entries);
    }

    [Fact]
    public async Task A_stored_change_is_audited_with_the_actor_the_business_and_the_changed_fields()
    {
        var store = new FakeBusinessSettingsStore(Stored);
        var audit = new RecordingAudit();

        await Create(store, audit).Handle(
            Submission(name: "Renamed Co", timeZoneId: NewYork),
            CancellationToken.None);

        var entry = Assert.Single(audit.Entries);
        Assert.Equal(Actor().DirectoryTenantId, entry.ActorDirectoryTenantId);
        Assert.Equal(Actor().ObjectId, entry.ActorObjectId);
        Assert.Equal(BusinessId, entry.BusinessId);
        Assert.Equal(SavedAt, entry.OccurredAtUtc);
        Assert.Equal(
            [BusinessSettingsField.Name, BusinessSettingsField.TimeZoneId],
            entry.ChangedFields);
    }

    [Fact]
    public async Task A_save_that_changes_nothing_is_not_audited()
    {
        var store = new FakeBusinessSettingsStore(Stored);
        var audit = new RecordingAudit();

        await Create(store, audit).Handle(Submission(), CancellationToken.None);

        Assert.Equal(Inventory.Domain.Tenancy.BusinessId.From(BusinessId), store.LastSavedBusinessId);
        Assert.Empty(audit.Entries);
    }

    /// <summary>
    /// The audit event is evidence about a stored change, so a business that disappeared between
    /// the resolution and the write produces no event at all - and the caller is told the same
    /// thing the read tells them.
    /// </summary>
    [Fact]
    public async Task A_business_that_is_gone_is_neither_saved_nor_audited()
    {
        var store = new FakeBusinessSettingsStore(null);
        var audit = new RecordingAudit();

        await Assert.ThrowsAsync<DomainConflictException>(
            () => Create(store, audit).Handle(Submission(), CancellationToken.None));

        Assert.Empty(audit.Entries);
    }

    /// <summary>
    /// The save runs inside the shared write transaction, and the business's lifecycle status is
    /// re-read in it before anything is written (issue #680): a settings save already in flight when
    /// the Super Admin deactivates the business is refused with the fixed sentence, writes nothing
    /// and is audited as nothing.
    /// </summary>
    [Fact]
    public async Task A_save_is_refused_once_the_business_is_no_longer_active()
    {
        var store = new FakeBusinessSettingsStore(Stored);
        var audit = new RecordingAudit();
        var writeStore = new FakeBusinessMembershipWriteStore { Status = BusinessStatus.Deactivated };

        var refusal = await Assert.ThrowsAsync<DomainConflictException>(
            () => Create(store, audit, writeStore: writeStore)
                .Handle(Submission(name: "Renamed Co"), CancellationToken.None));

        Assert.Equal(ActiveBusinessWritePolicy.NotActiveMessage, refusal.Message);
        Assert.Equal(
            Inventory.Domain.Tenancy.BusinessId.From(BusinessId),
            writeStore.LastQueriedStatusBusiness);
        Assert.Null(store.LastSavedBusinessId);
        Assert.Empty(audit.Entries);
        Assert.DoesNotContain(FakeBusinessMembershipWriteStore.Commit, writeStore.Operations);
    }

    /// <summary>
    /// The status that decides it is read inside the write lock, not when the request arrived: a
    /// deactivation that commits while this save waits for the lock refuses it.
    /// </summary>
    [Fact]
    public async Task A_deactivation_that_commits_while_the_write_lock_is_awaited_refuses_the_save()
    {
        var store = new FakeBusinessSettingsStore(Stored);
        var audit = new RecordingAudit();
        var writeStore = new FakeBusinessMembershipWriteStore();
        writeStore.WhileWaitingForTheWriteLock = () => writeStore.Status = BusinessStatus.Deactivated;

        var refusal = await Assert.ThrowsAsync<DomainConflictException>(
            () => Create(store, audit, writeStore: writeStore)
                .Handle(Submission(name: "Renamed Co"), CancellationToken.None));

        Assert.Equal(ActiveBusinessWritePolicy.NotActiveMessage, refusal.Message);
        Assert.Null(store.LastSavedBusinessId);
        Assert.Empty(audit.Entries);
    }

    /// <summary>
    /// A permitted save still happens inside that transaction, with the status re-read first and the
    /// write committed - the other half of the rule above, and what would hide a guard that refused
    /// every save.
    /// </summary>
    [Fact]
    public async Task A_permitted_save_runs_inside_the_committed_transaction()
    {
        var store = new FakeBusinessSettingsStore(Stored);
        var writeStore = new FakeBusinessMembershipWriteStore();

        await Create(store, new RecordingAudit(), writeStore: writeStore)
            .Handle(Submission(name: "Renamed Co"), CancellationToken.None);

        Assert.Equal(
            [
                FakeBusinessMembershipWriteStore.Begin,
                FakeBusinessMembershipWriteStore.StatusRead,
                FakeBusinessMembershipWriteStore.Commit,
                FakeBusinessMembershipWriteStore.Dispose,
            ],
            writeStore.Operations);
        Assert.Equal(Inventory.Domain.Tenancy.BusinessId.From(BusinessId), store.LastSavedBusinessId);
    }

    /// <summary>
    /// A change of the business's state adds the supplier records that state newly entitles it to
    /// (issue #935), inside the same committed transaction as the save and after it - so what the
    /// top-up matches against is the state that was just stored.
    /// </summary>
    [Fact]
    public async Task A_state_change_adds_the_supplier_records_that_state_serves()
    {
        var store = new FakeBusinessSettingsStore(Stored);
        var presets = new FakeAvailableSupplierPresetStore()
            .WithPreset(1, "Coles", servesNationwide: true)
            .WithPreset(2, "JB Metro South Coast & ACT", servesNationwide: false, servedStates: "NSW,ACT")
            .WithPreset(3, "Perth Wholesale", servesNationwide: false, servedStates: "WA")
            .WithCopyOf(1);
        var writeStore = new FakeBusinessMembershipWriteStore();

        // The store answers about the state as it now stands, which is what the save just wrote.
        presets.BusinessState = "ACT";

        await Create(store, new RecordingAudit(), presets: presets, writeStore: writeStore)
            .Handle(Submission(state: "ACT"), CancellationToken.None);

        Assert.Equal(1, presets.ListCalls);
        Assert.Equal([1, 2], presets.Copies);
        Assert.Equal([BusinessId], presets.BusinessIds.Distinct());
        Assert.Contains(FakeBusinessMembershipWriteStore.Commit, writeStore.Operations);
    }

    /// <summary>
    /// A save that leaves the state exactly as it was adds nothing at all, even when the platform
    /// has records this business does not hold. A record added or enabled later is deliberately
    /// never pushed into an existing business - it is offered under "Available to add" instead - so
    /// an Owner correcting their ABN must not acquire suppliers as a side effect.
    /// </summary>
    [Fact]
    public async Task A_save_that_does_not_change_the_state_adds_no_supplier()
    {
        var store = new FakeBusinessSettingsStore(Stored);
        var presets = new FakeAvailableSupplierPresetStore()
            .WithPreset(1, "Coles", servesNationwide: true);
        presets.BusinessState = Nsw;

        await Create(store, new RecordingAudit(), presets: presets)
            .Handle(Submission(name: "Renamed Co"), CancellationToken.None);

        Assert.Equal(0, presets.ListCalls);
        Assert.Empty(presets.Copies);
    }

    /// <summary>
    /// A record another request copied between the top-up's list and its insert is skipped, not
    /// reported: the unique index refuses the second copy, one copy exists - which is the rule - and
    /// the Owner's state change still saves. The other matching record is still added, so one
    /// refusal does not abandon the rest of the top-up.
    /// </summary>
    [Fact]
    public async Task A_record_copied_while_the_state_change_runs_is_skipped_and_the_save_still_commits()
    {
        var store = new FakeBusinessSettingsStore(Stored);
        var presets = new FakeAvailableSupplierPresetStore()
            .WithPreset(1, "Coles", servesNationwide: true)
            .WithPreset(2, "JB Metro South Coast & ACT", servesNationwide: false, servedStates: "NSW,ACT");
        presets.BusinessState = "ACT";
        presets.RefuseNextCopy = true;
        var writeStore = new FakeBusinessMembershipWriteStore();

        var saved = await Create(store, new RecordingAudit(), presets: presets, writeStore: writeStore)
            .Handle(Submission(state: "ACT"), CancellationToken.None);

        Assert.Equal("ACT", saved.State);
        Assert.Equal([1, 2], presets.Copies.Order());
        Assert.Contains(FakeBusinessMembershipWriteStore.Commit, writeStore.Operations);
    }

    /// <summary>
    /// A record that stopped being offered between the top-up's list and its copy is skipped, and
    /// the Owner's state change still saves. The top-up's list is a candidate list: each copy judges
    /// the record's enabled flag, its served states and the business's own state for itself, so a
    /// record the Super Admin disabled in that window is never written - and refusing it is not a
    /// reason to fail a settings save. The other matching record is still added.
    /// </summary>
    [Fact]
    public async Task A_record_that_stopped_being_offered_during_the_state_change_is_skipped()
    {
        var store = new FakeBusinessSettingsStore(Stored);
        var presets = new FakeAvailableSupplierPresetStore()
            .WithPreset(1, "Coles", servesNationwide: true)
            .WithPreset(2, "JB Metro South Coast & ACT", servesNationwide: false, servedStates: "NSW,ACT");
        presets.BusinessState = "ACT";
        var writeStore = new FakeBusinessMembershipWriteStore();

        // The Super Admin stops suggesting it after the candidate list was read.
        presets.AfterListing = () => presets.Disable(2);

        var saved = await Create(store, new RecordingAudit(), presets: presets, writeStore: writeStore)
            .Handle(Submission(state: "ACT"), CancellationToken.None);

        Assert.Equal("ACT", saved.State);
        Assert.Equal([1], presets.Copies);
        Assert.Contains(FakeBusinessMembershipWriteStore.Commit, writeStore.Operations);
    }

    /// <summary>
    /// A refused save adds nothing either: the business was deactivated, so neither the settings nor
    /// a supplier is written. The top-up is inside the transaction the guard rolls back, and it is
    /// never reached at all when the status check refuses first.
    /// </summary>
    [Fact]
    public async Task A_refused_save_adds_no_supplier()
    {
        var store = new FakeBusinessSettingsStore(Stored);
        var presets = new FakeAvailableSupplierPresetStore().WithPreset(1, "Coles");
        presets.BusinessState = "ACT";
        var writeStore = new FakeBusinessMembershipWriteStore { Status = BusinessStatus.Deactivated };

        await Assert.ThrowsAsync<DomainConflictException>(
            () => Create(store, new RecordingAudit(), presets: presets, writeStore: writeStore)
                .Handle(Submission(state: "ACT"), CancellationToken.None));

        Assert.Equal(0, presets.ListCalls);
        Assert.Empty(presets.Copies);
    }

    private static UpdateBusinessSettings Create(
        FakeBusinessSettingsStore store,
        RecordingAudit audit,
        ICurrentBusinessProvider? currentBusiness = null,
        IAuthenticatedActorAccessor? actors = null,
        FakeAvailableSupplierPresetStore? presets = null,
        FakeBusinessMembershipWriteStore? writeStore = null) =>
        new(
            currentBusiness ?? new FakeCurrentBusinessProvider(BusinessId),
            actors ?? FakeAuthenticatedActorAccessor.Identified(Actor()),
            store,
            new AddMatchingSupplierPresets(presets ?? new FakeAvailableSupplierPresetStore()),
            new MembershipWriteGuard(writeStore ?? new FakeBusinessMembershipWriteStore()),
            audit,
            new FakeClock(SavedAt));

    /// <summary>
    /// An in-memory settings store that answers with one business's stored settings and records
    /// what it was asked to save, so a use-case test can see which business a write was aimed at
    /// without a database.
    /// </summary>
    private sealed class FakeBusinessSettingsStore : IBusinessSettingsStore
    {
        private BusinessSettings? _stored;

        public FakeBusinessSettingsStore(BusinessSettings? stored) => _stored = stored;

        public Inventory.Domain.Tenancy.BusinessId? LastRequestedBusinessId { get; private set; }

        public Inventory.Domain.Tenancy.BusinessId? LastSavedBusinessId { get; private set; }

        public Task<BusinessSettings?> FindAsync(
            Inventory.Domain.Tenancy.BusinessId businessId,
            CancellationToken cancellationToken)
        {
            LastRequestedBusinessId = businessId;
            return Task.FromResult(_stored);
        }

        public Task<BusinessSettingsSaveResult?> SaveAsync(
            Inventory.Domain.Tenancy.BusinessId businessId,
            BusinessSettings submitted,
            CancellationToken cancellationToken)
        {
            if (_stored is not { } current)
            {
                return Task.FromResult<BusinessSettingsSaveResult?>(null);
            }

            LastSavedBusinessId = businessId;
            var changed = BusinessSettingsPolicy.ChangedFields(current, submitted);
            _stored = submitted;

            return Task.FromResult<BusinessSettingsSaveResult?>(
                new BusinessSettingsSaveResult(submitted, changed));
        }
    }

    private sealed class RecordingAudit : IBusinessSettingsAudit
    {
        public List<BusinessSettingsAuditEntry> Entries { get; } = [];

        public void Record(BusinessSettingsAuditEntry entry) => Entries.Add(entry);
    }
}
