import { HttpErrorResponse } from '@angular/common/http';
import { SiteService } from '../../services/site.service';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { Observable, of, throwError } from 'rxjs';
import { AccessService } from '../../access/access.service';
import { BusinessTimeZoneService } from '../../formatting/business-time-zone.service';
import { Machine, PickListResult, Product } from '../../models/models';
import { provideNayaxConnectionStatus } from '../../provider/provider-availability.spec-helpers';
import { LocalMachineService } from '../../services/local-machine.service';
import { MachineService } from '../../services/machine.service';
import { PickListService } from '../../services/pick-list.service';
import { ProductService } from '../../services/product.service';
import { ToastService } from '../../services/toast.service';
import { SupportSessionService } from '../../support-session/support-session.service';
import { PickListComponent } from './pick-list.component';

function machine(machineID: number, machineName: string): Machine {
  return { machineID, machineName } as Machine;
}

function product(): Product {
  return { id: 1, name: 'Coke' } as Product;
}

function pickListResult(): PickListResult {
  return { products: [] };
}

async function render(machinesResult: Observable<Machine[]> = of([machine(10, 'Machine A')])) {
  await TestBed.configureTestingModule({
    imports: [PickListComponent],
    providers: [
      provideRouter([]),
      { provide: MachineService, useValue: { getAll: jest.fn(() => machinesResult) } },
      { provide: ProductService, useValue: { getAll: jest.fn(() => of([product()])) } },
      { provide: PickListService, useValue: { get: jest.fn(() => of(pickListResult())) } },
      { provide: SiteService, useValue: { getList: jest.fn(() => of([])) } },
      { provide: LocalMachineService, useValue: { getAll: jest.fn(() => of([])) } },
      { provide: ToastService, useValue: { success: jest.fn(), error: jest.fn(), warning: jest.fn(), info: jest.fn() } },
      { provide: AccessService, useValue: { allows: () => of(true) } },
      { provide: SupportSessionService, useValue: { active$: of(false) } },
      provideNayaxConnectionStatus('Ready')
    ]
  }).compileComponents();

  TestBed.inject(BusinessTimeZoneService).publish('Australia/Sydney');

  const fixture = TestBed.createComponent(PickListComponent);
  fixture.detectChanges();

  return { component: fixture.componentInstance, host: fixture.nativeElement as HTMLElement };
}

/**
 * The filter card (which holds the selected machine chips since issue #996) and the results card
 * carry no margin of their own (issue #894): the 1.5rem gap between them comes entirely from `.page`'s own
 * `flex flex-col gap-6` (styles.scss), which spaces every one of its direct children by the same
 * amount regardless of which are present. That only holds if these `@if` blocks keep emitting the
 * cards as direct children of `.page` with no wrapping element and no competing margin utility -
 * either of which would silently double the gap or collapse it back to zero.
 */
describe('PickListComponent calculated-view card spacing (issue #894)', () => {
  const marginUtility = /\b(m[tbxy]?-\d|space-y-\d)\b/;

  it('keeps the filter and results cards as direct, unmargined children of .page', async () => {
    const { host } = await render(of([machine(10, 'Machine A'), machine(20, 'Machine B')]));

    const page = host.querySelector('.page') as HTMLElement;
    const cards = Array.from(page.children).filter((child) => child.classList.contains('card'));

    expect(cards).toHaveLength(2); // filters (with the selected machine chips), results
    cards.forEach((card) => {
      expect(card.parentElement).toBe(page);
      expect(card.className).not.toMatch(marginUtility);
    });
    expect(page.className).toContain('page');
  });

  it('keeps a single gap between the filter and results cards when the machine list failed', async () => {
    const { host } = await render(throwError(() => new HttpErrorResponse({ status: 500 })));

    const page = host.querySelector('.page') as HTMLElement;
    const cards = Array.from(page.children).filter((child) => child.classList.contains('card'));

    expect(cards).toHaveLength(2); // filters, results
    cards.forEach((card) => {
      expect(card.parentElement).toBe(page);
      expect(card.className).not.toMatch(marginUtility);
    });
  });
});
