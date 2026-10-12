import { TestBed } from '@angular/core/testing';
import { BehaviorSubject, NEVER, Observable, of, Subject, throwError } from 'rxjs';
import { SupplierGstDefaultsComponent } from './supplier-gst-defaults.component';
import { AccessService } from '../../../access/access.service';
import { CAPABILITIES, Capability } from '../../../access/capabilities';
import { apiConfig, queuedReads, readingHttp, signedInIdentity } from '../../../access/identity-cache.spec-helpers';
import { SupplierService } from '../../../services/supplier.service';
import { GstClassification, SupplierGstDefaults } from '../../../models/models';

const noDefaults = {
  supplierId: 7,
  productLineGstDefault: GstClassification.Unknown,
  deliveryGstDefault: GstClassification.Unknown,
  packageGstDefault: GstClassification.Unknown
};

const defaultsFor = (
  supplierId: number,
  productLineGstDefault: GstClassification,
  deliveryGstDefault: GstClassification,
  packageGstDefault: GstClassification
): SupplierGstDefaults => ({
  supplierId,
  productLineGstDefault,
  deliveryGstDefault,
  packageGstDefault
});

/**
 * The capability answer is stubbed: the component renders what `GET /api/me/access` reported and
 * decides nothing itself, and the write is refused by the API whatever it renders (issue #528).
 * Granted by default, which is what every role but an Operator holds. `access` replaces the stub
 * with the real service for the tests that are about when it answers (issue #603).
 */
async function render(
  getGstDefaults: jest.Mock = jest.fn(() => of(noDefaults)),
  setGstDefaults: jest.Mock = jest.fn(() => of(undefined)),
  canManage: Observable<boolean> = of(true),
  access?: AccessService
) {
  const allows = jest.fn((_capability: Capability) => canManage);

  await TestBed.configureTestingModule({
    imports: [SupplierGstDefaultsComponent],
    providers: [
      { provide: SupplierService, useValue: { getGstDefaults, setGstDefaults } },
      { provide: AccessService, useValue: access ?? { allows } }
    ]
  }).compileComponents();

  const fixture = TestBed.createComponent(SupplierGstDefaultsComponent);
  fixture.componentRef.setInput('supplierId', 7);
  fixture.componentRef.setInput('supplierName', 'Wholesale Co');
  fixture.detectChanges();

  const host = fixture.nativeElement as HTMLElement;
  const saveButton = () =>
    Array.from(host.querySelectorAll<HTMLButtonElement>('button')).find((button) =>
      button.textContent?.trim().startsWith('Save')
    )!;
  const save = () => {
    saveButton().click();
    fixture.detectChanges();
  };
  const openSupplier = (supplierId: number, supplierName: string) => {
    fixture.componentRef.setInput('supplierId', supplierId);
    fixture.componentRef.setInput('supplierName', supplierName);
    fixture.detectChanges();
  };

  return { fixture, host, saveButton, save, openSupplier, getGstDefaults, setGstDefaults, allows, canManage };
}

describe('SupplierGstDefaultsComponent', () => {
  it('loads the supplier defaults into three separate pickers', async () => {
    const { fixture, host, getGstDefaults } = await render(
      jest.fn(() =>
        of({
          supplierId: 7,
          productLineGstDefault: GstClassification.GstFree,
          deliveryGstDefault: GstClassification.Taxable,
          packageGstDefault: GstClassification.Unknown
        })
      )
    );

    expect(getGstDefaults).toHaveBeenCalledWith(7);
    expect(fixture.componentInstance.form).toEqual({
      productLineGstDefault: GstClassification.GstFree,
      deliveryGstDefault: GstClassification.Taxable,
      packageGstDefault: GstClassification.Unknown
    });
    expect(host.querySelector('#supplier-gst-product-lines')).not.toBeNull();
    expect(host.querySelector('#supplier-gst-delivery')).not.toBeNull();
    expect(host.querySelector('#supplier-gst-package')).not.toBeNull();
    expect(host.textContent).toContain('Wholesale Co');
  });

  /**
   * The charge defaults must be sent as their own values. A delivery charge never inherits the
   * product-line default (parent issue #62), so a save that collapsed the three into one would be
   * wrong even though every value it sent was valid.
   */
  it('saves the three defaults independently', async () => {
    const { fixture, save, setGstDefaults } = await render();

    fixture.componentInstance.form = {
      productLineGstDefault: GstClassification.GstFree,
      deliveryGstDefault: GstClassification.Taxable,
      packageGstDefault: GstClassification.Unknown
    };
    fixture.detectChanges();
    save();

    expect(setGstDefaults).toHaveBeenCalledWith(7, {
      productLineGstDefault: GstClassification.GstFree,
      deliveryGstDefault: GstClassification.Taxable,
      packageGstDefault: GstClassification.Unknown
    });
  });

  it('offers no-default, taxable and GST-free for every picker', async () => {
    const { host } = await render();

    const labels = Array.from(host.querySelectorAll<HTMLSelectElement>('select')).map((select) =>
      Array.from(select.querySelectorAll('option')).map((option) => option.textContent?.trim())
    );

    expect(labels).toEqual([
      ['No default', 'Taxable', 'GST-free'],
      ['No default', 'Taxable', 'GST-free'],
      ['No default', 'Taxable', 'GST-free']
    ]);
  });

  it('reports a failed save and does not claim it succeeded', async () => {
    const { host, save } = await render(
      undefined,
      jest.fn(() => throwError(() => new Error('rejected')))
    );

    save();

    expect(host.textContent).toContain('Failed to save the GST defaults.');
    expect(host.textContent).not.toContain('GST defaults saved.');
  });

  it('reports a failed load', async () => {
    const { host } = await render(jest.fn(() => throwError(() => new Error('unavailable'))));

    expect(host.textContent).toContain('Failed to load the GST defaults.');
  });

  /**
   * A failed read leaves the form on its "no default" placeholders, which are a real value the API
   * would store. Saving them would erase whatever defaults the supplier already has, so saving must
   * stay impossible until the stored defaults have actually been read back.
   */
  it('keeps saving disabled after a failed load, so a failed GET cannot lead to a PUT', async () => {
    const { fixture, saveButton, save, setGstDefaults } = await render(
      jest.fn(() => throwError(() => new Error('unavailable')))
    );

    expect(fixture.componentInstance.loaded).toBe(false);
    expect(saveButton().disabled).toBe(true);
    save();
    fixture.componentInstance.save();

    expect(setGstDefaults).not.toHaveBeenCalled();
  });

  it('keeps saving disabled while the defaults are still loading', async () => {
    const { saveButton, fixture, setGstDefaults } = await render(jest.fn(() => new Subject<SupplierGstDefaults>()));

    expect(saveButton().disabled).toBe(true);
    fixture.componentInstance.save();

    expect(setGstDefaults).not.toHaveBeenCalled();
  });

  /**
   * Opening supplier A and then B leaves A's read in flight. If A's slower response populated the
   * form, the next save would write A's defaults onto B. The read for the supplier no longer on
   * screen must be abandoned, whenever it finishes.
   */
  it('ignores a slower read for a supplier that is no longer on screen', async () => {
    const reads = new Map<number, Subject<SupplierGstDefaults>>();
    const getGstDefaults = jest.fn((id: number) => {
      const read = new Subject<SupplierGstDefaults>();
      reads.set(id, read);
      return read;
    });
    const { fixture, host, save, openSupplier, setGstDefaults } = await render(getGstDefaults);

    openSupplier(8, 'Bulk Supplies');
    reads.get(8)!.next(defaultsFor(8, GstClassification.Taxable, GstClassification.Unknown, GstClassification.GstFree));
    fixture.detectChanges();

    // Supplier 7's response arrives out of order, after supplier 8 is already on screen.
    reads.get(7)!.next(defaultsFor(7, GstClassification.GstFree, GstClassification.GstFree, GstClassification.GstFree));
    fixture.detectChanges();

    expect(fixture.componentInstance.form).toEqual({
      productLineGstDefault: GstClassification.Taxable,
      deliveryGstDefault: GstClassification.Unknown,
      packageGstDefault: GstClassification.GstFree
    });
    expect(host.textContent).toContain('Bulk Supplies');

    save();

    expect(setGstDefaults).toHaveBeenCalledTimes(1);
    expect(setGstDefaults).toHaveBeenCalledWith(8, {
      productLineGstDefault: GstClassification.Taxable,
      deliveryGstDefault: GstClassification.Unknown,
      packageGstDefault: GstClassification.GstFree
    });
  });

  /**
   * The same ordering problem applies to a save: its outcome belongs to the supplier it was issued
   * for, so it must not be reported on - or re-enable - the form of a supplier opened since.
   */
  it('does not report a save for one supplier on the form of another', async () => {
    const saves = new Map<number, Subject<void>>();
    const setGstDefaults = jest.fn((id: number) => {
      const pending = new Subject<void>();
      saves.set(id, pending);
      return pending;
    });
    const { fixture, host, save, openSupplier } = await render(
      jest.fn((id: number) => of(defaultsFor(id, GstClassification.Taxable, GstClassification.Unknown, GstClassification.Unknown))),
      setGstDefaults
    );

    save();
    openSupplier(8, 'Bulk Supplies');

    saves.get(7)!.next();
    saves.get(7)!.complete();
    fixture.detectChanges();

    expect(host.textContent).not.toContain('GST defaults saved.');
    expect(fixture.componentInstance.saved).toBe(false);
  });

  it('does not report a failed save for one supplier on the form of another', async () => {
    const saves = new Map<number, Subject<void>>();
    const setGstDefaults = jest.fn((id: number) => {
      const pending = new Subject<void>();
      saves.set(id, pending);
      return pending;
    });
    const { fixture, host, save, openSupplier } = await render(
      jest.fn((id: number) => of(defaultsFor(id, GstClassification.Taxable, GstClassification.Unknown, GstClassification.Unknown))),
      setGstDefaults
    );

    save();
    openSupplier(8, 'Bulk Supplies');

    saves.get(7)!.error(new Error('rejected'));
    fixture.detectChanges();

    expect(host.textContent).not.toContain('Failed to save the GST defaults.');
    expect(fixture.componentInstance.error).toBe('');
  });

  /**
   * The copy the acceptance criteria call for: the page has to say that a supplier default applies
   * only where no product rule exists, and that configuring one changes no recorded purchase.
   */
  it('explains that a default applies only where no product rule exists', async () => {
    const { host } = await render();

    expect(host.textContent).toContain('applies only where the purchased product has no GST rule of its own');
    expect(host.textContent).toContain('never inherit the product-line default');
    expect(host.textContent).toContain('does not change the GST classification of any');
  });
});

/**
 * The editor is the one action this page hides by role (issue #528). Reading the defaults stays on
 * `Purchasing.Operate`, because an Operator recording a purchase needs to see which classification
 * it will be given; changing them needs `Suppliers.ManageGstDefaults`. The API enforces the same
 * split, so this hides a control rather than protecting the configuration.
 */
describe('SupplierGstDefaultsComponent without Suppliers.ManageGstDefaults (issue #528)', () => {
  function withoutManage(getGstDefaults?: jest.Mock, setGstDefaults?: jest.Mock) {
    return render(
      getGstDefaults ?? jest.fn(() => of(noDefaults)),
      setGstDefaults ?? jest.fn(() => of(undefined)),
      of(false)
    );
  }

  it('asks the access service for exactly that capability', async () => {
    const { allows } = await render();

    expect(allows).toHaveBeenCalledWith('Suppliers.ManageGstDefaults');
  });

  it('renders no Save control', async () => {
    const { host } = await withoutManage();

    const buttons = Array.from(host.querySelectorAll('button')).map((button) => button.textContent?.trim() ?? '');
    expect(buttons.some((text) => text.startsWith('Save'))).toBe(false);
    expect(host.textContent).toContain('Your role can see these defaults but not change them');
  });

  it('still shows the stored defaults, read-only, because the classification is worth seeing', async () => {
    const { host, fixture, getGstDefaults } = await withoutManage(
      jest.fn(() =>
        of({
          supplierId: 7,
          productLineGstDefault: GstClassification.GstFree,
          deliveryGstDefault: GstClassification.Taxable,
          packageGstDefault: GstClassification.Unknown
        })
      )
    );

    expect(getGstDefaults).toHaveBeenCalledWith(7);
    expect(fixture.componentInstance.form.productLineGstDefault).toBe(GstClassification.GstFree);
    for (const select of Array.from(host.querySelectorAll<HTMLSelectElement>('select'))) {
      expect(select.disabled).toBe(true);
    }
  });

  it('issues no PUT even if save() is reached some other way', async () => {
    const setGstDefaults = jest.fn(() => of(undefined));
    const { fixture } = await withoutManage(undefined, setGstDefaults);

    fixture.componentInstance.save();

    expect(setGstDefaults).not.toHaveBeenCalled();
  });

  it('offers the control again when a refreshed capability set allows it', async () => {
    const canManage = new BehaviorSubject(false);
    const { fixture, host } = await render(undefined, undefined, canManage);
    expect(host.querySelectorAll('button')).toHaveLength(0);

    canManage.next(true);
    fixture.detectChanges();

    expect(host.querySelector('button')?.textContent?.trim()).toBe('Save GST defaults');
  });

  /**
   * The fail-open rule: an unanswered or unreadable capability set must not take the editor away
   * from the Owner it belongs to, because the API refuses the write regardless.
   */
  it('keeps the control while the capability set has not answered', async () => {
    const { host } = await render(undefined, undefined, NEVER);

    expect(host.querySelector('button')?.textContent?.trim()).toBe('Save GST defaults');
  });
});

/**
 * The Save control after a Microsoft account change (issue #603). This editor is the one action
 * the capability matrix hides, and it holds its subscription for as long as it is on screen, so it
 * is the consumer that would otherwise keep presenting the previous account's permission while the
 * new account's capabilities are still being read. The real `AccessService` answers here, with
 * only its one `GET` and MSAL's two signals doubled: the point of the test is when it answers.
 */
describe('SupplierGstDefaultsComponent across a Microsoft account change (issue #603)', () => {
  const OWNER_RESPONSE = {
    role: 'Owner',
    capabilities: [...CAPABILITIES],
    businessName: 'Vending Co',
    timeZoneId: 'Australia/Sydney'
  };
  const OPERATOR_RESPONSE = {
    role: 'Operator',
    capabilities: ['Dashboard.View', 'PickList.View', 'Inventory.Operate', 'Purchasing.Operate'],
    businessName: 'Other Co',
    timeZoneId: 'Australia/Sydney'
  };

  function accessWithSwitch(reads: (() => unknown)[]): { access: AccessService; switchAccount(id: string): void } {
    const identity = signedInIdentity();
    const read = queuedReads(reads, OWNER_RESPONSE);
    const access = new AccessService(readingHttp(read), apiConfig(), identity.msal, identity.broadcast);

    return { access, switchAccount: identity.switchAccount };
  }

  it('takes the Save control away at once, and no late answer gives it back', async () => {
    const stale = new Subject<unknown>();
    const pending = new Subject<unknown>();
    const setGstDefaults = jest.fn(() => of(undefined));
    const { access, switchAccount } = accessWithSwitch([() => of(OWNER_RESPONSE), () => stale, () => pending]);
    const { fixture, host } = await render(undefined, setGstDefaults, undefined, access);
    expect(host.querySelector('button')?.textContent?.trim()).toBe('Save GST defaults');

    // The previous identity's own re-read - what a `403` triggers - still in flight when the
    // Microsoft account changes.
    access.refresh();
    switchAccount('member-2');
    fixture.detectChanges();

    expect(host.querySelectorAll('button')).toHaveLength(0);
    expect(fixture.componentInstance.canManage).toBe(false);
    fixture.componentInstance.save();
    expect(setGstDefaults).not.toHaveBeenCalled();

    stale.next(OWNER_RESPONSE);
    stale.complete();
    fixture.detectChanges();

    // The previous Owner answering last cannot hand the next member their control.
    expect(host.querySelectorAll('button')).toHaveLength(0);

    pending.next(OPERATOR_RESPONSE);
    pending.complete();
    fixture.detectChanges();

    expect(host.querySelectorAll('button')).toHaveLength(0);
    expect(host.textContent).toContain('Your role can see these defaults but not change them');
  });

  /** The fail-open rule still holds for the identity signed in now, and only for it. */
  it('offers the control again when the new identity’s own capabilities allow it', async () => {
    const pending = new Subject<unknown>();
    const { access, switchAccount } = accessWithSwitch([() => of(OWNER_RESPONSE), () => pending]);
    const { fixture, host } = await render(undefined, undefined, undefined, access);

    switchAccount('member-2');
    fixture.detectChanges();
    expect(host.querySelectorAll('button')).toHaveLength(0);

    pending.next(OWNER_RESPONSE);
    pending.complete();
    fixture.detectChanges();

    expect(host.querySelector('button')?.textContent?.trim()).toBe('Save GST defaults');
  });
});
