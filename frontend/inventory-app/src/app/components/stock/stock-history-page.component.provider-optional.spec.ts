import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';
import { of } from 'rxjs';
import { BusinessTimeZoneService } from '../../formatting/business-time-zone.service';
import { LocalMachine, StockAdjustmentReason, StockAdjustmentSource, StockHistoryPage } from '../../models/models';
import {
  SpecConnectionStatus,
  provideNayaxConnectionStatus
} from '../../provider/provider-availability.spec-helpers';
import { LocalMachineService } from '../../services/local-machine.service';
import { ProductService } from '../../services/product.service';
import { StockService } from '../../services/stock.service';
import { SupportSessionService } from '../../support-session/support-session.service';
import { StockHistoryPageComponent } from './stock-history-page.component';

function localMachine(id: number, name: string): LocalMachine {
  return { id, name, isActive: true, isProviderNamed: false };
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
        machineId: 41,
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

async function render(options: { status?: SpecConnectionStatus } = {}) {
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
      { provide: LocalMachineService, useValue: { getAll: jest.fn(() => of([localMachine(41, 'Lobby')])) } },
      { provide: SupportSessionService, useValue: { active$: of(false) } },
      provideNayaxConnectionStatus(options.status ?? 'Ready')
    ]
  }).compileComponents();

  TestBed.inject(BusinessTimeZoneService).publish('Australia/Sydney');

  const fixture = TestBed.createComponent(StockHistoryPageComponent);
  fixture.detectChanges();
  return {
    component: fixture.componentInstance,
    host: fixture.nativeElement as HTMLElement
  };
}

const machineFilter = (host: HTMLElement) => host.querySelector('#filter-machine');
const sourceOptions = (host: HTMLElement) =>
  Array.from(host.querySelectorAll('#filter-source option')).map((option) => option.textContent?.trim());

afterEach(() => TestBed.resetTestingModule());

/**
 * Stock History without a usable provider (issue #728): the Nayax source option is about movements
 * that are already recorded locally, so it follows `providerRecords` and nothing else.
 *
 * The **machine** filter no longer follows the provider at all: since issue #746 it lists the
 * business's own machine records, and `stock-history-page.component.local-machines.spec.ts` covers
 * it.
 */
describe('StockHistoryPageComponent provider-optional filters (issue #728)', () => {
  it('offers the Nayax source for a connected business', async () => {
    const { host } = await render();

    expect(sourceOptions(host)).toEqual(['All sources', 'Manual', 'Nayax Sync Restock']);
  });

  it('drops the Nayax source without a provider, and keeps the machine filter', async () => {
    const { host } = await render({ status: 'NotConfigured' });

    expect(sourceOptions(host)).toEqual(['All sources', 'Manual']);
    expect(machineFilter(host)).not.toBeNull();
  });

  it('keeps the Nayax source for a connection that needs attention: those movements are recorded', async () => {
    const { host } = await render({ status: 'NeedsAttention' });

    expect(sourceOptions(host)).toEqual(['All sources', 'Manual', 'Nayax Sync Restock']);
  });

  it('still labels a Nayax-sourced movement in the table when the filter does not offer it', async () => {
    const { host } = await render({ status: 'NotConfigured' });

    const cells = Array.from(host.querySelectorAll('[data-testid="history-rows"] td')).map((cell) =>
      cell.textContent?.trim()
    );
    expect(cells).toContain('Nayax Sync Restock');
  });
});
