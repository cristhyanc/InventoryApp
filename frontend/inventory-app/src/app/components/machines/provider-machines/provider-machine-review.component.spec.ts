import { HttpErrorResponse } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { Observable, of, throwError } from 'rxjs';
import { LocalMachine } from '../../../models/models';
import { LocalMachineService } from '../../../services/local-machine.service';
import {
  ApplyProviderMachineLinksRequest,
  ProviderMachineEntry,
  ProviderMachineLinkAction,
  ProviderMachineLinked,
  ProviderMachineLinksApplied,
  ProviderMachineReview,
  ProviderMachineReviewService
} from '../../../services/provider-machine-review.service';
import { ToastService } from '../../../services/toast.service';
import { SupportSessionService } from '../../../support-session/support-session.service';
import { ProviderMachineReviewComponent } from './provider-machine-review.component';

function localMachine(overrides: Partial<LocalMachine> = {}): LocalMachine {
  return { id: 1, name: 'Corridor', isActive: true, isProviderNamed: false, ...overrides };
}

function entry(overrides: Partial<ProviderMachineEntry> = {}): ProviderMachineEntry {
  return {
    providerMachineId: 7,
    name: 'Lobby level 1',
    providerMachineNumber: 'A-100',
    localMachineId: null,
    localMachineName: null,
    isLinked: false,
    ...overrides
  };
}

function review(overrides: Partial<ProviderMachineReview> = {}): ProviderMachineReview {
  const base: ProviderMachineReview = {
    reviewedAtUtc: '2026-10-21T02:00:00Z',
    autoLinked: [],
    awaitingReview: [],
    linked: [],
    awaitingReviewCount: 0,
    linkedCount: 0,
    ...overrides
  };
  return { ...base, awaitingReviewCount: base.awaitingReview.length };
}

function linked(overrides: Partial<ProviderMachineLinked> = {}): ProviderMachineLinked {
  return { providerMachineId: 7, localMachineId: 11, name: 'Lobby level 1', created: true, ...overrides };
}

interface ReviewServiceDouble {
  review: jest.Mock<Observable<ProviderMachineReview>, []>;
  apply: jest.Mock<Observable<ProviderMachineLinksApplied>, [ApplyProviderMachineLinksRequest]>;
}

function reviewServiceDouble(
  answers: Observable<ProviderMachineReview>,
  applied: Observable<ProviderMachineLinksApplied> = of({
    appliedAtUtc: '2026-10-21T02:05:00Z',
    links: [linked({ created: false })],
    createdCount: 0,
    linkedCount: 1
  })
): ReviewServiceDouble {
  return {
    review: jest.fn(() => answers),
    apply: jest.fn((_request: ApplyProviderMachineLinksRequest) => applied)
  };
}

async function render(
  options: {
    service?: ReviewServiceDouble;
    machines?: LocalMachine[];
    machinesFail?: boolean;
    sessionActive?: boolean;
  } = {}
) {
  const service = options.service ?? reviewServiceDouble(of(review()));
  const toast = { success: jest.fn(), error: jest.fn() };

  await TestBed.configureTestingModule({
    imports: [ProviderMachineReviewComponent],
    providers: [
      { provide: ProviderMachineReviewService, useValue: service },
      {
        provide: LocalMachineService,
        useValue: {
          getAll: () =>
            options.machinesFail
              ? throwError(() => new HttpErrorResponse({ status: 500 }))
              : of(options.machines ?? [localMachine()])
        }
      },
      { provide: ToastService, useValue: toast },
      { provide: SupportSessionService, useValue: { active$: of(options.sessionActive ?? false) } }
    ]
  }).compileComponents();

  const fixture = TestBed.createComponent(ProviderMachineReviewComponent);
  fixture.detectChanges();

  return {
    fixture,
    component: fixture.componentInstance,
    host: fixture.nativeElement as HTMLElement,
    service,
    toast
  };
}

const radioLabelled = (host: HTMLElement, label: string) =>
  Array.from(host.querySelectorAll<HTMLLabelElement>('label')).find((element) =>
    (element.textContent ?? '').includes(label)
  );

const buttonLabelled = (host: HTMLElement, label: string) =>
  Array.from(host.querySelectorAll<HTMLButtonElement>('button')).find((button) =>
    (button.textContent ?? '').includes(label)
  );

afterEach(() => TestBed.resetTestingModule());

/**
 * Provider machine linking on the Machines page (issue #742, parent #329 child G3). The panel
 * decides nothing: every provider machine and name comes from the API, and the apply carries the
 * operator's decisions alone.
 */
describe('ProviderMachineReviewComponent (issue #742)', () => {
  it('reads the fleet as soon as it is shown and reports what was linked automatically', async () => {
    const { host, service, component } = await render({
      service: reviewServiceDouble(
        of(review({ autoLinked: [linked({ name: 'Gym', providerMachineId: 8 })], linked: [entry()] }))
      )
    });

    expect(service.review).toHaveBeenCalledTimes(1);
    expect(host.textContent).toContain('Added 1 machine(s) from Nayax');
    expect(host.textContent).toContain('Gym');
    // Nothing is left to decide, so no decision control is offered at all.
    expect(host.querySelector('input[type="radio"]')).toBeNull();
    expect(component.awaitingReview).toEqual([]);
  });

  it('asks the page to reload its machine list when the read linked machines by itself', async () => {
    const { component, fixture } = await render({
      service: reviewServiceDouble(of(review({ autoLinked: [linked()] })))
    });
    const reloads = jest.fn();
    component.linked.subscribe(reloads);

    component.check();
    fixture.detectChanges();

    expect(reloads).toHaveBeenCalledTimes(1);
  });

  it('offers skip, link and create for a provider machine waiting for review, and defaults to skip', async () => {
    const { host, component } = await render({
      service: reviewServiceDouble(of(review({ awaitingReview: [entry()] })))
    });

    expect(host.textContent).toContain('Lobby level 1');
    expect(host.textContent).toContain('Nayax machine 7');
    expect(host.textContent).toContain('#A-100');
    expect(host.textContent).toContain('Nothing is matched by name');
    expect(radioLabelled(host, 'Skip for now')).toBeDefined();
    expect(radioLabelled(host, 'It is one of my machines')).toBeDefined();
    expect(radioLabelled(host, 'Add it as a new machine')).toBeDefined();

    // Skip is the default, so an operator who answers nothing links nothing.
    expect(component.answer(7).action).toBe(ProviderMachineLinkAction.Defer);
    expect(component.decidedCount).toBe(0);
    expect(buttonLabelled(host, 'Apply')?.disabled).toBe(true);
  });

  it('sends only the decisions the operator made, with the machine a link names', async () => {
    const service = reviewServiceDouble(
      of(review({ awaitingReview: [entry(), entry({ providerMachineId: 8, name: 'Gym' })] }))
    );
    const { component, fixture, toast } = await render({ service, machines: [localMachine({ id: 4 })] });

    component.setAction(7, ProviderMachineLinkAction.LinkExisting);
    component.setLocalMachine(7, '4');
    fixture.detectChanges();
    component.applyDecisions();

    expect(service.apply).toHaveBeenCalledWith({
      decisions: [{ providerMachineId: 7, action: ProviderMachineLinkAction.LinkExisting, localMachineId: 4 }]
    });
    expect(toast.success).toHaveBeenCalled();
    // Machine 8 was answered nothing about, so it is sent as nothing at all.
    expect(service.apply.mock.calls[0][0].decisions).toHaveLength(1);
  });

  it('sends a create decision without naming a machine', async () => {
    const service = reviewServiceDouble(of(review({ awaitingReview: [entry()] })));
    const { component } = await render({ service });

    component.setAction(7, ProviderMachineLinkAction.CreateNew);
    component.applyDecisions();

    expect(service.apply).toHaveBeenCalledWith({
      decisions: [{ providerMachineId: 7, action: ProviderMachineLinkAction.CreateNew }]
    });
  });

  it('forgets the machine a link named when the answer changes away from linking', async () => {
    const service = reviewServiceDouble(of(review({ awaitingReview: [entry()] })));
    const { component } = await render({ service });

    component.setAction(7, ProviderMachineLinkAction.LinkExisting);
    component.setLocalMachine(7, 4);
    component.setAction(7, ProviderMachineLinkAction.CreateNew);

    expect(component.answer(7).localMachineId).toBeNull();
  });

  it('refuses a link that names no machine locally rather than throwing the other answers away', async () => {
    const service = reviewServiceDouble(
      of(review({ awaitingReview: [entry(), entry({ providerMachineId: 8, name: 'Gym' })] }))
    );
    const { component, toast } = await render({ service });

    component.setAction(8, ProviderMachineLinkAction.CreateNew);
    component.setAction(7, ProviderMachineLinkAction.LinkExisting);
    component.applyDecisions();

    expect(service.apply).not.toHaveBeenCalled();
    expect(toast.error).toHaveBeenCalledWith(ProviderMachineReviewComponent.UnnamedLinkMessage);
  });

  it('offers only the machines the provider does not already name, marking a retired one', async () => {
    const { component } = await render({
      service: reviewServiceDouble(of(review({ awaitingReview: [entry()] }))),
      machines: [
        localMachine({ id: 1, name: 'Corridor' }),
        localMachine({ id: 2, name: 'Retired bay', isActive: false }),
        localMachine({ id: 3, name: 'Already Nayax', isProviderNamed: true })
      ]
    });

    // A machine the provider already names would be refused by the server's own link rule, so
    // offering it would only produce a refusal. A retired machine is offered: linking it beats
    // duplicating it.
    expect(component.candidates.map((machine) => machine.id)).toEqual([1, 2]);
  });

  it('reports a failed fleet read in place of its content, and claims nothing about the machines', async () => {
    const { host, component, toast } = await render({
      service: reviewServiceDouble(throwError(() => new HttpErrorResponse({ status: 502 })))
    });

    expect(host.textContent).toContain('could not be read');
    expect(host.textContent).toContain('Nothing about your machines');
    expect(component.review).toBeNull();
    expect(toast.error).toHaveBeenCalled();
  });

  it('says so when the business own machines could not be listed, so linking is unavailable', async () => {
    const { host, component } = await render({
      service: reviewServiceDouble(of(review({ awaitingReview: [entry()] }))),
      machinesFail: true
    });

    expect(component.machinesFailed).toBe(true);
    expect(component.candidates).toEqual([]);
    expect(host.textContent).toContain('could not be listed');
  });

  it('states that every provider machine is linked when there is nothing to decide', async () => {
    const { host } = await render({
      service: reviewServiceDouble(of(review({ linked: [entry({ localMachineId: 11 })], linkedCount: 1 })))
    });

    expect(host.textContent).toContain('All 1 Nayax machine(s) are linked');
  });

  it('reads nothing and renders nothing in a support view', async () => {
    const service = reviewServiceDouble(of(review({ awaitingReview: [entry()] })));
    const { host, service: used } = await render({ service, sessionActive: true });

    // Both calls are writes the read-only session rule refuses, and the Nayax credentials they need
    // are refused in that state anyway (issues #551, #661).
    expect(used.review).not.toHaveBeenCalled();
    expect(host.querySelector('section')).toBeNull();
  });

  it('shows no panel at all for a fleet with nothing in it', async () => {
    const { host, component } = await render({ service: reviewServiceDouble(of(review())) });

    expect(component.hasSomethingToReport).toBe(false);
    expect(host.querySelector('section')).toBeNull();
  });
});
