import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { of, throwError } from 'rxjs';
import { MachineListComponent } from './machine-list.component';
import { MachineService } from '../../services/machine.service';
import { LocalMachineService } from '../../services/local-machine.service';
import { Machine } from '../../models/models';
import { AccessService } from '../../access/access.service';
import { SupportSessionService } from '../../support-session/support-session.service';
import {
  provideNayaxConnectionStatus,
  providerAvailabilityDouble
} from '../../provider/provider-availability.spec-helpers';

/**
 * The providers this page's children need: the status banner (issue #539) and the shared
 * availability signal the live fleet section reads (issue #728), plus the business's own machine
 * list composed into the page since issue #740.
 */
function machinePageProviders(status: 'Ready' | 'NotConfigured' = 'Ready', canViewFinancials = true) {
  return [
    provideNayaxConnectionStatus(status),
    { provide: AccessService, useValue: { allows: () => of(canViewFinancials) } },
    { provide: LocalMachineService, useValue: { getAll: () => of([]) } },
    { provide: SupportSessionService, useValue: { active$: of(false) } }
  ];
}

/** A stub `AccessService` for the direct-construction tests below, granted by default. */
function accessServiceStub(canViewFinancials = true): AccessService {
  return { allows: () => of(canViewFinancials) } as unknown as AccessService;
}

function machine(overrides: Partial<Machine>): Machine {
  return {
    machineID: 1,
    machineName: null,
    machineNumber: null,
    todayGrossRevenue: 0,
    currentWeekGrossRevenue: 0,
    lastWeekGrossRevenue: 0,
    twoWeeksAgoGrossRevenue: 0,
    todayDirectProfit: null,
    twoWeeksAgoDirectProfit: null,
    lastWeekDirectProfit: null,
    currentWeekDirectProfit: null,
    previousComparableWeekGrossRevenue: 0,
    previousComparableWeekDirectProfit: null,
    monthToDateGrossRevenue: 0,
    monthToDateDirectProfit: null,
    profitabilityStatus: null,
    ...overrides
  };
}

const MACHINE_A = machine({ machineID: 10, localMachineId: 110, machineName: 'Front Lobby Snacks', machineNumber: 'A-100', todayGrossRevenue: 12.5, todayDirectProfit: 5 });
const MACHINE_B = machine({ machineID: 20, localMachineId: 120, machineName: 'Gym Drinks', machineNumber: 'B-200', todayGrossRevenue: 30, todayDirectProfit: null });

describe('MachineListComponent loading', () => {
  it('loads machines from MachineService.getAll on init', () => {
    const getAll = jest.fn(() => of([MACHINE_A, MACHINE_B]));
    const component = new MachineListComponent({ getAll } as unknown as MachineService, providerAvailabilityDouble(), accessServiceStub());

    component.ngOnInit();

    expect(getAll).toHaveBeenCalledTimes(1);
    expect(component.machines.map((m) => m.machineID)).toEqual([10, 20]);
    expect(component.filteredMachines.map((m) => m.machineID)).toEqual([10, 20]);
    expect(component.loadState.loaded).toBe(true);
    expect(component.loadState.loadFailed).toBe(false);
  });

  /**
   * An outage is reported as an outage (issue #740, over issue #728's shared path): the live fleet
   * section is replaced by an error rather than by an empty list, and no machine is fabricated.
   */
  it('reports an error in place of the fleet when MachineService.getAll fails, without fabricating data', () => {
    const getAll = jest.fn(() => throwError(() => new Error('network error')));
    const component = new MachineListComponent({ getAll } as unknown as MachineService, providerAvailabilityDouble(), accessServiceStub());

    component.ngOnInit();

    expect(component.fleetError).toContain('could not be loaded from Nayax');
    expect(component.machines).toEqual([]);
    // The section is still "shown": an outage is not an unavailable provider, so the page says so
    // instead of silently dropping the part of itself the provider feeds.
    expect(component.fleetShown).toBe(true);
  });
});

describe('MachineListComponent search/filter', () => {
  it('filters by machine name, case-insensitively', () => {
    const component = new MachineListComponent({ getAll: () => of([MACHINE_A, MACHINE_B]) } as unknown as MachineService, providerAvailabilityDouble(), accessServiceStub());
    component.ngOnInit();

    component.onSearchChange('gym');

    expect(component.filteredMachines.map((m) => m.machineID)).toEqual([20]);
  });

  it('filters by machine number', () => {
    const component = new MachineListComponent({ getAll: () => of([MACHINE_A, MACHINE_B]) } as unknown as MachineService, providerAvailabilityDouble(), accessServiceStub());
    component.ngOnInit();

    component.onSearchChange('a-100');

    expect(component.filteredMachines.map((m) => m.machineID)).toEqual([10]);
  });

  it('shows no matches for an unmatched search term, without discarding the full machine list', () => {
    const component = new MachineListComponent({ getAll: () => of([MACHINE_A, MACHINE_B]) } as unknown as MachineService, providerAvailabilityDouble(), accessServiceStub());
    component.ngOnInit();

    component.onSearchChange('does-not-exist');

    expect(component.filteredMachines).toEqual([]);
    expect(component.machines).toHaveLength(2);
  });

  it('clearing the search restores the full list', () => {
    const component = new MachineListComponent({ getAll: () => of([MACHINE_A, MACHINE_B]) } as unknown as MachineService, providerAvailabilityDouble(), accessServiceStub());
    component.ngOnInit();
    component.onSearchChange('gym');

    component.onSearchChange('');

    expect(component.filteredMachines.map((m) => m.machineID)).toEqual([10, 20]);
  });
});

describe('MachineListComponent rendering', () => {
  afterEach(() => TestBed.resetTestingModule());

  async function renderList(machines: Machine[]) {
    await TestBed.configureTestingModule({
      imports: [MachineListComponent],
      providers: [
        provideRouter([]),
        { provide: MachineService, useValue: { getAll: () => of(machines) } },
        ...machinePageProviders()
      ]
    }).compileComponents();

    const fixture = TestBed.createComponent(MachineListComponent);
    fixture.detectChanges();
    return fixture;
  }

  it('renders the machine name, number and revenue summary for a successful load', async () => {
    const fixture = await renderList([MACHINE_A]);
    const host = fixture.nativeElement as HTMLElement;

    expect(host.textContent).toContain('Front Lobby Snacks');
    expect(host.textContent).toContain('A-100');
    expect(host.textContent).toContain('$12.50 sales');
    expect(host.textContent).toContain('$5.00 profit');
    expect(host.textContent).not.toContain('margin');
  });

  it('shows "Profit unavailable" instead of a fabricated figure when direct profit is null', async () => {
    const fixture = await renderList([MACHINE_B]);
    const host = fixture.nativeElement as HTMLElement;

    expect(host.textContent).toContain('Profit unavailable');
  });

  it('shows an empty state when there are no machines at all', async () => {
    const fixture = await renderList([]);
    const host = fixture.nativeElement as HTMLElement;

    expect(host.textContent).toContain('No machines available yet.');
  });

  it('shows a distinct no-match state when a search matches nothing', async () => {
    const fixture = await renderList([MACHINE_A, MACHINE_B]);
    const component = fixture.componentInstance;
    component.onSearchChange('nonexistent-machine');
    fixture.detectChanges();
    const host = fixture.nativeElement as HTMLElement;

    expect(host.textContent).toContain('No machines match your search.');
    expect(host.textContent).not.toContain('No machines available yet.');
  });

  it('shows an explicit error message when the load fails', async () => {
    await TestBed.configureTestingModule({
      imports: [MachineListComponent],
      providers: [
        provideRouter([]),
        { provide: MachineService, useValue: { getAll: () => throwError(() => new Error('boom')) } },
        ...machinePageProviders()
      ]
    }).compileComponents();
    const fixture = TestBed.createComponent(MachineListComponent);
    fixture.detectChanges();
    const host = fixture.nativeElement as HTMLElement;

    expect(host.textContent).toContain('The live machine list could not be loaded from Nayax.');
  });

  it('links each machine to its existing machine detail route', async () => {
    const fixture = await renderList([MACHINE_A]);
    const host = fixture.nativeElement as HTMLElement;

    const link = host.querySelector('a[href="/machines/110"]');
    expect(link).not.toBeNull();
    expect(link?.textContent).toContain('Front Lobby Snacks');
  });

  it('links a machine by its local id, and does not link one the business has no local record for (issue #741)', async () => {
    const unrecorded = machine({ machineID: 30, localMachineId: null, machineName: 'Not yet recorded' });
    const fixture = await renderList([MACHINE_A, unrecorded]);
    const host = fixture.nativeElement as HTMLElement;

    expect(host.querySelector('a[href="/machines/10"]')).toBeNull();
    const cards = Array.from(host.querySelectorAll('a')).filter((a) => a.textContent?.includes('Not yet recorded'));
    expect(cards).toHaveLength(1);
    expect(cards[0].getAttribute('href')).toBeNull();
  });

  it('shows the Nayax status banner when the connection is not Ready (issue #539)', async () => {
    await TestBed.configureTestingModule({
      imports: [MachineListComponent],
      providers: [
        provideRouter([]),
        { provide: MachineService, useValue: { getAll: () => of([MACHINE_A]) } },
        ...machinePageProviders('NotConfigured')
      ]
    }).compileComponents();
    const fixture = TestBed.createComponent(MachineListComponent);
    fixture.detectChanges();
    const host = fixture.nativeElement as HTMLElement;

    expect(host.querySelector('[data-testid="nayax-status-banner"]')).not.toBeNull();
  });

  it('hides the Nayax status banner once the connection is Ready (issue #539)', async () => {
    const fixture = await renderList([MACHINE_A]);
    const host = fixture.nativeElement as HTMLElement;

    expect(host.querySelector('[data-testid="nayax-status-banner"]')).toBeNull();
  });
});

/**
 * `Dashboard.ViewFinancials` hides sales, direct profit and profitability status on this page's
 * cards (issue #699): a role without it - an Operator - sees none of those figures, while a
 * Manager or an Owner sees them exactly as before.
 */
describe('MachineListComponent financial figures gated on Dashboard.ViewFinancials (issue #699)', () => {
  afterEach(() => TestBed.resetTestingModule());

  const MACHINE_WITH_STATUS = machine({
    machineID: 30,
    machineName: 'Loading Dock',
    todayGrossRevenue: 99,
    todayDirectProfit: 40,
    currentWeekGrossRevenue: 500,
    currentWeekDirectProfit: 120,
    profitabilityStatus: 'Profitable'
  });

  async function renderAs(canViewFinancials: boolean, machines: Machine[] = [MACHINE_WITH_STATUS]) {
    await TestBed.configureTestingModule({
      imports: [MachineListComponent],
      providers: [
        provideRouter([]),
        { provide: MachineService, useValue: { getAll: () => of(machines) } },
        ...machinePageProviders('Ready', canViewFinancials)
      ]
    }).compileComponents();

    const fixture = TestBed.createComponent(MachineListComponent);
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  }

  it('shows no sales, profit or profitability-status text to an Operator', async () => {
    const host = await renderAs(false);

    expect(host.textContent).not.toContain('$99.00');
    expect(host.textContent).not.toContain('$40.00');
    expect(host.textContent).not.toContain('sales');
    expect(host.textContent).not.toContain('profit');
    expect(host.textContent).not.toContain('Profitable');
    expect(host.textContent).toContain('Loading Dock');
  });

  it('keeps the figures exactly as today for a Manager or an Owner', async () => {
    const host = await renderAs(true);

    expect(host.textContent).toContain('$99.00 sales');
    expect(host.textContent).toContain('$40.00 profit');
    expect(host.textContent).toContain('Profitable');
  });

  it('renders without error when a granted role is sent a card with the figures absent', async () => {
    const withheld = machine({
      machineID: 40,
      machineName: 'Unknown figures',
      todayGrossRevenue: null,
      currentWeekGrossRevenue: null,
      profitabilityStatus: null
    });
    const host = await renderAs(true, [withheld]);

    expect(host.textContent).toContain('Unknown figures');
    expect(host.textContent).toContain('Unavailable');
    expect(host.textContent).toContain('Profit unavailable');
    expect(host.textContent).not.toContain('NaN');
    expect(host.textContent).not.toContain('$0.00');
  });
});
