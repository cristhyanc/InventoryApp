import { HttpErrorResponse } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { Observable, of, throwError } from 'rxjs';
import { AccessService } from '../../access/access.service';
import { BusinessTimeZoneService } from '../../formatting/business-time-zone.service';
import { Machine, PickListResult, Product } from '../../models/models';
import {
  SpecConnectionStatus,
  provideNayaxConnectionStatus
} from '../../provider/provider-availability.spec-helpers';
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

function notConnected(): HttpErrorResponse {
  return new HttpErrorResponse({
    status: 409,
    error: { code: 'nayax_not_connected', nayaxConnectionStatus: 'NotConfigured' }
  });
}

async function render(options: { status?: SpecConnectionStatus; machines?: Observable<Machine[]> } = {}) {
  await TestBed.configureTestingModule({
    imports: [PickListComponent],
    providers: [
      provideRouter([]),
      {
        provide: MachineService,
        useValue: { getAll: jest.fn(() => options.machines ?? of([machine(10, 'Machine A')])) }
      },
      { provide: ProductService, useValue: { getAll: jest.fn(() => of([product()])) } },
      { provide: PickListService, useValue: { get: jest.fn(() => of(pickListResult())) } },
      { provide: ToastService, useValue: { success: jest.fn(), error: jest.fn(), warning: jest.fn(), info: jest.fn() } },
      { provide: AccessService, useValue: { allows: () => of(true) } },
      { provide: SupportSessionService, useValue: { active$: of(false) } },
      provideNayaxConnectionStatus(options.status ?? 'Ready')
    ]
  }).compileComponents();

  TestBed.inject(BusinessTimeZoneService).publish('Australia/Sydney');

  const fixture = TestBed.createComponent(PickListComponent);
  fixture.detectChanges();

  return {
    component: fixture.componentInstance,
    host: fixture.nativeElement as HTMLElement,
    machineService: TestBed.inject(MachineService) as unknown as { getAll: jest.Mock }
  };
}

const machinesDropdown = (host: HTMLElement) =>
  Array.from(host.querySelectorAll<HTMLButtonElement>('button')).find((button) =>
    button.getAttribute('aria-label')?.startsWith('Machines:')
  );
const unavailableNotice = (host: HTMLElement) => host.querySelector('[data-testid="pick-list-provider-unavailable"]');
const machinesError = (host: HTMLElement) => host.querySelector('[data-testid="pick-list-machines-error"]');

afterEach(() => TestBed.resetTestingModule());

/**
 * The Pick List without a usable provider (issue #728). The whole page is a live fleet read, so
 * there is one honest thing to say and it is not "No machines selected yet".
 */
describe('PickListComponent provider-optional page (issue #728)', () => {
  it('offers the machine selection for a connected business', async () => {
    const { host } = await render();

    expect(machinesDropdown(host)).toBeDefined();
    expect(unavailableNotice(host)).toBeNull();
    expect(machinesError(host)).toBeNull();
    expect(host.textContent).toContain('Select at least one machine to build a pick list.');
  });

  it('states why there is no pick list without a provider, and asks for no machines', async () => {
    const { host, machineService } = await render({ status: 'NotConfigured' });

    expect(machineService.getAll).not.toHaveBeenCalled();
    expect(machinesDropdown(host)).toBeUndefined();
    expect(unavailableNotice(host)?.textContent?.trim()).toBe(
      'The pick list is unavailable because Nayax is not connected.'
    );
    expect(host.textContent).not.toContain('Select at least one machine to build a pick list.');
    expect(host.textContent).not.toContain('No machines selected yet.');
  });

  it('states the same for a connection that needs attention, which no live read may use', async () => {
    const { host, machineService } = await render({ status: 'NeedsAttention' });

    expect(machineService.getAll).not.toHaveBeenCalled();
    expect(unavailableNotice(host)).not.toBeNull();
  });

  it('states it from the machine read when that is where the refusal came from', async () => {
    const { host, machineService } = await render({
      status: 'unreadable',
      machines: throwError(() => notConnected())
    });

    expect(machineService.getAll).toHaveBeenCalledTimes(1);
    expect(unavailableNotice(host)).not.toBeNull();
    expect(machinesError(host)).toBeNull();
  });

  it('reports a failed machine read as a failure, never as an empty machine selection', async () => {
    const { host } = await render({ machines: throwError(() => new HttpErrorResponse({ status: 502 })) });

    expect(machinesDropdown(host)).toBeUndefined();
    expect(unavailableNotice(host)).toBeNull();
    expect(machinesError(host)?.textContent?.trim()).toBe(
      'The machine list could not be loaded, so a pick list cannot be built.'
    );
  });

  it('leaves a business with a provider and no machines able to select, with no claim about Nayax', async () => {
    const { host } = await render({ machines: of([]) });

    expect(machinesDropdown(host)).toBeDefined();
    expect(unavailableNotice(host)).toBeNull();
    expect(machinesError(host)).toBeNull();
  });
});
