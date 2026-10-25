import { HttpErrorResponse } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { Observable, of, throwError } from 'rxjs';
import { AccessService } from '../../access/access.service';
import { LocalMachine, Machine } from '../../models/models';
import {
  SpecConnectionStatus,
  provideNayaxConnectionStatus
} from '../../provider/provider-availability.spec-helpers';
import { LocalMachineService } from '../../services/local-machine.service';
import { MachineService } from '../../services/machine.service';
import { ProviderMachineReviewService } from '../../services/provider-machine-review.service';
import { SupportSessionService } from '../../support-session/support-session.service';
import { MachineListComponent } from './machine-list.component';

function machine(machineID: number, machineName: string): Machine {
  return { machineID, machineName, todayGrossRevenue: 12.5, todayDirectProfit: 5 } as Machine;
}

function localMachine(id: number, name: string): LocalMachine {
  return { id, name, isActive: true, isProviderNamed: false };
}

function notConnected(): HttpErrorResponse {
  return new HttpErrorResponse({
    status: 409,
    error: { code: 'nayax_not_connected', nayaxConnectionStatus: 'NotConfigured' }
  });
}

async function render(
  options: { status?: SpecConnectionStatus; fleet?: Observable<Machine[]>; locals?: LocalMachine[] } = {}
) {
  const fleetRead = jest.fn(() => options.fleet ?? of([machine(10, 'Front Lobby Snacks')]));

  await TestBed.configureTestingModule({
    imports: [MachineListComponent],
    providers: [
      provideRouter([]),
      { provide: MachineService, useValue: { getAll: fleetRead } },
      { provide: LocalMachineService, useValue: { getAll: () => of(options.locals ?? [localMachine(1, 'Atrium')]) } },
      // The provider machine review composed beside the list since issue #742. It reads the fleet
      // the moment the page shows it, so these tests give it an empty reading and stay about the
      // page itself.
      {
        provide: ProviderMachineReviewService,
        useValue: {
          review: () =>
            of({
              reviewedAtUtc: '2026-10-21T02:00:00Z',
              autoLinked: [],
              awaitingReview: [],
              linked: [],
              awaitingReviewCount: 0,
              linkedCount: 0
            }),
          apply: () => of({})
        }
      },
      { provide: SupportSessionService, useValue: { active$: of(false) } },
      { provide: AccessService, useValue: { allows: () => of(true) } },
      provideNayaxConnectionStatus(options.status ?? 'Ready')
    ]
  }).compileComponents();

  const fixture = TestBed.createComponent(MachineListComponent);
  fixture.detectChanges();

  return { fixture, component: fixture.componentInstance, host: fixture.nativeElement as HTMLElement, fleetRead };
}

const searchBox = (host: HTMLElement) => host.querySelector('input[aria-label="Search machines by name or number"]');

afterEach(() => TestBed.resetTestingModule());

/**
 * The Machines page with no usable provider (issue #740, parent #329 child G1). It is the reason the
 * destination left the provider-locked section of issue #729: the page is two lists, and only one of
 * them needs Nayax.
 */
describe('MachineListComponent without a usable provider (issue #740)', () => {
  it('shows the business own machine list and its actions, and no live fleet at all', async () => {
    const { host, component, fleetRead } = await render({ status: 'NotConfigured' });

    // The local list, with the actions that maintain it.
    expect(host.textContent).toContain('Your machines');
    expect(host.textContent).toContain('Atrium');
    expect(host.querySelector('#newMachineName')).not.toBeNull();

    // And none of the live parts: no fleet section, no revenue figure, no search over it.
    expect(component.fleetShown).toBe(false);
    expect(host.textContent).not.toContain('Live from Nayax');
    expect(host.textContent).not.toContain('$12.50 sales');
    expect(searchBox(host)).toBeNull();

    // The fleet read is not even attempted: the shared answer already said it cannot be served.
    expect(fleetRead).not.toHaveBeenCalled();
  });

  it('withholds the live fleet for credentials that are stored and known not to work', async () => {
    const { component, fleetRead } = await render({ status: 'NeedsAttention' });

    expect(component.fleetShown).toBe(false);
    expect(fleetRead).not.toHaveBeenCalled();
  });

  it('withholds it for a request the server refused as not connected', async () => {
    const { component, host } = await render({ fleet: throwError(notConnected) });

    expect(component.fleetShown).toBe(false);
    expect(host.textContent).toContain('Your machines');
  });

  /**
   * An outage is not an unavailable provider: the page says the live list could not be read, rather
   * than dropping the section as though the business had no provider.
   */
  it('reports an outage in place of the live fleet, and keeps the local list', async () => {
    const { component, host } = await render({ fleet: throwError(() => new Error('socket hang up')) });

    expect(component.fleetShown).toBe(true);
    expect(host.textContent).toContain('The live machine list could not be loaded from Nayax.');
    expect(host.textContent).toContain('Your machines');
  });

  /** An unread status is not a refusal (issue #728's fail-open rule), so the fleet is still asked. */
  it('still asks for the live fleet when the connection status could not be read', async () => {
    const { component, fleetRead, host } = await render({ status: 'unreadable' });

    expect(fleetRead).toHaveBeenCalledTimes(1);
    expect(component.fleetShown).toBe(true);
    expect(host.textContent).toContain('Front Lobby Snacks');
  });

  it('shows both lists for a connected business', async () => {
    const { host, fleetRead } = await render({ status: 'Ready' });

    expect(host.textContent).toContain('Your machines');
    expect(host.textContent).toContain('Atrium');
    expect(host.textContent).toContain('Live from Nayax');
    expect(host.textContent).toContain('Front Lobby Snacks');
    expect(searchBox(host)).not.toBeNull();
    expect(fleetRead).toHaveBeenCalledTimes(1);
  });
});
