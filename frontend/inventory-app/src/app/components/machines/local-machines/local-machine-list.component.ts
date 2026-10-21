import { HttpErrorResponse } from '@angular/common/http';
import { CommonModule } from '@angular/common';
import { Component, OnDestroy, OnInit } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Observable, Subscription } from 'rxjs';
import { LocalMachine } from '../../../models/models';
import { LocalMachineService } from '../../../services/local-machine.service';
import { SupportSessionService } from '../../../support-session/support-session.service';
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
 * child G1): add a machine, rename one, take one out of service, put it back.
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
 * <p><strong>No delete.</strong> Stored sales, refills, counts and expenses refer to a machine, so a
 * machine that stops trading is deactivated and stays listed. The API offers no delete either.</p>
 *
 * <p>Write controls are hidden while a support session is active, on the established rule (issues
 * #554, #658): the session grants <code>Inventory.Operate</code>, which is what this surface would
 * otherwise gate them on, and the backend refuses the write by its read-only method rule rather than
 * by withholding the capability.</p>
 */
@Component({
  selector: 'app-local-machine-list',
  standalone: true,
  imports: [CommonModule, FormsModule, IconComponent],
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
    if (this.sessionActive || this.saving) return;
    this.write(this.machines$.deactivate(machine.id));
  }

  reactivate(machine: LocalMachine): void {
    if (this.sessionActive || this.saving) return;
    this.write(this.machines$.reactivate(machine.id));
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
