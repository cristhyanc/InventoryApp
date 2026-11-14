import { Component, OnDestroy, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { HttpErrorResponse } from '@angular/common/http';
import { Subscription, catchError, of, tap } from 'rxjs';
import { SupplierService } from '../../services/supplier.service';
import { ToastService } from '../../services/toast.service';
import { AvailableSupplierPreset, Supplier } from '../../models/models';
import { SupplierGstDefaultsDialogComponent } from './supplier-gst-defaults-dialog/supplier-gst-defaults-dialog.component';
import { SupplierFormDialogComponent } from './supplier-form-dialog/supplier-form-dialog.component';
import { ConfirmationDialogComponent } from '../shared/confirmation-dialog.component';
import { handOffFocusFromRemovedOpener } from '../shared/confirmation-focus';
import { ActionMenuComponent } from '../shared/action-menu/action-menu.component';
import { IconComponent } from '../shared/icon.component';
import { ListLoadState } from '../shared/list-load-state';
import { RolePreviewService } from '../../role-preview/role-preview.service';
import { SupportSessionService } from '../../support-session/support-session.service';
import { isSafeSupplierUrl, supplierUrlDomain } from './supplier-url';
import { supplierRefusal } from './supplier-refusal';
import { plural } from '../../formatting/plural';

/** The Suppliers toolbar's usage filter (issue #1175): "Not used" is the existing `isHidden` switch. */
export type SupplierUsageFilter = 'all' | 'inUse' | 'notUsed';

@Component({
  selector: 'app-supplier-list',
  standalone: true,
  imports: [
    CommonModule,
    FormsModule,
    SupplierGstDefaultsDialogComponent,
    SupplierFormDialogComponent,
    ConfirmationDialogComponent,
    ActionMenuComponent,
    IconComponent
  ],
  templateUrl: './supplier-list.component.html'
})
export class SupplierListComponent implements OnInit, OnDestroy {
  suppliers: Supplier[] = [];
  readonly loadState = new ListLoadState();

  /** The register's own name search (issue #1175): case-insensitive, over the loaded list only. */
  search = '';

  /** The register's own usage filter (issue #1175), combined with {@link search}. */
  usageFilter: SupplierUsageFilter = 'all';

  /**
   * The suppliers from the platform's list this business may add (issue #935): the ones that serve
   * its state and that it has no copy of. The server decides both, and an empty list is the normal
   * state - a business that has taken up every suggestion for its state is offered nothing.
   */
  availablePresets: AvailableSupplierPreset[] = [];

  /**
   * Whether the Add/Edit dialog is open (issue #1174), and for which supplier: `editing` is `null`
   * for an Add. The form itself - its values, its validation, its save and its discard question -
   * belongs to `SupplierFormDialogComponent`; this page only says which supplier it is open for.
   */
  formOpen = false;

  editing: Supplier | null = null;

  /** The supplier whose GST defaults dialog is open (issue #1174), or `null` while none is. */
  gstDefaultsFor: Supplier | null = null;

  /** The supplier the delete confirmation is asking about (issue #1174), or `null` while none is. */
  deleting: Supplier | null = null;

  /**
   * What the server refused, verbatim, or `null` when nothing is being reported (issue #934). The
   * refusals a linked supplier produces - a delete, a "Not used" switch it does not allow - are
   * sentences written for the operator, so they are shown rather than replaced with a message of
   * this page's own. A refused *save* is reported inside the dialog, on the form it belongs to.
   */
  error: string | null = null;

  /**
   * Whether a support session is active (issue #657, extending #554): the header's **Add
   * supplier** action, the dialog's Save and the row's Delete are blocked while it is, because a
   * session grants `PurchasingOperate` - the capability this page would otherwise gate those on -
   * and the backend refuses the write itself only by its read-only method rule, never by
   * withholding the capability. The "Not used" switch
   * of issue #934 is on the same capability and is hidden for exactly the same reason. See
   * `SupportSessionService.active$`.
   */
  sessionActive = false;

  /**
   * Whether this tab is previewing the application as another role (issue #721). It hides the
   * **Add** action of issue #935 for the same reason `sessionActive` blocks the writes above: a
   * previewed request is read-only server-side, so the action would render and then be refused.
   */
  previewActive = false;

  private readonly subscriptions = new Subscription();

  constructor(
    private readonly supplierService: SupplierService,
    private readonly toasts: ToastService,
    supportSession: SupportSessionService,
    rolePreview: RolePreviewService
  ) {
    this.subscriptions.add(supportSession.active$.subscribe((active) => (this.sessionActive = active)));
    this.subscriptions.add(rolePreview.active$.subscribe((active) => (this.previewActive = active)));
  }

  ngOnInit(): void {
    this.load();
  }

  ngOnDestroy(): void {
    this.subscriptions.unsubscribe();
  }

  load(): void {
    // The one request in the application that asks for the suppliers marked "Not used" (issue
    // #934): this page is where one is switched back on, which is impossible if it is not shown.
    const token = this.loadState.start();
    this.supplierService
      .getAllIncludingHidden()
      .pipe(
        tap((suppliers) => {
          if (this.loadState.isCurrent(token)) {
            this.suppliers = suppliers;
          }
          this.loadState.succeed(token);
        }),
        catchError(() => {
          this.loadState.fail(token);
          return of([] as Supplier[]);
        })
      )
      .subscribe();

    // And what the business may still add from the platform's list (issue #935). It is read on
    // every load, so adding one - or acquiring one through a state change in another tab - leaves
    // this section showing what is actually left.
    this.supplierService.getAvailablePresets().subscribe((presets) => (this.availablePresets = presets));
  }

  /** "7 suppliers", counted from the whole loaded register regardless of the search or usage filter. */
  get supplierCountLabel(): string {
    return plural(this.suppliers.length, 'supplier');
  }

  get hasSearch(): boolean {
    return this.search.trim().length > 0;
  }

  /** Whether the search or the usage filter differs from its default, the only time either may be cleared. */
  get filtersDiffer(): boolean {
    return this.hasSearch || this.usageFilter !== 'all';
  }

  /**
   * The rows on screen (issue #1175): the name search and the All/In use/Not used filter applied
   * together, client-side over the one register already loaded (unpaginated, and this issue adds
   * none).
   */
  get filteredSuppliers(): Supplier[] {
    return this.suppliers.filter((supplier) => this.matchesFilters(supplier));
  }

  clearSearch(): void {
    this.search = '';
  }

  setUsageFilter(filter: SupplierUsageFilter): void {
    this.usageFilter = filter;
  }

  /** The no-match state's own action: back to every supplier, with no search text. */
  resetSearchAndFilter(): void {
    this.search = '';
    this.usageFilter = 'all';
  }

  /**
   * Whether this row counts as "Not used" (issue #1175's usage filter and column). Usage is the
   * existing `isHidden` switch, never purchase history - and that switch is only ever offered for
   * a linked supplier (issue #934), so a supplier the business added itself is always "In use"
   * whatever a stale `isHidden` might otherwise hold.
   */
  isNotUsed(supplier: Supplier): boolean {
    return this.isLinked(supplier) && supplier.isHidden === true;
  }

  /**
   * Whether the **Add** action is offered (issue #935). It is hidden in a read-only support session
   * and in a role preview, both of which grant the capability this page is gated on and then refuse
   * every write by their own method rule - so the server refuses the add in both cases whatever
   * this renders.
   */
  get canAddFromPlatformList(): boolean {
    return !this.sessionActive && !this.previewActive;
  }

  /** Whether this supplier's stored website may be rendered as a link (issue #847). */
  canLinkTo(supplier: Supplier): boolean {
    return isSafeSupplierUrl(supplier.url);
  }

  /**
   * The same rule for a platform record's website (issue #935). It is the one website rule this
   * page has, asked of the other shape rather than copied: a value that is not an http(s) address
   * is shown as plain text and is never clickable, wherever it came from.
   */
  canLinkToPreset(preset: AvailableSupplierPreset): boolean {
    return isSafeSupplierUrl(preset.url);
  }

  /**
   * The Website column's short form (issue #1175): the bare domain a safe stored URL points at.
   * Read only where the template has already checked {@link canLinkTo}, so the value is never
   * `null` or unparsable here.
   */
  websiteDomain(supplier: Supplier): string {
    return supplierUrlDomain(supplier.url as string);
  }

  /**
   * Whether this supplier came from the platform's supplier list (issue #934). The server decides
   * it; an older response that says nothing is treated as "not linked", which is what every
   * supplier was before this issue.
   */
  isLinked(supplier: Supplier): boolean {
    return supplier.isLinked === true;
  }

  /**
   * Whether the header's **Add supplier** action is offered (issues #657 and #1174). It is hidden
   * in a read-only support session, like every other write this page offers: a session grants
   * `PurchasingOperate` - the capability a create is gated on - and then refuses the write by its
   * own method rule, so an Add dialog opened in a session could only ever end in a refusal. Edit
   * is a different matter and stays available; see `editSupplier`. A role preview is deliberately
   * not read here: it leaves this form exactly as the inline one had it (issue #721 unchanged),
   * unlike the **Add** of issue #935 above, which that preview already hid before this issue.
   */
  get canAddSupplier(): boolean {
    return !this.sessionActive;
  }

  /**
   * Opens the dialog on an empty form. Guarded here as well as hidden in the template, so the
   * create is actually unavailable rather than merely invisible.
   */
  addSupplier(): void {
    if (!this.canAddSupplier) return;

    this.editing = null;
    this.formOpen = true;
    this.error = null;
  }

  /**
   * Opens the dialog on this supplier's stored values. It is offered during a support session, as
   * it was before issue #1174: opening the form issues no request, so a session may still read a
   * supplier's stored details here, and the dialog's own Save is what the session disables.
   */
  editSupplier(supplier: Supplier): void {
    this.editing = supplier;
    this.formOpen = true;
    this.error = null;
  }

  /** Dismissed, or discarded: the dialog goes and nothing was written. Search, filter and scroll stay. */
  closeForm(): void {
    this.formOpen = false;
    this.editing = null;
  }

  /**
   * A create or update that actually succeeded. The list is re-read and the toast shown only here,
   * so neither can describe a save the server refused. Since issue #1175 the dialog reports what it
   * saved, which is read only to say whether the current search or usage filter now hides that very
   * row (issue #1175's "saved while filtered" message) - the search and filter themselves are left
   * exactly as they were, so a saved supplier that the filter hides stays hidden rather than being
   * sprung back into view.
   */
  onSupplierSaved(saved: Supplier): void {
    const created = this.editing === null;
    this.closeForm();
    this.load();

    const base = created ? 'Supplier created.' : 'Supplier saved.';
    this.toasts.success(
      this.matchesFilters(saved) ? base : `${base} It is hidden by your current search or usage filter.`
    );
  }

  /** Opens this supplier's GST defaults (issue #430) in their own dialog. */
  openGstDefaults(supplier: Supplier): void {
    this.gstDefaultsFor = supplier;
  }

  closeGstDefaults(): void {
    this.gstDefaultsFor = null;
  }

  /**
   * Asks about deleting this supplier through the shared confirmation dialog. Guarded here as well
   * as hidden in the template, so a question the server would refuse is never even asked.
   */
  askDelete(supplier: Supplier): void {
    if (this.sessionActive) return;
    if (supplier.canDelete === false) return;

    this.deleting = supplier;
  }

  cancelDelete(): void {
    this.deleting = null;
  }

  /**
   * `opener` is the control the confirmation returned focus to as it closed, which keeps focus while
   * the delete is in flight so a refusal leaves it on the row it is about. A success re-reads the
   * list without that row, so focus is handed on rather than dropped on the document body
   * (issue #1277).
   */
  confirmDelete(opener: HTMLElement | null = null): void {
    const supplier = this.deleting;
    this.deleting = null;
    if (supplier === null) {
      return;
    }

    this.remove(supplier, opener);
  }

  remove(supplier: Supplier, opener: HTMLElement | null = null): void {
    // Checked here as well as hidden in the template, so the write is actually unavailable rather
    // than merely invisible - the endpoint refuses it either way.
    if (this.sessionActive) return;
    if (supplier.canDelete === false) return;

    this.error = null;
    this.supplierService.delete(supplier.id).subscribe({
      next: () => {
        this.load();
        handOffFocusFromRemovedOpener(opener);
      },
      error: (response: HttpErrorResponse) =>
        (this.error = supplierRefusal(response, 'Failed to delete the supplier.'))
    });
  }

  /** The overflow menu's own label for the "Not used" switch (issue #1175), naming what it will do. */
  usageToggleLabel(supplier: Supplier): string {
    return this.isNotUsed(supplier) ? 'Mark as in use' : 'Mark as not used';
  }

  /**
   * Sets or clears "Not used" on a linked supplier (issue #934). The row stays either way: what
   * changes is whether a purchase, a supplier order or a product's order dialog offers it.
   */
  setNotUsed(supplier: Supplier, isHidden: boolean): void {
    if (this.sessionActive) return;
    if (!this.isLinked(supplier)) return;

    this.error = null;
    this.supplierService.setNotUsed(supplier.id, isHidden).subscribe({
      next: () => this.load(),
      error: (response: HttpErrorResponse) => {
        this.error = supplierRefusal(response, 'Failed to change "Not used".');
        // The switch shows the stored state, so a refused change must not leave it showing the
        // state the server rejected.
        this.load();
      }
    });
  }

  /**
   * Adds one supplier from the platform's list as this business's own linked copy (issue #935).
   *
   * The request carries the record's id alone: its name and website are the platform's, and the
   * business is the caller's own. A refusal is shown verbatim - the server's "already added"
   * sentence for a record a second tab has just added - and the lists are reloaded either way, so
   * what is offered afterwards is what the server says is offered.
   */
  addFromPreset(preset: AvailableSupplierPreset): void {
    // Checked here as well as hidden in the template, so the write is actually unavailable rather
    // than merely invisible - the endpoint refuses it either way.
    if (!this.canAddFromPlatformList) return;

    this.error = null;
    this.supplierService.addFromPreset(preset.id).subscribe({
      next: () => this.load(),
      error: (response: HttpErrorResponse) => {
        this.error = supplierRefusal(response, `Failed to add "${preset.name}".`);
        this.load();
      }
    });
  }

  /** The search and usage filter applied together (issue #1175), read by both the list and the save toast. */
  private matchesFilters(supplier: Supplier): boolean {
    const term = this.search.trim().toLowerCase();
    if (term && !supplier.name.toLowerCase().includes(term)) {
      return false;
    }
    if (this.usageFilter === 'inUse' && this.isNotUsed(supplier)) {
      return false;
    }
    if (this.usageFilter === 'notUsed' && !this.isNotUsed(supplier)) {
      return false;
    }
    return true;
  }
}
