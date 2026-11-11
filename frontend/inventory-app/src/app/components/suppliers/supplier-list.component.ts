import { Component, OnDestroy, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { Subscription } from 'rxjs';
import { SupplierService } from '../../services/supplier.service';
import { ToastService } from '../../services/toast.service';
import { AvailableSupplierPreset, Supplier } from '../../models/models';
import { SupplierGstDefaultsDialogComponent } from './supplier-gst-defaults-dialog/supplier-gst-defaults-dialog.component';
import { SupplierFormDialogComponent } from './supplier-form-dialog/supplier-form-dialog.component';
import { ConfirmationDialogComponent } from '../shared/confirmation-dialog.component';
import { IconComponent } from '../shared/icon.component';
import { RolePreviewService } from '../../role-preview/role-preview.service';
import { SupportSessionService } from '../../support-session/support-session.service';
import { isSafeSupplierUrl } from './supplier-url';
import { supplierRefusal } from './supplier-refusal';

@Component({
  selector: 'app-supplier-list',
  standalone: true,
  imports: [
    CommonModule,
    SupplierGstDefaultsDialogComponent,
    SupplierFormDialogComponent,
    ConfirmationDialogComponent,
    IconComponent
  ],
  templateUrl: './supplier-list.component.html'
})
export class SupplierListComponent implements OnInit, OnDestroy {
  suppliers: Supplier[] = [];

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
    this.supplierService.getAllIncludingHidden().subscribe((s) => (this.suppliers = s));

    // And what the business may still add from the platform's list (issue #935). It is read on
    // every load, so adding one - or acquiring one through a state change in another tab - leaves
    // this section showing what is actually left.
    this.supplierService.getAvailablePresets().subscribe((presets) => (this.availablePresets = presets));
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
   * unlike the **Add** of issue #935 below, which that preview already hid before this issue.
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

  /** Dismissed, or discarded: the dialog goes and nothing was written. */
  closeForm(): void {
    this.formOpen = false;
    this.editing = null;
  }

  /**
   * A create or update that actually succeeded. The list is re-read and the toast shown only here,
   * so neither can describe a save the server refused.
   */
  onSupplierSaved(): void {
    const created = this.editing === null;
    this.closeForm();
    this.load();
    this.toasts.success(created ? 'Supplier created.' : 'Supplier saved.');
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

  confirmDelete(): void {
    const supplier = this.deleting;
    this.deleting = null;
    if (supplier === null) {
      return;
    }

    this.remove(supplier);
  }

  remove(supplier: Supplier): void {
    // Checked here as well as hidden in the template, so the write is actually unavailable rather
    // than merely invisible - the endpoint refuses it either way.
    if (this.sessionActive) return;
    if (supplier.canDelete === false) return;

    this.error = null;
    this.supplierService.delete(supplier.id).subscribe({
      next: () => this.load(),
      error: (response: HttpErrorResponse) =>
        (this.error = supplierRefusal(response, 'Failed to delete the supplier.'))
    });
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
}
