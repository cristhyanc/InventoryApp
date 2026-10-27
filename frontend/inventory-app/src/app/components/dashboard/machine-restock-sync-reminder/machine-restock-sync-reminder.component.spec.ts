import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { Observable, Subject, of, throwError } from 'rxjs';
import { AccessService } from '../../../access/access.service';
import { BusinessTimeZoneService } from '../../../formatting/business-time-zone.service';
import {
  MachineRestockSyncReminder,
  MachineRestockSyncReminderMachine,
  MachineRestockSyncReminderResponse
} from '../../../models/models';
import { MachineService } from '../../../services/machine.service';
import { SupportSessionService } from '../../../support-session/support-session.service';
import { MachineRestockSyncReminderComponent } from './machine-restock-sync-reminder.component';

function machine(
  overrides: Partial<MachineRestockSyncReminderMachine> = {}
): MachineRestockSyncReminderMachine {
  return {
    localMachineId: 31,
    machineName: 'Avenue Hotel',
    lastSuccessfulSyncAtUtc: '2026-10-18T08:00:00Z',
    lastSuccessfulSyncBusinessDate: '2026-10-18T00:00:00',
    daysSinceLastSuccessfulSync: 7,
    isFetchOverdue: true,
    knownPendingEventCount: 0,
    oldestKnownPendingEventAtUtc: null,
    hasPendingWorkBeforeDefaultFrom: false,
    ...overrides
  };
}

function reminder(
  machines: MachineRestockSyncReminderMachine[],
  overrides: Partial<MachineRestockSyncReminder> = {}
): MachineRestockSyncReminder {
  return {
    applicable: true,
    asOfUtc: '2026-10-25T00:00:00Z',
    businessDate: '2026-10-25T00:00:00',
    overdueAfterDays: 3,
    defaultFromBusinessDate: '2026-10-22T00:00:00',
    syncAvailability: 'Confirmed',
    machines,
    ...overrides
  };
}

interface RenderOptions {
  response?: Observable<MachineRestockSyncReminderResponse>;
  capabilities?: Record<string, boolean>;
  sessionActive$?: Observable<boolean>;
}

async function render(options: RenderOptions = {}) {
  const getRestockSyncReminder = jest.fn(
    () => options.response ?? of<MachineRestockSyncReminderResponse>(reminder([machine()]))
  );
  const capabilities = options.capabilities ?? { 'Inventory.Operate': true, 'Integration.Manage': true };

  await TestBed.configureTestingModule({
    imports: [MachineRestockSyncReminderComponent],
    providers: [
      provideRouter([]),
      { provide: MachineService, useValue: { getRestockSyncReminder } },
      { provide: AccessService, useValue: { allows: (c: string) => of(capabilities[c] ?? false) } },
      { provide: SupportSessionService, useValue: { active$: options.sessionActive$ ?? of(false) } },
      { provide: BusinessTimeZoneService, useValue: { timeZoneId: 'Australia/Sydney' } }
    ]
  }).compileComponents();

  const fixture = TestBed.createComponent(MachineRestockSyncReminderComponent);
  fixture.detectChanges();

  return {
    fixture,
    host: fixture.nativeElement as HTMLElement,
    component: fixture.componentInstance,
    getRestockSyncReminder
  };
}

const banner = (host: HTMLElement) => host.querySelector('[data-testid="restock-sync-reminder"]');
const unavailable = (host: HTMLElement) =>
  host.querySelector('[data-testid="restock-sync-reminder-unavailable"]');

/**
 * The Dashboard's machine restock-sync reminder (issue #891).
 *
 * Its specification is a set of things it must never say: an empty banner for a business it was
 * never told about, "up to date" after a failed read, a Sync Restock action it has no authority to
 * offer, and a reminder inside a support session.
 */
describe('MachineRestockSyncReminderComponent (issue #891)', () => {
  it('names each machine, how long since its last successful sync, and the Sync Restock action', async () => {
    const { host } = await render();

    expect(banner(host)).not.toBeNull();
    expect(banner(host)?.textContent).toContain('Machine restock sync needs attention.');
    expect(banner(host)?.textContent).toContain('Avenue Hotel');
    expect(banner(host)?.textContent).toContain('last synced 7 days ago');
    expect(banner(host)?.textContent).toContain('Sync Restock');
    expect(banner(host)?.textContent).toContain('Review restocks to keep storage inventory accurate.');
  });

  it('links each row to that machine details page by its local machine id', async () => {
    const { host } = await render();

    const link = host.querySelector('[data-testid="restock-sync-reminder-link-31"]');
    expect(link?.getAttribute('href')).toBe('/machines/31');
  });

  /**
   * "No recorded sync" and "never synced" are different claims, and only the first is true:
   * machines restocked before this metadata existed have no record of syncs that really happened.
   */
  it('reports a machine with no recorded sync as such rather than as never synced', async () => {
    const { host } = await render({
      response: of(
        reminder([
          machine({ lastSuccessfulSyncAtUtc: null, daysSinceLastSuccessfulSync: null, isFetchOverdue: true })
        ])
      )
    });

    expect(banner(host)?.textContent).toContain('No recorded sync');
    expect(banner(host)?.textContent).not.toContain('never');
  });

  it('reports known pending restock events as known, never as a complete count of outstanding work', async () => {
    const { host } = await render({
      response: of(
        reminder([
          machine({
            daysSinceLastSuccessfulSync: 0,
            isFetchOverdue: false,
            knownPendingEventCount: 2,
            oldestKnownPendingEventAtUtc: '2026-10-24T03:00:00Z'
          })
        ])
      )
    });

    expect(banner(host)?.textContent).toContain('last synced today');
    expect(banner(host)?.textContent).toContain('2 known pending restock events');
  });

  /**
   * Work older than the dialog's default From date would otherwise simply not be in the list an
   * operator opens, which is the silent disappearance this reminder exists to prevent.
   */
  it('tells the operator to clear or move the From date back when pending work predates it', async () => {
    const { host } = await render({
      response: of(
        reminder([
          machine({ knownPendingEventCount: 1, oldestKnownPendingEventAtUtc: '2026-10-14T03:00:00Z', hasPendingWorkBeforeDefaultFrom: true })
        ])
      )
    });

    const note = host.querySelector('[data-testid="restock-sync-reminder-older-than-default-31"]');
    expect(note?.textContent).toContain('older than');
    expect(note?.textContent).toContain('22 Oct 2026');
  });

  it('does not show that instruction when nothing pending predates the default From date', async () => {
    const { host } = await render({
      response: of(reminder([machine({ knownPendingEventCount: 1, oldestKnownPendingEventAtUtc: '2026-10-24T03:00:00Z' })]))
    });

    expect(host.querySelector('[data-testid="restock-sync-reminder-older-than-default-31"]')).toBeNull();
  });

  it('says the sync may be unavailable when the alerts permission is unconfirmed', async () => {
    const { host } = await render({
      response: of(reminder([machine()], { syncAvailability: 'NotConfirmed' }))
    });

    const note = host.querySelector('[data-testid="restock-sync-reminder-availability"]');
    expect(note?.textContent).toContain('Sync Restock may be unavailable');
    expect(
      host.querySelector('[data-testid="restock-sync-reminder-availability-link"]')?.getAttribute('href')
    ).toBe('/admin/nayax-settings');
  });

  it('asks for an Owner instead of linking the connection for a member who may not manage it', async () => {
    const { host } = await render({
      response: of(reminder([machine()], { syncAvailability: 'NotConfirmed' })),
      capabilities: { 'Inventory.Operate': true, 'Integration.Manage': false }
    });

    expect(host.querySelector('[data-testid="restock-sync-reminder-availability-link"]')).toBeNull();
    expect(host.querySelector('[data-testid="restock-sync-reminder-availability"]')?.textContent)
      .toContain('Ask a business Owner');
  });

  it('shows nothing when every provider machine is synced and clear', async () => {
    const { host } = await render({ response: of(reminder([])) });

    expect(banner(host)).toBeNull();
    expect(unavailable(host)).toBeNull();
  });

  it.each(['SupportView', 'NayaxNotConnected', 'NoProviderMachines'])(
    'shows nothing when the API withholds the reminder with reason %s',
    async (reason) => {
      const { host } = await render({ response: of({ applicable: false as const, reason }) });

      expect(banner(host)).toBeNull();
      expect(unavailable(host)).toBeNull();
    }
  );

  /**
   * A request nobody answered is not a business with nothing outstanding, so the failure is stated
   * rather than rendered as an absent banner.
   */
  it('reports a failed read as unavailable rather than as nothing pending', async () => {
    const { host } = await render({ response: throwError(() => new Error('boom')) });

    expect(banner(host)).toBeNull();
    expect(unavailable(host)?.textContent).toContain('Machine restock sync status is unavailable.');
    expect(unavailable(host)?.textContent).toContain('unknown rather than up to date');
  });

  it('asks for nothing while Inventory.Operate is not granted', async () => {
    const { host, getRestockSyncReminder } = await render({
      capabilities: { 'Inventory.Operate': false, 'Integration.Manage': false }
    });

    expect(getRestockSyncReminder).not.toHaveBeenCalled();
    expect(banner(host)).toBeNull();
    expect(unavailable(host)).toBeNull();
  });

  /**
   * A support session resolves no Nayax credentials and may take no action (issue #551), so there
   * is no reminder to show and no request worth making - even though the session's capability set
   * does grant `Inventory.Operate`.
   */
  it('asks for nothing and shows nothing inside a support session', async () => {
    const { host, getRestockSyncReminder } = await render({ sessionActive$: of(true) });

    expect(getRestockSyncReminder).not.toHaveBeenCalled();
    expect(banner(host)).toBeNull();
    expect(unavailable(host)).toBeNull();
  });

  it('withdraws the reminder when a support session starts after it was loaded', async () => {
    const sessionActive$ = new Subject<boolean>();
    const { fixture, host } = await render({ sessionActive$ });

    expect(banner(host)).not.toBeNull();

    sessionActive$.next(true);
    fixture.detectChanges();

    expect(banner(host)).toBeNull();
  });

  it('reads the reminder exactly once per page load', async () => {
    const { getRestockSyncReminder } = await render();

    expect(getRestockSyncReminder).toHaveBeenCalledTimes(1);
  });
});
