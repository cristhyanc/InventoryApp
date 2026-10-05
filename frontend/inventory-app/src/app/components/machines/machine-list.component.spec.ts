import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { of, throwError } from 'rxjs';
import { MachineListComponent } from './machine-list.component';
import { MachineService } from '../../services/machine.service';
import { Machine } from '../../models/models';

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

const MACHINE_A = machine({ machineID: 10, machineName: 'Front Lobby Snacks', machineNumber: 'A-100', todayGrossRevenue: 12.5, todayDirectProfit: 5 });
const MACHINE_B = machine({ machineID: 20, machineName: 'Gym Drinks', machineNumber: 'B-200', todayGrossRevenue: 30, todayDirectProfit: null });

describe('MachineListComponent loading', () => {
  it('loads machines from MachineService.getAll on init', () => {
    const getAll = jest.fn(() => of([MACHINE_A, MACHINE_B]));
    const component = new MachineListComponent({ getAll } as unknown as MachineService);

    component.ngOnInit();

    expect(getAll).toHaveBeenCalledTimes(1);
    expect(component.machines.map((m) => m.machineID)).toEqual([10, 20]);
    expect(component.filteredMachines.map((m) => m.machineID)).toEqual([10, 20]);
    expect(component.loadState.loaded).toBe(true);
    expect(component.loadState.loadFailed).toBe(false);
  });

  it('flags the load as failed when MachineService.getAll errors, without fabricating data', () => {
    const getAll = jest.fn(() => throwError(() => new Error('network error')));
    const component = new MachineListComponent({ getAll } as unknown as MachineService);

    component.ngOnInit();

    expect(component.loadState.loadFailed).toBe(true);
    expect(component.loadState.loaded).toBe(false);
    expect(component.machines).toEqual([]);
  });
});

describe('MachineListComponent search/filter', () => {
  it('filters by machine name, case-insensitively', () => {
    const component = new MachineListComponent({ getAll: () => of([MACHINE_A, MACHINE_B]) } as unknown as MachineService);
    component.ngOnInit();

    component.onSearchChange('gym');

    expect(component.filteredMachines.map((m) => m.machineID)).toEqual([20]);
  });

  it('filters by machine number', () => {
    const component = new MachineListComponent({ getAll: () => of([MACHINE_A, MACHINE_B]) } as unknown as MachineService);
    component.ngOnInit();

    component.onSearchChange('a-100');

    expect(component.filteredMachines.map((m) => m.machineID)).toEqual([10]);
  });

  it('shows no matches for an unmatched search term, without discarding the full machine list', () => {
    const component = new MachineListComponent({ getAll: () => of([MACHINE_A, MACHINE_B]) } as unknown as MachineService);
    component.ngOnInit();

    component.onSearchChange('does-not-exist');

    expect(component.filteredMachines).toEqual([]);
    expect(component.machines).toHaveLength(2);
  });

  it('clearing the search restores the full list', () => {
    const component = new MachineListComponent({ getAll: () => of([MACHINE_A, MACHINE_B]) } as unknown as MachineService);
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
        { provide: MachineService, useValue: { getAll: () => of(machines) } }
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
        { provide: MachineService, useValue: { getAll: () => throwError(() => new Error('boom')) } }
      ]
    }).compileComponents();
    const fixture = TestBed.createComponent(MachineListComponent);
    fixture.detectChanges();
    const host = fixture.nativeElement as HTMLElement;

    expect(host.textContent).toContain('Failed to load machines.');
  });

  it('links each machine to its existing machine detail route', async () => {
    const fixture = await renderList([MACHINE_A]);
    const host = fixture.nativeElement as HTMLElement;

    const link = host.querySelector('a[href="/machines/10"]');
    expect(link).not.toBeNull();
    expect(link?.textContent).toContain('Front Lobby Snacks');
  });
});
