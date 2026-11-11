import { Component, EventEmitter, Input, Output } from '@angular/core';
import { Supplier } from '../../../models/models';
import { SupplierGstDefaultsComponent } from '../supplier-gst-defaults/supplier-gst-defaults.component';
import { ModalDialogComponent } from '../../shared/modal-dialog/modal-dialog.component';

/**
 * The GST defaults dialog (issue #1174): the existing, unchanged `SupplierGstDefaultsComponent`
 * composed into the shared dialog shell.
 *
 * It is a dedicated feature component rather than markup authored inside
 * `SupplierListComponent`'s own template, per the page composition boundary
 * (docs/architecture.md § Page composition boundary): the page only says which supplier the
 * dialog is open for, through `[supplier]`, and reacts to `(closed)`. The panel's own read, save,
 * messages and `Suppliers.ManageGstDefaults` permission stay entirely inside
 * `SupplierGstDefaultsComponent`, untouched by this wrapper.
 */
@Component({
  selector: 'app-supplier-gst-defaults-dialog',
  standalone: true,
  imports: [SupplierGstDefaultsComponent, ModalDialogComponent],
  templateUrl: './supplier-gst-defaults-dialog.component.html'
})
export class SupplierGstDefaultsDialogComponent {
  /** The supplier whose GST defaults are open. */
  @Input({ required: true }) supplier!: Supplier;

  /** Reported when the dialog should close: Escape, the backdrop, or the Close control. */
  @Output() readonly closed = new EventEmitter<void>();
}
