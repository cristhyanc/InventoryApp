import { HttpErrorResponse } from '@angular/common/http';
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
import { SiteService } from '../../services/site.service';
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
      { provide: SiteService, useValue: { getList: jest.fn(() => of([])) } },
      { provide: PickListService, useValue: { get: jest.fn(() => of(pickListResult())) } },
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
 * The 1.5rem gap between cards comes entirely from a `flex flex-col gap-6` parent (styles.scss's
 * `.page`, and each view component's own wrapper), which spaces every one of its direct children by
 * the same amount regardless of which are present (issue #894). Since issue #996 Plan picks is a
 * component of its own, so - exactly like Record refill - its cards are the direct children of one
 * wrapper **inside** it, and its host element must carry no display utility: the page hides the view
 * with `[hidden]`, and a `flex` class on the host would compete with that rule.
 */
describe('PickListComponent view card spacing (issues #894, #996)', () => {
  const marginUtility = /\b(m[tbxy]?-\d|space-y-\d)\b/;

  afterEach(() => TestBed.resetTestingModule());

  it('keeps the mode toggle and both view components as direct, unmargined children of .page', async () => {
    const { host } = await render(of([machine(10, 'Machine A'), machine(20, 'Machine B')]));

    const page = host.querySelector('.page') as HTMLElement;
    const toggleCards = Array.from(page.children).filter((child) => child.classList.contains('card'));
    expect(toggleCards).toHaveLength(1); // the mode toggle; each view owns its own cards
    toggleCards.forEach((card) => {
      expect(card.parentElement).toBe(page);
      expect(card.className).not.toMatch(marginUtility);
    });

    const planPicks = page.querySelector('app-plan-picks') as HTMLElement;
    expect(planPicks.parentElement).toBe(page);
    expect(planPicks.className).not.toMatch(/\b(flex|grid|block)\b/);
    expect(planPicks.className).not.toMatch(marginUtility);
  });

  it('spaces Plan picks’ own cards with one gap-6 wrapper inside the component', async () => {
    const { host } = await render(of([machine(10, 'Machine A')]));

    const planPicks = host.querySelector('app-plan-picks') as HTMLElement;
    expect(planPicks.children).toHaveLength(1);
    const wrapper = planPicks.firstElementChild as HTMLElement;
    expect(wrapper.classList).toContain('flex');
    expect(wrapper.classList).toContain('flex-col');
    expect(wrapper.classList).toContain('gap-6');

    const cards = Array.from(wrapper.children).filter((child) => child.classList.contains('card'));
    expect(cards).toHaveLength(3); // toolbar, Selected machines, the matrix
    cards.forEach((card) => expect(card.className).not.toMatch(marginUtility));
  });

  it('keeps a single gap when the Selected machines card is absent', async () => {
    const { host } = await render(throwError(() => new HttpErrorResponse({ status: 500 })));

    const planPicks = host.querySelector('app-plan-picks') as HTMLElement;
    const wrapper = planPicks.firstElementChild as HTMLElement;
    const cards = Array.from(wrapper.children).filter((child) => child.classList.contains('card'));

    expect(cards).toHaveLength(2); // toolbar and the matrix card - no Selected machines card
    cards.forEach((card) => expect(card.className).not.toMatch(marginUtility));
  });
});
