import { TestBed } from '@angular/core/testing';
import { of } from 'rxjs';
import { SupplierGstDefaultsDialogComponent } from './supplier-gst-defaults-dialog.component';
import { SupplierService } from '../../../services/supplier.service';
import { AccessService } from '../../../access/access.service';
import { GstClassification, Supplier } from '../../../models/models';

function supplier(overrides: Partial<Supplier> = {}): Supplier {
  return {
    id: 7,
    name: 'Wholesale Co',
    contactName: null,
    phone: null,
    email: null,
    address: null,
    url: null,
    isLinked: false,
    isHidden: false,
    canRename: true,
    canDelete: true,
    ...overrides
  };
}

async function render(getGstDefaults = jest.fn(() => of({
  supplierId: 7,
  productLineGstDefault: GstClassification.Unknown,
  deliveryGstDefault: GstClassification.Unknown,
  packageGstDefault: GstClassification.Unknown
}))) {
  await TestBed.configureTestingModule({
    imports: [SupplierGstDefaultsDialogComponent],
    providers: [
      { provide: SupplierService, useValue: { getGstDefaults, setGstDefaults: jest.fn(() => of(undefined)) } },
      { provide: AccessService, useValue: { allows: () => of(true) } }
    ]
  }).compileComponents();

  const fixture = TestBed.createComponent(SupplierGstDefaultsDialogComponent);
  fixture.componentRef.setInput('supplier', supplier());
  fixture.detectChanges();

  const host = fixture.nativeElement as HTMLElement;
  const closed = jest.fn();
  fixture.componentInstance.closed.subscribe(closed);

  return { fixture, host, getGstDefaults, closed };
}

describe('SupplierGstDefaultsDialogComponent', () => {
  it('composes the existing GST defaults panel into the shared dialog shell, titled with the supplier', async () => {
    const { host, getGstDefaults } = await render();

    const dialog = host.querySelector('[data-testid="supplier-gst-defaults-dialog"]') as HTMLElement;
    expect(dialog).not.toBeNull();
    expect(dialog.textContent).toContain('GST defaults - Wholesale Co');
    expect(dialog.querySelector('app-supplier-gst-defaults')).not.toBeNull();
    expect(getGstDefaults).toHaveBeenCalledWith(7);
  });

  it('reports closed when the Close control is pressed', async () => {
    const { fixture, host, closed } = await render();

    const dialog = host.querySelector('[data-testid="supplier-gst-defaults-dialog"]') as HTMLElement;
    const close = Array.from(dialog.querySelectorAll<HTMLButtonElement>('button')).find(
      (button) => button.textContent?.trim() === 'Close'
    )!;
    close.click();
    fixture.detectChanges();

    expect(closed).toHaveBeenCalledTimes(1);
  });

  it('reports closed when the shell reports closeRequested, e.g. from Escape', async () => {
    const { fixture, host, closed } = await render();

    const dialog = host.querySelector('[data-testid="supplier-gst-defaults-dialog"]') as HTMLDialogElement;
    dialog.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
    fixture.detectChanges();

    expect(closed).toHaveBeenCalledTimes(1);
  });
});
