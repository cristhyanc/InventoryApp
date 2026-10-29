import { Component, HostListener, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ActivatedRoute } from '@angular/router';
import { MachineService } from '../../services/machine.service';
import { ProductService } from '../../services/product.service';
import { SiteService } from '../../services/site.service';
import { ToastService } from '../../services/toast.service';
import { Machine, Product, SiteListItem } from '../../models/models';
import { ListLoadState } from '../shared/list-load-state';
import { PERMISSION_NOT_GRANTED_REASON, nayaxUnavailableMessage } from '../../provider/nayax-availability';
import { ProviderAvailabilityService } from '../../provider/provider-availability.service';
import { loadProviderList } from '../../provider/provider-list-load';
import { ManualPickListCatalogueState, ManualPickListComponent, ManualPickUnsavedWork } from './manual-pick-list.component';
import { PlanPicksComponent } from './plan-picks/plan-picks.component';

/**
 * Which of the page's two views is showing. The values are the long-standing internal ones, which
 * the machine-detail page's "Record refill" deep link (`?mode=manual`) still carries; the names the
 * operator reads are **Plan picks** and **Record refill** (issue #996). See
 * `PickListComponent.calculatedUnavailableReason`.
 */
export type PickListMode = 'calculated' | 'manual';

/** The view names, in one place, so the toggle, the headings and the guard all say the same thing. */
export const PLAN_PICKS_VIEW_NAME = 'Plan picks';
export const RECORD_REFILL_VIEW_NAME = 'Record refill';

/** What Record refill says it does to inventory, before anything is recorded (issue #996). */
export const RECORD_REFILL_IMPACT_MESSAGE = 'Recording this refill reduces storage stock by the quantities entered.';

/** What Plan picks says it does to inventory: nothing at all. */
export const PLAN_PICKS_IMPACT_MESSAGE = 'Planning only. Editing quantities and checking picks does not change inventory.';

/** What leaving unrecorded quantities is warned about; see {@link PickListComponent.confirmLeave}. */
export const LEAVE_WITH_ENTERED_QUANTITIES_WARNING =
  'The quantities you entered have not been recorded yet. Leaving this page discards them. Leave anyway?';

/** What leaving planning work is warned about: it is held in this page and nowhere else. */
export const LEAVE_WITH_PLANNING_WORK_WARNING =
  'Your edited pick quantities and checked picks are held on this page only. Leaving discards them. Leave anyway?';

/**
 * What leaving an unresolved refill save is warned about. It is the most serious of the three: the
 * retry context - the exact payload and idempotency key that make a resend safe - lives in this
 * page, so leaving means nothing can finish or safely repeat that submission.
 */
export const LEAVE_WITH_UNRESOLVED_SUBMISSION_WARNING =
  'A refill save has not been confirmed. Leaving loses the retry that would finish it, and you will not be able to tell from here whether it was recorded. Leave anyway?';

/**
 * The Pick List page (issues #222, #226, #744, #904; redesigned by issue #996), which composes the
 * two views an operator plans and records a stock run with, and owns the reads they share.
 *
 * - **Plan picks** (`PlanPicksComponent`) is the product-first planning matrix over the read-only
 *   `GET /api/pick-list` projection, with the local quantity overrides, completion marks and
 *   shortage visibility issue #996 added. It writes nothing at all.
 * - **Record refill** (`ManualPickListComponent`) is the manual catalogue entry that records what
 *   was actually taken out of storage, as one atomic, idempotent batch.
 *
 * Both are dedicated feature components rather than UI grown in this template
 * (docs/architecture.md § Page composition boundary): this page owns the route/query parameters,
 * the shared reads and their load states, the mode choice, and the navigation guard.
 *
 * **The whole of Plan picks is a live fleet read**, so without a usable provider there is nothing
 * to plan (issue #728). The machine list goes through `loadProviderList`: a provider that cannot
 * list machines leaves {@link machinesUnavailable} - one sentence in place of the planning view,
 * because "No machines selected yet" is a claim about this business's selection rather than about
 * Nayax - and a machine read that *failed* leaves {@link machinesError}, which says so instead of
 * presenting an empty selection as "this business has no machines". The site list
 * (`GET /api/sites/list`) is read the same way and is only ever used to group machine columns and
 * options by site: an empty or unavailable site read leaves the columns ungrouped rather than
 * inventing a site for a machine.
 *
 * **Record refill is offered with or without a provider**, and is the only view offered at all when
 * {@link calculatedUnavailableReason} is non-empty - no usable provider, or a stated refusal of the
 * `machineStock` permission the planning matrix's fan-out needs, checked up front and also caught
 * live if a selected machine's own product read is refused only once a snapshot is actually asked
 * for. The toggle to switch back to planning is then not offered at all: there is nothing to switch
 * to.
 *
 * **A mode switch never destroys Record refill, because it can hold an unconfirmed write.** What
 * protects it from recording a pick twice is the exact row set and idempotency key its component
 * keeps for a retry (see `ManualPickListComponent`). Destroying it would throw both away, so an
 * operator whose save ended uncertain could switch views, retype the same pick, and deduct the same
 * storage a second time under a new key. So the component is created once, the first time Record
 * refill is shown ({@link manualViewCreated}), and is **hidden rather than destroyed** afterwards -
 * the same treatment `MachineDetailComponent` gives its tab panels - and while a save is actually
 * in flight ({@link manualSaveInFlight}) switching away is refused outright, so no response can
 * land on a view the operator has already left. Plan picks is a read-only projection plus local
 * planning state, so its own state survives simply because nothing clears it.
 *
 * **Leaving the page is guarded** (issue #996). {@link confirmLeave} is the one rule behind both the
 * route's `canDeactivate` guard (`pickListUnsavedWorkGuard`) and the browser's own
 * `beforeunload` prompt, and it covers all three kinds of work this page holds: entered-but-unsaved
 * quantities, planning edits and checked picks, and - most seriously - an unresolved submission
 * whose retry context would be lost. A cancelled departure changes nothing: no state is touched by
 * asking. The browser's reload/close prompt cannot carry our wording, which is a native limitation,
 * so the sentences live on the in-app confirmation.
 *
 * **The active catalogue has its own load state** ({@link productsLoadState}), because Record refill
 * is built entirely from it: until the first read succeeds, an empty {@link allProducts} is "not
 * known yet" or "could not be read", never "this business has no products", and a failed refresh
 * after a recorded refill is reported rather than leaving stale storage quantities unlabelled. Both
 * failures are retried through {@link loadProducts}.
 *
 * **A machine-details page can open straight into a preselected Record refill** (issue #744's
 * machine-page consolidation, the owner's 18 October 2026 decision): its "Record refill" link is
 * this same route with `?mode=manual&machineId=<local id>`, read once, synchronously, from
 * `ActivatedRoute.snapshot.queryParamMap` in `ngOnInit` - a one-shot deep link, not a value this
 * page tracks across later navigation within itself. `mode=manual` only chooses the **starting**
 * view, so the toggle stays offered and a connected business can still switch to planning.
 * `machineId` is carried as {@link preselectedMachineId} into `ManualPickListComponent`'s own
 * `@Input` of the same name, which is the only place that resolves it against the business's actual
 * machines; this page parses nothing from it beyond "is it a number" so a malformed value
 * preselects no machine rather than a wrong one.
 */
@Component({
  selector: 'app-pick-list',
  standalone: true,
  imports: [CommonModule, ManualPickListComponent, PlanPicksComponent],
  templateUrl: './pick-list.component.html'
})
export class PickListComponent implements OnInit {
  allMachines: Machine[] = [];
  allProducts: Product[] = [];

  /** The business's sites, for grouping the planning matrix' machine columns and options. */
  sites: SiteListItem[] = [];

  /** The active catalogue reads: the first one, its retries, and the refresh after each refill. */
  readonly productsLoadState = new ListLoadState();

  /** Whether a catalogue read has ever succeeded, so a later failure is a failed refresh of real data rather than no data at all. */
  private productsEverLoaded = false;

  /** Which view is showing; see the class doc for when it is forced to `'manual'`. */
  mode: PickListMode = 'calculated';

  readonly planPicksViewName = PLAN_PICKS_VIEW_NAME;
  readonly recordRefillViewName = RECORD_REFILL_VIEW_NAME;
  readonly planPicksImpactMessage = PLAN_PICKS_IMPACT_MESSAGE;
  readonly recordRefillImpactMessage = RECORD_REFILL_IMPACT_MESSAGE;

  /**
   * Whether `ManualPickListComponent` exists yet. It is created the first time Record refill is
   * shown and then kept alive - hidden while the planning view is showing - so its typed quantities
   * and its retry key survive every later switch. A page that never records a refill never builds it.
   */
  manualViewCreated = false;

  /** Whether Record refill has a save outstanding; while it has, switching away from it is refused. */
  manualSaveInFlight = false;

  /** Whether Record refill holds typed quantities that have not been recorded. */
  manualEntriesPresent = false;

  /** Whether a refill save ended without an answer, so only its own retry may resolve it. */
  manualSubmissionUnresolved = false;

  /** Whether Plan picks holds edited quantities or checked picks. */
  planningWorkPresent = false;

  /** The one sentence saying Nayax has refused the `machineStock` permission the planning view needs; `''` while it has not. */
  machineProductsUnavailable = '';

  /** The one sentence for a provider that cannot list machines; `''` while it can. */
  machinesUnavailable = '';

  /** Set when the machine list could not be read for a reason that is not an availability answer. */
  machinesError = '';

  /** The "Record refill" link's `machineId` query parameter, or `null` for ordinary pick-list entry. */
  preselectedMachineId: number | null = null;

  constructor(
    private machineService: MachineService,
    private productService: ProductService,
    private siteService: SiteService,
    private toastService: ToastService,
    private provider: ProviderAvailabilityService,
    private route: ActivatedRoute
  ) {}

  ngOnInit(): void {
    const queryParams = this.route.snapshot.queryParamMap;
    if (queryParams.get('mode') === 'manual') {
      this.setMode('manual');
    }
    const linkedMachineId = Number(queryParams.get('machineId'));
    this.preselectedMachineId = queryParams.get('machineId') !== null && Number.isInteger(linkedMachineId) && linkedMachineId > 0
      ? linkedMachineId
      : null;

    loadProviderList(this.provider, 'machineFleet', () => this.machineService.getAll()).subscribe((outcome) => {
      this.allMachines = outcome.kind === 'listed' ? outcome.items : [];
      this.machinesUnavailable =
        outcome.kind === 'unavailable' ? nayaxUnavailableMessage(outcome.unavailability, 'The pick list') : '';
      this.machinesError =
        outcome.kind === 'failed' ? 'The machine list could not be loaded, so a pick list cannot be built.' : '';
      if (this.machinesUnavailable !== '') {
        this.setMode('manual');
      }
    });
    // The site list only groups machine columns and options, so anything other than a listed answer
    // simply leaves them ungrouped: the machine read above already says why Nayax data is missing,
    // and a machine must never be shown under a site this read did not confirm.
    loadProviderList(this.provider, 'machineFleet', () => this.siteService.getList()).subscribe((outcome) => {
      this.sites = outcome.kind === 'listed' ? outcome.items : [];
    });
    // Checked up front, beside the machine fleet above: a usable connection does not mean Nayax has
    // granted every read the planning matrix needs, and a permission this session already knows is
    // refused must open straight into Record refill rather than offering a matrix that will only fail.
    this.provider.featureAvailability('machineStock').subscribe((availability) => {
      if (availability === 'permissionNotGranted') {
        this.machineProductsUnavailable = nayaxUnavailableMessage(PERMISSION_NOT_GRANTED_REASON, PLAN_PICKS_VIEW_NAME);
        this.setMode('manual');
      }
    });
    this.loadProducts();
  }

  /**
   * Reads the active catalogue: on arrival, after each recorded refill, and whenever the operator
   * retries a failed read. A later read only replaces the products, so a refresh never resets what
   * the operator selected or typed.
   */
  loadProducts(): void {
    const token = this.productsLoadState.start();
    this.productService.getAll().subscribe({
      next: (products) => {
        if (this.productsLoadState.isCurrent(token)) {
          this.allProducts = products;
          this.productsEverLoaded = true;
        }
        this.productsLoadState.succeed(token);
      },
      error: () => {
        this.productsLoadState.fail(token);
        this.toastService.error(this.productsEverLoaded ? 'Failed to refresh the product list.' : 'Failed to load the product list.');
      }
    });
  }

  /** What Record refill may say about {@link allProducts}; see `ManualPickListCatalogueState`. */
  get catalogueState(): ManualPickListCatalogueState {
    if (this.productsEverLoaded) {
      return 'loaded';
    }
    return this.productsLoadState.loadFailed ? 'failed' : 'loading';
  }

  /** Whether the last catalogue read failed after an earlier one succeeded, so the storage quantities shown may be stale. */
  get catalogueRefreshFailed(): boolean {
    return this.productsEverLoaded && this.productsLoadState.loadFailed;
  }

  /**
   * The one sentence saying why Plan picks is not a real choice right now, or `''` while it is.
   * Non-empty forces {@link mode} to `'manual'` and hides the toggle back: there is nothing to
   * switch to.
   */
  get calculatedUnavailableReason(): string {
    return this.machinesUnavailable || this.machineProductsUnavailable;
  }

  /**
   * Switches the visible view. Switching to the planning view is refused once
   * {@link calculatedUnavailableReason} is set (there is nothing to switch to) and while
   * {@link manualSaveInFlight} is set (an outstanding save must land where the operator can see
   * what it did); the toggle button is disabled in both cases, and this is the same rule behind it.
   */
  switchMode(mode: PickListMode): void {
    if (mode === 'calculated' && (this.calculatedUnavailableReason !== '' || this.manualSaveInFlight)) {
      return;
    }
    this.setMode(mode);
  }

  /** Refreshes the active catalogue `ManualPickListComponent` reads, after it records a refill. */
  refreshProductsAfterManualRefill(): void {
    this.loadProducts();
  }

  /** Records whether Record refill has a save outstanding; see {@link switchMode}. */
  onManualSaveInFlightChange(inFlight: boolean): void {
    this.manualSaveInFlight = inFlight;
  }

  /** Records what Record refill would lose if the page were left; see {@link confirmLeave}. */
  onManualUnsavedWorkChange(work: ManualPickUnsavedWork): void {
    this.manualEntriesPresent = work.entriesPresent;
    this.manualSubmissionUnresolved = work.unresolvedSubmission;
  }

  /** Records whether Plan picks holds edited or checked work; see {@link confirmLeave}. */
  onPlanningWorkChange(present: boolean): void {
    this.planningWorkPresent = present;
  }

  /**
   * What a failed snapshot read means for the *availability* of planning at all. The permission
   * evidence read in `ngOnInit` is this session's first answer, but a session often proves the
   * `machineStock` refusal only here - the moment a selected machine's own product read is actually
   * asked for - so the refusal is noted and planning withdrawn, exactly as it was before issue #996
   * moved the matrix into its own component. A failure that is **not** one of the published Nayax
   * availability codes (a transport failure, an unrelated `5xx`) says nothing about whether planning
   * could work, so it stays the generic toast beside the view's own Retry.
   */
  onPlanLoadFailed(error: unknown): void {
    const unavailability = this.provider.noteRefusal(error, 'machineStock');
    if (unavailability !== null) {
      this.machineProductsUnavailable = nayaxUnavailableMessage(unavailability, PLAN_PICKS_VIEW_NAME);
      this.setMode('manual');
      return;
    }
    this.toastService.error('Failed to load the pick list.');
  }

  /**
   * The one sentence to warn a departure with, or `null` when there is nothing on this page that
   * leaving would lose. An unresolved or in-flight submission comes first: it is the only one of
   * the three where leaving also throws away the retry that could finish it safely.
   */
  get unsavedWorkWarning(): string | null {
    if (this.manualSubmissionUnresolved || this.manualSaveInFlight) {
      return LEAVE_WITH_UNRESOLVED_SUBMISSION_WARNING;
    }
    if (this.manualEntriesPresent) {
      return LEAVE_WITH_ENTERED_QUANTITIES_WARNING;
    }
    if (this.planningWorkPresent) {
      return LEAVE_WITH_PLANNING_WORK_WARNING;
    }
    return null;
  }

  /**
   * Whether the page may be left. Asking changes nothing at all, so a cancelled departure leaves
   * every entry, override, mark and kept submission exactly as it was. The route's `canDeactivate`
   * guard is its one caller for in-app navigation.
   */
  confirmLeave(): boolean {
    const warning = this.unsavedWorkWarning;
    return warning === null || window.confirm(warning);
  }

  /**
   * The browser's own reload/close prompt. A browser will not show our sentence - it shows its own
   * generic wording, and only after an interaction - which is a native limitation, not a choice:
   * the in-app confirmation above is where the explanation lives.
   */
  @HostListener('window:beforeunload', ['$event'])
  onBeforeUnload(event: BeforeUnloadEvent): void {
    if (this.unsavedWorkWarning !== null) {
      event.preventDefault();
      event.returnValue = true;
    }
  }

  /** The one place `mode` is written, so entering Record refill always creates the view that must then survive. */
  private setMode(mode: PickListMode): void {
    this.mode = mode;
    if (mode === 'manual') {
      this.manualViewCreated = true;
    }
  }
}
