import { HttpErrorResponse } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { Observable, of, throwError } from 'rxjs';
import { LocalMachine } from '../../../models/models';
import { LocalMachineService } from '../../../services/local-machine.service';
import { SupportSessionService } from '../../../support-session/support-session.service';
import { GENERIC_MACHINE_WRITE_ERROR, LocalMachineListComponent } from './local-machine-list.component';

function localMachine(overrides: Partial<LocalMachine> = {}): LocalMachine {
  return { id: 1, name: 'Atrium', isActive: true, isProviderNamed: false, ...overrides };
}

interface ServiceDouble {
  getAll: jest.Mock<Observable<LocalMachine[]>, []>;
  create: jest.Mock;
  rename: jest.Mock;
  deactivate: jest.Mock;
  reactivate: jest.Mock;
}

function serviceDouble(machines: LocalMachine[]): ServiceDouble {
  return {
    getAll: jest.fn(() => of(machines)),
    create: jest.fn(() => of(localMachine())),
    rename: jest.fn(() => of(undefined)),
    deactivate: jest.fn(() => of(undefined)),
    reactivate: jest.fn(() => of(undefined))
  };
}

async function render(options: { machines?: LocalMachine[]; service?: ServiceDouble; sessionActive?: boolean } = {}) {
  const service = options.service ?? serviceDouble(options.machines ?? [localMachine()]);

  await TestBed.configureTestingModule({
    imports: [LocalMachineListComponent],
    providers: [
      { provide: LocalMachineService, useValue: service },
      { provide: SupportSessionService, useValue: { active$: of(options.sessionActive ?? false) } }
    ]
  }).compileComponents();

  const fixture = TestBed.createComponent(LocalMachineListComponent);
  fixture.detectChanges();

  return { fixture, component: fixture.componentInstance, host: fixture.nativeElement as HTMLElement, service };
}

const addForm = (host: HTMLElement) => host.querySelector('#newMachineName');
const buttonLabelled = (host: HTMLElement, label: string) =>
  Array.from(host.querySelectorAll<HTMLButtonElement>('button')).find((button) =>
    (button.textContent ?? '').includes(label)
  );

afterEach(() => TestBed.resetTestingModule());

/**
 * The business's own machine records and the actions that maintain them (issue #740, parent #329
 * child G1). Nothing here reads the provider: the whole component works for a business that has
 * never connected one, which is the behaviour the issue exists for.
 */
describe('LocalMachineListComponent (issue #740)', () => {
  it('lists active and inactive machines, saying which the provider names', async () => {
    const { host } = await render({
      machines: [
        localMachine({ id: 1, name: 'Atrium', isProviderNamed: true }),
        localMachine({ id: 2, name: 'Zebra bay', isActive: false })
      ]
    });

    expect(host.textContent).toContain('Atrium');
    expect(host.textContent).toContain('From Nayax');
    // A deactivated machine stays listed: it is history an operator may reactivate.
    expect(host.textContent).toContain('Zebra bay');
    expect(host.textContent).toContain('Inactive');
  });

  it('shows an empty state that invites the first machine, rather than nothing at all', async () => {
    const { host } = await render({ machines: [] });

    expect(host.textContent).toContain('No machines recorded yet.');
    expect(addForm(host)).not.toBeNull();
  });

  it('reports a failed load as a failure rather than as a business with no machines', async () => {
    const service = serviceDouble([]);
    service.getAll.mockReturnValue(throwError(() => new Error('boom')));
    const { host } = await render({ service });

    expect(host.textContent).toContain('Failed to load your machines.');
    expect(host.textContent).not.toContain('No machines recorded yet.');
  });

  it('creates a machine from the typed name, trimmed, and reloads the list', async () => {
    const { component, service } = await render({ machines: [] });

    component.newName = '  Lobby level 1  ';
    component.add();

    expect(service.create).toHaveBeenCalledWith('Lobby level 1');
    expect(service.getAll).toHaveBeenCalledTimes(2);
    expect(component.newName).toBe('');
  });

  it('refuses a blank name without calling the API', async () => {
    const { component, service } = await render({ machines: [] });

    component.newName = '   ';
    component.add();

    expect(service.create).not.toHaveBeenCalled();
    expect(component.error).toBe('Machine name is required.');
  });

  it('shows the refusal the server worded, and does not claim the change was made', async () => {
    const service = serviceDouble([]);
    service.create.mockReturnValue(
      throwError(() => new HttpErrorResponse({ status: 400, error: 'Machine name is required.' }))
    );
    const { component } = await render({ service });

    component.newName = 'Anything';
    component.add();

    expect(component.error).toBe('Machine name is required.');
    // Only the initial load: a refused write must not be followed by a reload that looks like
    // success.
    expect(service.getAll).toHaveBeenCalledTimes(1);
  });

  it('falls back to one plain sentence for a failure the server did not word', async () => {
    const service = serviceDouble([]);
    service.create.mockReturnValue(throwError(() => new HttpErrorResponse({ status: 500 })));
    const { component } = await render({ service });

    component.newName = 'Anything';
    component.add();

    expect(component.error).toBe(GENERIC_MACHINE_WRITE_ERROR);
  });

  it('renames a machine from the inline draft and leaves the draft closed afterwards', async () => {
    const { component, service } = await render({ machines: [localMachine({ id: 7, name: 'Machine 7' })] });

    component.startRename(localMachine({ id: 7, name: 'Machine 7' }));
    expect(component.editingName).toBe('Machine 7');

    component.editingName = ' Lobby level 1 ';
    component.saveRename();

    expect(service.rename).toHaveBeenCalledWith(7, 'Lobby level 1');
    expect(component.editingId).toBeNull();
    expect(service.getAll).toHaveBeenCalledTimes(2);
  });

  it('refuses a blank rename without calling the API', async () => {
    const { component, service } = await render({ machines: [localMachine({ id: 7 })] });

    component.startRename(localMachine({ id: 7 }));
    component.editingName = '  ';
    component.saveRename();

    expect(service.rename).not.toHaveBeenCalled();
    expect(component.error).toBe('Machine name is required.');
  });

  it('deactivates and reactivates a machine, and never deletes one', async () => {
    const { component, service, host } = await render({ machines: [localMachine({ id: 7 })] });

    component.deactivate(localMachine({ id: 7 }));
    expect(service.deactivate).toHaveBeenCalledWith(7);

    component.reactivate(localMachine({ id: 7, isActive: false }));
    expect(service.reactivate).toHaveBeenCalledWith(7);

    expect(buttonLabelled(host, 'Delete')).toBeUndefined();
  });

  it('offers Rename and Deactivate on an active machine, and Reactivate on an inactive one', async () => {
    const active = await render({ machines: [localMachine({ id: 7, isActive: true })] });
    expect(buttonLabelled(active.host, 'Rename')).toBeDefined();
    expect(buttonLabelled(active.host, 'Deactivate')).toBeDefined();
    expect(buttonLabelled(active.host, 'Reactivate')).toBeUndefined();

    TestBed.resetTestingModule();

    const inactive = await render({ machines: [localMachine({ id: 7, isActive: false })] });
    expect(buttonLabelled(inactive.host, 'Reactivate')).toBeDefined();
    expect(buttonLabelled(inactive.host, 'Deactivate')).toBeUndefined();
  });
});

/**
 * A read-only support session grants `Inventory.Operate`, which is what this surface would otherwise
 * gate its writes on, so the controls are hidden rather than left to be refused (issues #554, #658).
 * The list itself stays readable: that is what a support session is for.
 */
describe('LocalMachineListComponent in a support session (issue #658)', () => {
  it('hides every write control and still lists the machines', async () => {
    const { host } = await render({ machines: [localMachine({ id: 7 })], sessionActive: true });

    expect(host.textContent).toContain('Atrium');
    expect(addForm(host)).toBeNull();
    expect(buttonLabelled(host, 'Rename')).toBeUndefined();
    expect(buttonLabelled(host, 'Deactivate')).toBeUndefined();
  });

  it('refuses the writes in code too, so they are unavailable rather than merely invisible', async () => {
    const { component, service } = await render({ machines: [localMachine({ id: 7 })], sessionActive: true });

    component.newName = 'Smuggled in';
    component.add();
    component.startRename(localMachine({ id: 7 }));
    component.editingName = 'Renamed';
    component.saveRename();
    component.deactivate(localMachine({ id: 7 }));
    component.reactivate(localMachine({ id: 7 }));

    expect(service.create).not.toHaveBeenCalled();
    expect(service.rename).not.toHaveBeenCalled();
    expect(service.deactivate).not.toHaveBeenCalled();
    expect(service.reactivate).not.toHaveBeenCalled();
  });
});
