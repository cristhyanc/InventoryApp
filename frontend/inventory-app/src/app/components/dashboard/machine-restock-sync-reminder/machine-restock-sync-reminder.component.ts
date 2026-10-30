import { CommonModule } from '@angular/common';
import { Component, OnDestroy } from '@angular/core';
import { RouterLink } from '@angular/router';
import { Subscription } from 'rxjs';
import { AccessService } from '../../../access/access.service';
import { BusinessDateTimePipe } from '../../../formatting/business-date-time.pipe';
import {
  MachineRestockSyncReminder,
  MachineRestockSyncReminderMachine,
  isMachineRestockSyncReminderApplicable
} from '../../../models/models';
import { MachineService } from '../../../services/machine.service';
import { IconComponent } from '../../shared/icon.component';
import { SYNC_RESTOCK_OPEN, SYNC_RESTOCK_QUERY_PARAM } from '../../machines/machine-restock-sync/sync-restock-link';
import { SupportSessionService } from '../../../support-session/support-session.service';

/**
 * Where the reminder read has got to (issue #891). The five are answers to different questions and
 * must never stand in for one another: nothing has been asked (`idle` - the capability is
 * unanswered or refused, or a support session is active), the answer is outstanding (`loading`),
 * the answer arrived and this business is shown a reminder (`loaded`), the answer arrived and this
 * business is shown none at all (`hidden`), and the request failed (`failed`).
 *
 * Only `loaded` may show "nothing needs attention" - by showing nothing - because that is a
 * statement about machines, and a request nobody answered is not evidence for it. `failed` says so
 * out loud instead.
 */
export type RestockSyncReminderState = 'idle' | 'loading' | 'loaded' | 'hidden' | 'failed';

/**
 * The Dashboard's machine restock-sync reminder (issue #891), composed into the Dashboard as its
 * own component: it owns its capability decision, its one request, its loading/failed lifecycle and
 * its own wording, so the Dashboard page only has to place it.
 *
 * <p>**It reminds and nothing else.** There is no Sync Restock action here, no apply, no duplicate
 * resolution and no acknowledgement: each row links to the machine's own page, where the Sync
 * Restock dialog lives, and opening this banner or following one of its links leaves every pending
 * event exactly as pending as it was. The backend endpoint is a `GET` that makes no Nayax request
 * and writes nothing.</p>
 *
 * <p>**Who sees it.** `Inventory.Operate`, the capability Sync Restock itself requires: a role that
 * may not reconcile restocks has nothing to do with a reminder to go and reconcile them. Like
 * `LegacyBusinessSettingsPromptComponent`, and unlike `NayaxStatusBannerComponent`'s fail-open
 * default, it fetches nothing while the capability is not affirmatively granted - the read is about
 * machine restock work, not something every member needs to render the page.</p>
 *
 * <p>**Not in a support session.** A session resolves no Nayax credentials and may take no action
 * (issue #551), so a reminder to go and sync would name work its reader cannot do. The backend
 * withholds it in that state too; this simply does not ask.</p>
 *
 * <p>**Where it sits.** Since issue #972 it is one alert in the Dashboard's Needs attention panel,
 * which reads {@link isShown} and {@link state} to decide whether the panel has anything to show
 * and how many alerts to count. Its eligibility, window and links are unchanged.</p>
 *
 * <p>**A failed read is visible.** The alternative - rendering nothing - would read as "no machine
 * needs attention", which is the one thing a failed read cannot say.</p>
 */
@Component({
  selector: 'app-machine-restock-sync-reminder',
  standalone: true,
  imports: [CommonModule, RouterLink, BusinessDateTimePipe, IconComponent],
  // No box of its own (issue #972): the Dashboard places it as one alert in its Needs attention
  // grid, so the rendered alert - or nothing at all - is the grid item.
  host: { class: 'contents' },
  templateUrl: './machine-restock-sync-reminder.component.html'
})
export class MachineRestockSyncReminderComponent implements OnDestroy {
  /** See {@link RestockSyncReminderState}. */
  state: RestockSyncReminderState = 'idle';

  /** The reminder as the API published it, or `null` while there is none to show. */
  reminder: MachineRestockSyncReminder | null = null;

  /**
   * Whether the signed-in member's role may reach the Nayax connection wizard, which decides
   * whether the unconfirmed-permission note offers the link or asks for an Owner. It starts
   * `true`, the same fail-open default `NayaxStatusBannerComponent` uses: the route is
   * independently guarded by the same capability, so a stale `true` only ever offers a link a
   * Manager or Operator could not follow, never a boundary.
   */
  canManageIntegration = true;

  /** The sentence shown in place of the banner when the read failed. */
  readonly unavailableMessage =
    'Machine restock sync status could not be loaded, so it is unknown rather than up to date. '
    + 'Reload the Dashboard, or open a machine and use Sync Restock to check it directly.';

  private reminderSubscription: Subscription | null = null;
  private sessionActive = false;
  private readonly subscriptions = new Subscription();

  constructor(
    private readonly machineService: MachineService,
    access: AccessService,
    supportSession: SupportSessionService
  ) {
    this.subscriptions.add(
      supportSession.active$.subscribe((active) => {
        this.sessionActive = active;
        if (active) {
          this.clear();
        }
      })
    );
    this.subscriptions.add(
      access.allows('Integration.Manage').subscribe((allowed) => (this.canManageIntegration = allowed))
    );
    this.subscriptions.add(
      access.allows('Inventory.Operate').subscribe((allowed) => {
        if (allowed) {
          this.ensureLoaded();
        } else {
          this.clear();
        }
      })
    );
  }

  ngOnDestroy(): void {
    this.subscriptions.unsubscribe();
    this.reminderSubscription?.unsubscribe();
  }

  /** Whether there is a reminder with at least one machine on it to render. */
  get isShown(): boolean {
    return this.state === 'loaded' && (this.reminder?.machines.length ?? 0) > 0;
  }

  /**
   * Whether this business's Nayax account has not been confirmed to allow the machine alerts read
   * (issue #891), in which case the banner says Sync Restock may be unavailable rather than
   * sending an operator to an action that will refuse them.
   */
  get isSyncAvailabilityUnconfirmed(): boolean {
    return this.reminder?.syncAvailability === 'NotConfirmed';
  }

  /**
   * What one row says about the machine's last successful fetch. "No recorded sync" is deliberately
   * not "never synced": a machine restocked before this was recorded has no record of syncs that
   * really happened.
   */
  lastSyncedLabel(machine: MachineRestockSyncReminderMachine): string {
    if (machine.daysSinceLastSuccessfulSync === null) {
      return 'No recorded sync';
    }
    if (machine.daysSinceLastSuccessfulSync === 0) {
      return 'last synced today';
    }
    return machine.daysSinceLastSuccessfulSync === 1
      ? 'last synced 1 day ago'
      : `last synced ${machine.daysSinceLastSuccessfulSync} days ago`;
  }

  /**
   * What one row says about the restock events already imported for the machine. It names them as
   * *known* pending events on purpose: this is a count of persisted facts, and a zero is never a
   * claim that Nayax holds no alert nobody has fetched.
   */
  pendingLabel(machine: MachineRestockSyncReminderMachine): string {
    if (machine.knownPendingEventCount === 0) {
      return 'no known pending restock events';
    }
    return machine.knownPendingEventCount === 1
      ? '1 known pending restock event'
      : `${machine.knownPendingEventCount} known pending restock events`;
  }

  /**
   * The query parameters a row's machine link carries: `syncRestock=open` (issue #1011), so the
   * machine page opens its Sync Restock dialog by itself, and the business date of the oldest
   * counted pending event as `restockFrom` (issue #948), so that dialog opens far enough back to
   * show every event this row counted. `restockFrom` is left off when nothing is pending.
   */
  restockLinkQueryParams(machine: MachineRestockSyncReminderMachine): Record<string, string> {
    const params: Record<string, string> = { [SYNC_RESTOCK_QUERY_PARAM]: SYNC_RESTOCK_OPEN };
    const oldest = machine.oldestKnownPendingEventBusinessDate;
    if (oldest) {
      params['restockFrom'] = oldest.slice(0, 10);
    }
    return params;
  }

  private ensureLoaded(): void {
    if (this.reminderSubscription !== null || this.sessionActive) {
      return;
    }

    this.state = 'loading';
    this.reminderSubscription = this.machineService.getRestockSyncReminder().subscribe({
      next: (response) => {
        if (isMachineRestockSyncReminderApplicable(response)) {
          this.reminder = response;
          this.state = 'loaded';
        } else {
          this.reminder = null;
          this.state = 'hidden';
        }
      },
      error: () => {
        this.reminder = null;
        this.state = 'failed';
      }
    });
  }

  private clear(): void {
    this.reminderSubscription?.unsubscribe();
    this.reminderSubscription = null;
    this.reminder = null;
    this.state = 'idle';
  }
}
