import { HttpErrorResponse } from '@angular/common/http';
import { CommonModule } from '@angular/common';
import { Component, OnDestroy, OnInit } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Observable, Subscription } from 'rxjs';
import { LocalMachine } from '../../../models/models';
import { LocalMachineService } from '../../../services/local-machine.service';
import { SupportSessionService } from '../../../support-session/support-session.service';
import { ConfirmationDialogComponent } from '../../shared/confirmation-dialog.component';
import { IconComponent } from '../../shared/icon.component';
import { ListLoadState } from '../../shared/list-load-state';

/** What an operator is told when a write failed for a reason the server did not word itself. */
export const GENERIC_MACHINE_WRITE_ERROR = 'That could not be saved. Try again, or reload the page.';

/**
 * The refusal the API worded, or the generic sentence. `POST`/`PUT /api/local-machines` answer a
 * refusal as the message itself (a bare string); a problem-details body is read too, because the
 * global exception handler words its own.
 */
export function machineWriteErrorMessage(error: unknown): string {
  if (!(error instanceof HttpErrorResponse)) {
    return GENERIC_MACHINE_WRITE_ERROR;
  }

  if (error.status === 400) {
    const body: unknown = error.error;
    if (typeof body === 'string' && body.trim().length > 0) {
      return body;
    }

    const detail = (body as { message?: unknown } | null)?.message;
    if (typeof detail === 'string' && detail.trim().length > 0) {
      return detail;
    }
  }

  return GENERIC_MACHINE_WRITE_ERROR;
}

/**
 * The business's own machine records, with the actions that maintain them (issue #740, parent #329
 * child G1): add a machine, rename one, take one out of service, put it back. A machine the
 * provider names offers neither rename nor deactivation - it is managed at the provider and the API
 * refuses both - but can still be put back in service.
 *
 * <p>It is a dedicated feature component rather than more template on the Machines page, as the page
 * composition boundary requires (docs/architecture.md § Page composition boundary, issue #191): the
 * list, its load state, the add and rename drafts, the refusal message and every write live here,
 * and <code>MachineListComponent</code> only composes it beside the live fleet.</p>
 *
 * <p><strong>It needs no sales provider.</strong> Everything on it reads and writes
 * <code>/api/local-machines</code>, so it renders and works identically for a business that has
 * never connected one - which is the point of the issue. It shows no revenue, no stock and no sync:
 * those are the live fleet's, and the page withholds them itself.</p>
 *
 * <p><strong>Delete is only for a machine added by mistake</strong> (issue #896). A machine with
 * machine stock counts, stock movements, pick-list refills or operating expenses is deactivated and
 * stays listed, because that history refers to it; so is a machine the provider names. Which is
 * which is the server's answer, published as <code>canDelete</code>/<code>deletionRefusal</code>
 * from the one deletion policy - this component offers Delete only on a plain <code>true</code> and
 * shows the server's refusal for the rest, rather than deciding anything itself.</p>
 *
 * <p>Write controls are hidden while a support session is active, on the established rule (issues
 * #554, #658): the session grants <code>Inventory.Operate</code>, which is what this surface would
 * otherwise gate them on, and the backend refuses the write by its read-only method rule rather than
 * by withholding the capability.</p>
 */
@Component({
  selector: 'app-local-machine-list',
  standalone: true,
  imports: [CommonModule, FormsModule, IconComponent, ConfirmationDialogComponent],
  templateUrl: './local-machine-list.component.html'
})
export class LocalMachineListComponent implements OnInit, OnDestroy {
  machines: LocalMachine[] = [];
  readonly loadState = new ListLoadState();

  /** The name being typed into the add form. */
  newName = '';

  /** The machine being renamed, and its draft name. */
  editingId: number | null = null;
  editingName = '';

  /** The machine whose deletion is awaiting confirmation, which is what renders the dialog. */
  confirmingMachine: LocalMachine | null = null;

  /** The last refusal, worded by the server where it worded one. */
  error: string | null = null;

  /** True while a write is outstanding, so a form cannot be submitted twice. */
  saving = false;

  /** Whether a support session is active: every write control is hidden while it is. */
  sessionActive = false;

  private readonly sessionActiveSubscription: Subscription;

  constructor(
    private readonly machines$: LocalMachineService,
    supportSession: SupportSessionService
  ) {
    this.sessionActiveSubscription = supportSession.active$.subscribe((active) => (this.sessionActive = active));
  }

  ngOnInit(): void {
    this.load();
  }

  ngOnDestroy(): void {
    this.sessionActiveSubscription.unsubscribe();
  }

  load(): void {
    const token = this.loadState.start();
    this.machines$.getAll().subscribe({
      next: (machines) => {
        if (this.loadState.isCurrent(token)) {
          this.machines = machines;
        }
        this.loadState.succeed(token);
      },
      error: () => this.loadState.fail(token)
    });
  }

  add(): void {
    // Checked here as well as hidden in the template, so the write is actually unavailable rather
    // than merely invisible - the endpoint refuses it either way.
    if (this.sessionActive || this.saving) return;
    if (!this.newName.trim()) {
      this.error = 'Machine name is required.';
      return;
    }

    this.write(this.machines$.create(this.newName.trim()), () => (this.newName = ''));
  }

  startRename(machine: LocalMachine): void {
    // A machine from the provider is renamed there; the API refuses it either way.
    if (machine.isProviderNamed) return;
    this.editingId = machine.id;
    this.editingName = machine.name;
    this.error = null;
  }

  cancelRename(): void {
    this.editingId = null;
    this.editingName = '';
    this.error = null;
  }

  saveRename(): void {
    if (this.sessionActive || this.saving || this.editingId === null) return;
    if (!this.editingName.trim()) {
      this.error = 'Machine name is required.';
      return;
    }

    this.write(this.machines$.rename(this.editingId, this.editingName.trim()), () => this.cancelRename());
  }

  deactivate(machine: LocalMachine): void {
    // A machine from the provider is taken out of service there; the API refuses it either way.
    if (this.sessionActive || this.saving || machine.isProviderNamed) return;
    this.write(this.machines$.deactivate(machine.id));
  }

  reactivate(machine: LocalMachine): void {
    if (this.sessionActive || this.saving) return;
    this.write(this.machines$.reactivate(machine.id));
  }

  /**
   * Whether the API says this machine may be deleted (issue #896). `canDelete` is the deletion
   * rule's own answer, published by the machine list from the same
   * `Inventory.Domain.Machines.MachineDeletionPolicy` that refuses the delete itself: a provider
   * link, machine stock counts, stock movements, pick-list refills and operating expenses all reach
   * it. Delete is offered only on a plain `true` - an absent answer (a response that never resolved
   * the question) is not permission - and never for a machine the provider names, which is managed
   * at the provider exactly as its rename and deactivation are.
   *
   * The answer stays advisory - it is as fresh as the last fetch - and the server decides again
   * inside the delete's own transaction, which `deleteConfirmed` still surfaces.
   */
  canDelete(machine: LocalMachine): boolean {
    return machine.canDelete === true && !machine.isProviderNamed;
  }

  /**
   * Why this machine may not be deleted, in the server's own words, for a machine the business
   * maintains itself. A provider machine is left out on purpose: the row already says "From Nayax"
   * and offers neither rename nor deactivation, so repeating the sentence would only crowd it.
   */
  deletionRefusal(machine: LocalMachine): string | null {
    if (machine.isProviderNamed || machine.canDelete !== false) return null;
    return machine.deletionRefusal ?? null;
  }

  confirmDelete(machine: LocalMachine): void {
    // Checked here as well as hidden in the template, so the write is actually unavailable rather
    // than merely invisible - the endpoint refuses it either way.
    if (this.sessionActive || this.saving || !this.canDelete(machine)) return;
    this.error = null;
    this.confirmingMachine = machine;
  }

  cancelDelete(): void {
    // No request was made and none is made now: cancelling leaves the row exactly as it was.
    this.confirmingMachine = null;
  }

  /**
   * Sends the one delete. The machine being confirmed is cleared first, so a second click on the
   * dialog's button has nothing left to send even before `saving` closes the window.
   */
  deleteConfirmed(): void {
    const machine = this.confirmingMachine;
    this.confirmingMachine = null;
    if (machine === null || this.sessionActive || this.saving) return;

    // The refusal the server words is shown as it is: Delete was offered because `canDelete` said
    // so, but that answer was read before this click, and the machine can have gained an expense, a
    // count, a movement, a refill or a provider link in between. `write` reloads only on success,
    // so the row a refusal is about is still there.
    this.write(this.machines$.delete(machine.id));
  }

  /**
   * Runs one write, then reloads the list from the server rather than patching the local copy: the
   * list is what the server stored, and a refusal must not leave the table claiming a change that
   * was not made.
   */
  private write(request: Observable<unknown>, onSuccess?: () => void): void {
    this.saving = true;
    this.error = null;
    request.subscribe({
      next: () => {
        this.saving = false;
        onSuccess?.();
        this.load();
      },
      error: (failure: unknown) => {
        this.saving = false;
        this.error = machineWriteErrorMessage(failure);
      }
    });
  }
}
