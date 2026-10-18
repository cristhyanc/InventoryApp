import { HttpErrorResponse } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';
import { Observable, of, throwError } from 'rxjs';
import { BusinessTimeZoneService } from '../../formatting/business-time-zone.service';
import { Machine, StockAdjustmentReason, StockAdjustmentSource, StockHistoryPage } from '../../models/models';
import {
  SpecConnectionStatus,
  provideNayaxConnectionStatus
} from '../../provider/provider-availability.spec-helpers';
import { MachineService } from '../../services/machine.service';
import { ProductService } from '../../services/product.service';
import { StockService } from '../../services/stock.service';
import { SupportSessionService } from '../../support-session/support-session.service';
import { StockHistoryPageComponent } from './stock-history-page.component';

function machine(machineID: number, machineName: string): Machine {
  return { machineID, machineName } as Machine;
}

function page(): StockHistoryPage {
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
        source: StockAdjustmentSource.Nayax,
        machineId: 9,
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

/** A `409` exactly as `NayaxConnectionExceptionHandler` writes one for a business with no Nayax. */
function notConnected(): HttpErrorResponse {
  return new HttpErrorResponse({
    status: 409,
    error: { code: 'nayax_not_connected', nayaxConnectionStatus: 'NotConfigured' }
  });
}

async function render(options: {
  status?: SpecConnectionStatus;
  machines?: Observable<Machine[]>;
} = {}) {
  await TestBed.configureTestingModule({
    imports: [StockHistoryPageComponent],
    providers: [
      provideRouter([]),
      {
        provide: ActivatedRoute,
        useValue: { snapshot: { queryParamMap: convertToParamMap({}), paramMap: convertToParamMap({}) } }
      },
      { provide: StockService, useValue: { historyPage: jest.fn(() => of(page())) } },
      { provide: ProductService, useValue: { getAll: jest.fn(() => of([])) } },
      {
        provide: MachineService,
        useValue: { getAll: jest.fn(() => options.machines ?? of([machine(9, 'Lobby')])) }
      },
      { provide: SupportSessionService, useValue: { active$: of(false) } },
      provideNayaxConnectionStatus(options.status ?? 'Ready')
    ]
  }).compileComponents();

  TestBed.inject(BusinessTimeZoneService).publish('Australia/Sydney');

  const fixture = TestBed.createComponent(StockHistoryPageComponent);
  fixture.detectChanges();
  return {
    component: fixture.componentInstance,
    host: fixture.nativeElement as HTMLElement,
    machineService: TestBed.inject(MachineService) as unknown as { getAll: jest.Mock }
  };
}

const machineFilter = (host: HTMLElement) => host.querySelector('#filter-machine');
const machineFilterError = (host: HTMLElement) => host.querySelector('[data-testid="machine-filter-error"]');
const sourceOptions = (host: HTMLElement) =>
  Array.from(host.querySelectorAll('#filter-source option')).map((option) => option.textContent?.trim());

afterEach(() => TestBed.resetTestingModule());

/**
 * Stock History without a usable provider (issue #728): the machine filter and the Nayax source
 * option answer two different questions, and a failed machine read is never one of the answers.
 */
describe('StockHistoryPageComponent provider-optional filters (issue #728)', () => {
  it('offers the machine filter and the Nayax source for a connected business', async () => {
    const { host } = await render();

    expect(machineFilter(host)).not.toBeNull();
    expect(machineFilterError(host)).toBeNull();
    expect(sourceOptions(host)).toEqual(['All sources', 'Manual', 'Nayax Sync Restock']);
  });

  it('hides the machine filter and the Nayax source without a provider, and asks for no machines', async () => {
    const { host, machineService } = await render({ status: 'NotConfigured' });

    expect(machineService.getAll).not.toHaveBeenCalled();
    expect(machineFilter(host)).toBeNull();
    expect(machineFilterError(host)).toBeNull();
    expect(sourceOptions(host)).toEqual(['All sources', 'Manual']);
  });

  it('keeps the Nayax source for a connection that needs attention: those movements are recorded', async () => {
    const { host } = await render({ status: 'NeedsAttention' });

    expect(machineFilter(host)).toBeNull();
    expect(sourceOptions(host)).toEqual(['All sources', 'Manual', 'Nayax Sync Restock']);
  });

  it('hides the machine filter when the refusal comes back from the machine read itself', async () => {
    const { host, component, machineService } = await render({
      status: 'unreadable',
      machines: throwError(() => notConnected())
    });

    expect(machineService.getAll).toHaveBeenCalledTimes(1);
    expect(machineFilter(host)).toBeNull();
    expect(machineFilterError(host)).toBeNull();
    expect(component.machines).toEqual([]);
  });

  it('shows an error in place of the machine filter when the machine read failed', async () => {
    const { host } = await render({ machines: throwError(() => new HttpErrorResponse({ status: 500 })) });

    expect(machineFilter(host)).toBeNull();
    expect(machineFilterError(host)?.textContent).toContain('The machine list could not be loaded');
  });

  it('hides the machine filter when the provider lists no machines, with no error', async () => {
    const { host } = await render({ machines: of([]) });

    expect(machineFilter(host)).toBeNull();
    expect(machineFilterError(host)).toBeNull();
  });

  it('still labels a Nayax-sourced movement in the table when the filter does not offer it', async () => {
    const { host } = await render({ status: 'NotConfigured' });

    const cells = Array.from(host.querySelectorAll('[data-testid="history-rows"] td')).map((cell) =>
      cell.textContent?.trim()
    );
    expect(cells).toContain('Nayax Sync Restock');
  });
});
