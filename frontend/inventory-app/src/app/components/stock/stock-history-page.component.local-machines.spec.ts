import { HttpErrorResponse } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';
import { NEVER, Observable, of, throwError } from 'rxjs';
import { BusinessTimeZoneService } from '../../formatting/business-time-zone.service';
import {
  LocalMachine,
  Product,
  StockAdjustmentReason,
  StockAdjustmentSource,
  StockHistoryPage
} from '../../models/models';
import {
  SpecConnectionStatus,
  provideNayaxConnectionStatus
} from '../../provider/provider-availability.spec-helpers';
import { LocalMachineService } from '../../services/local-machine.service';
import { MachineService } from '../../services/machine.service';
import { ProductService } from '../../services/product.service';
import { StockService } from '../../services/stock.service';
import { SupportSessionService } from '../../support-session/support-session.service';
import { StockHistoryPageComponent } from './stock-history-page.component';

function localMachine(id: number, name: string, isActive = true): LocalMachine {
  return { id, name, isActive, isProviderNamed: false };
}

function product(): Product {
  return { id: 1, name: 'Coke', quantityInStock: 10, lowStockThreshold: 2, unit: 'each' } as Product;
}

function page(machineId: number | null): StockHistoryPage {
  return {
    items: [
      {
        id: 1,
        productId: 1,
        productName: 'Coke',
        quantityChange: 4,
        quantityAfter: 10,
        unitCost: null,
        reason: StockAdjustmentReason.Restock,
        source: StockAdjustmentSource.Manual,
        machineId,
        notes: '',
        eatBefore: null,
        createdAt: '2026-01-15T22:30:00Z'
      }
    ],
    page: 1,
    pageSize: 50,
    totalCount: 1,
    hasMore: false
  };
}

async function render(
  options: {
    status?: SpecConnectionStatus;
    machines?: Observable<LocalMachine[]>;
    movementMachineId?: number | null;
    selectedProduct?: boolean;
  } = {}
) {
  await TestBed.configureTestingModule({
    imports: [StockHistoryPageComponent],
    providers: [
      provideRouter([]),
      {
        provide: ActivatedRoute,
        useValue: {
          snapshot: {
            queryParamMap: convertToParamMap(options.selectedProduct ? { productId: '1' } : {}),
            paramMap: convertToParamMap({})
          }
        }
      },
      {
        provide: StockService,
        useValue: { historyPage: jest.fn(() => of(page(options.movementMachineId ?? null))) }
      },
      { provide: ProductService, useValue: { getAll: jest.fn(() => of([product()])) } },
      {
        provide: LocalMachineService,
        useValue: {
          getAll: jest.fn(() => options.machines ?? of([localMachine(41, 'Lobby')]))
        }
      },
      // Registered so that a page that still read the live fleet would be visible as a failure of
      // the assertions below rather than as a missing-provider error.
      { provide: MachineService, useValue: { getAll: jest.fn(() => of([])) } },
      { provide: SupportSessionService, useValue: { active$: of(false) } },
      provideNayaxConnectionStatus(options.status ?? 'Ready')
    ]
  }).compileComponents();

  TestBed.inject(BusinessTimeZoneService).publish('Australia/Sydney');

  const fixture = TestBed.createComponent(StockHistoryPageComponent);
  fixture.detectChanges();
  return {
    fixture,
    component: fixture.componentInstance,
    host: fixture.nativeElement as HTMLElement,
    localMachines: TestBed.inject(LocalMachineService) as unknown as { getAll: jest.Mock },
    fleet: TestBed.inject(MachineService) as unknown as { getAll: jest.Mock }
  };
}

const machineFilter = (host: HTMLElement) => host.querySelector('#filter-machine') as HTMLSelectElement | null;
const machineFilterError = (host: HTMLElement) => host.querySelector('[data-testid="machine-filter-error"]');
const machineFilterLoading = (host: HTMLElement) => host.querySelector('[data-testid="machine-filter-loading"]');
const filterOptionLabels = (host: HTMLElement) =>
  Array.from(host.querySelectorAll('#filter-machine option')).map((option) => option.textContent?.trim());
const machineCell = (host: HTMLElement) =>
  Array.from(host.querySelectorAll('[data-testid="history-rows"] td'))[6]?.textContent?.trim();

afterEach(() => TestBed.resetTestingModule());

/**
 * Stock History lists the business's **own** machines (issue #746, G2b of #329): the filter and the
 * adjustment form's machine field come from `/api/local-machines`, so they work with no provider at
 * all, and the fleet read is not made for them.
 */
describe('StockHistoryPageComponent local machine filter (issue #746)', () => {
  it('offers the machine filter from the local machines without a provider, and asks the fleet for nothing', async () => {
    const { host, localMachines, fleet } = await render({ status: 'NotConfigured' });

    expect(localMachines.getAll).toHaveBeenCalledTimes(1);
    expect(fleet.getAll).not.toHaveBeenCalled();
    expect(machineFilter(host)).not.toBeNull();
    expect(filterOptionLabels(host)).toEqual(['All machines', 'Lobby']);
    expect(machineFilterError(host)).toBeNull();
  });

  it('offers the same filter with a provider connected', async () => {
    const { host, fleet } = await render({ status: 'Ready' });

    expect(fleet.getAll).not.toHaveBeenCalled();
    expect(filterOptionLabels(host)).toEqual(['All machines', 'Lobby']);
  });

  it('sends the local machine id as the filter value', async () => {
    const { component, host } = await render();

    // Selecting "Lobby" must stage the local machine's own id - that is what a movement records.
    const select = machineFilter(host)!;
    select.value = select.options[1].value;
    select.dispatchEvent(new Event('change'));

    expect(component.filters.machineId).toBe(41);
  });

  it('lists and names a deactivated machine in the filter and in the machine column', async () => {
    const { host, component } = await render({
      machines: of([localMachine(41, 'Lobby'), localMachine(42, 'Retired corridor', false)]),
      movementMachineId: 42
    });

    expect(filterOptionLabels(host)).toEqual(['All machines', 'Lobby', 'Retired corridor']);
    expect(component.machineLabel(42)).toBe('Retired corridor');
    expect(machineCell(host)).toBe('Retired corridor');
  });

  it('hides the filter when every local machine is deactivated, still naming them in the column', async () => {
    const { host, component } = await render({
      machines: of([localMachine(42, 'Retired corridor', false)]),
      movementMachineId: 42
    });

    expect(machineFilter(host)).toBeNull();
    expect(machineFilterError(host)).toBeNull();
    expect(component.machineLabel(42)).toBe('Retired corridor');
  });

  it('hides the filter when the business has recorded no machine at all, with no error', async () => {
    const { host } = await render({ machines: of([]) });

    expect(machineFilter(host)).toBeNull();
    expect(machineFilterError(host)).toBeNull();
    expect(machineFilterLoading(host)).toBeNull();
  });

  it('shows an explicit error in place of the filter when the local machine read failed', async () => {
    const { host, component } = await render({
      machines: throwError(() => new HttpErrorResponse({ status: 500 }))
    });

    expect(machineFilter(host)).toBeNull();
    expect(machineFilterError(host)?.textContent).toContain('The machine list could not be loaded');
    expect(component.machines).toEqual([]);
  });

  it('says the machines are still loading rather than offering no filter', async () => {
    // A request that never answers: the filter must not yet read as "this business has no machines".
    const { host } = await render({ machines: NEVER });

    expect(machineFilter(host)).toBeNull();
    expect(machineFilterError(host)).toBeNull();
    expect(machineFilterLoading(host)).not.toBeNull();
  });
});

/**
 * The adjustment form is fed by this page (issue #746), so what it offers is asserted here too:
 * active local machines only, whatever the provider state, and an explicit error when the read
 * failed.
 */
describe('StockHistoryPageComponent adjustment machine field (issue #746)', () => {
  const adjustmentField = (host: HTMLElement) => host.querySelector('#adjustment-machine');
  const adjustmentOptions = (host: HTMLElement) =>
    Array.from(host.querySelectorAll('#adjustment-machine option')).map((option) => option.textContent?.trim());

  it('offers the active local machines without a provider', async () => {
    const { host } = await render({
      status: 'NotConfigured',
      selectedProduct: true,
      machines: of([localMachine(41, 'Lobby'), localMachine(42, 'Retired corridor', false)])
    });

    expect(adjustmentField(host)).not.toBeNull();
    expect(adjustmentOptions(host)).toEqual(['— None —', 'Lobby']);
  });

  it('offers the same machines with a provider connected', async () => {
    const { host } = await render({ status: 'Ready', selectedProduct: true });

    expect(adjustmentOptions(host)).toEqual(['— None —', 'Lobby']);
  });

  it('shows an explicit error in place of the field when the local machine read failed', async () => {
    const { host } = await render({
      selectedProduct: true,
      machines: throwError(() => new HttpErrorResponse({ status: 500 }))
    });

    expect(adjustmentField(host)).toBeNull();
    expect(host.querySelector('[data-testid="adjustment-machine-error"]')?.textContent).toContain(
      'The machine list could not be loaded'
    );
  });
});
