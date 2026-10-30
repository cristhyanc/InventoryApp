import { HttpErrorResponse } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { BehaviorSubject, NEVER, Observable, of, throwError } from 'rxjs';
import { AccessService, MemberAccessState } from '../../../access/access.service';
import { capabilitySet } from '../../../access/capabilities';
import { Entitlement } from '../../../access/entitlement';
import { planLockSummary } from '../../../access/plan-lock';
import { AdminImportsComponent, salesFileImportPlanLockedMessage } from './admin-imports.component';
import { ImportService } from '../../../services/import.service';
import { ToastService } from '../../../services/toast.service';
import { SupportSessionService } from '../../../support-session/support-session.service';

/** The state `AccessService` publishes before anything has been read - this page's default here. */
const UNREAD: MemberAccessState = { status: 'unread' };

/**
 * A read access answer carrying `entitlement`, for the plan lock of issue #979. The capability is
 * the one the route already requires: the page is reached through `requireCapability('Imports.Run')`,
 * so a member who holds no such role never gets this far and the plan is never the reason they do
 * not see the page.
 */
function granted(entitlement?: Entitlement): MemberAccessState {
  return {
    status: 'granted',
    access: {
      role: 'Owner',
      capabilities: capabilitySet(['Imports.Run']),
      businessName: 'Vending Co',
      timeZoneId: 'Australia/Sydney',
      entitlement
    }
  };
}

/** An enforced entitlement on `plan`, which is what makes a plan lock apply at all. */
function enforced(plan: string | null): Entitlement {
  return { enforced: true, plan, source: 'ManualGrant', endsAtUtc: null };
}

/** The shipped state until issue #966's rollout is switched on: nothing is enforced by plan. */
const ENFORCEMENT_OFF: Entitlement = { enforced: false, plan: null, source: 'EnforcementDisabled', endsAtUtc: null };

/**
 * Since issue #888 this page runs two imports - the Nayax sales upload and the pending
 * reimbursement XML - and hosts no catalogue review: the product catalogue import has its own page
 * at `/products/import` (`ProductImportPageComponent`, with its own spec) and this page's Import
 * products card is a link to it.
 */
async function render(
  importService: Partial<ImportService>,
  toast?: Partial<ToastService>,
  sessionActive: Observable<boolean> = of(false),
  access: Observable<MemberAccessState> = of(UNREAD)
) {
  await TestBed.configureTestingModule({
    imports: [AdminImportsComponent],
    providers: [
      provideRouter([]),
      { provide: ImportService, useValue: importService },
      { provide: ToastService, useValue: { success: jest.fn(), error: jest.fn(), warning: jest.fn(), info: jest.fn(), ...toast } },
      { provide: SupportSessionService, useValue: { active$: sessionActive } },
      { provide: AccessService, useValue: { access$: access } }
    ]
  }).compileComponents();

  const fixture = TestBed.createComponent(AdminImportsComponent);
  fixture.detectChanges();
  return { fixture, host: fixture.nativeElement as HTMLElement };
}

function selectionEvent(fileName: string): Event {
  const input = document.createElement('input');
  const file = new File(['content'], fileName, { type: 'text/csv' });
  Object.defineProperty(input, 'files', { value: [file], writable: true });
  return { target: input } as unknown as Event;
}

// jsdom's Blob has no text() helper, so the generated template is read through FileReader.
function readBlob(blob: Blob): Promise<string> {
  return new Promise<string>((resolve, reject) => {
    const reader = new FileReader();
    reader.onload = () => resolve(reader.result as string);
    reader.onerror = () => reject(reader.error);
    reader.readAsText(blob);
  });
}

function emptySelectionEvent(): Event {
  const input = document.createElement('input');
  Object.defineProperty(input, 'files', { value: [], writable: true });
  return { target: input } as unknown as Event;
}

describe('AdminImportsComponent Nayax sales upload (issue #389)', () => {
  it('rejects a file whose extension is not .xlsx, .xls or .csv without calling the import API', async () => {
    const importNayaxSales = jest.fn();
    const error = jest.fn();
    const { fixture } = await render({ importNayaxSales }, { error });

    fixture.componentInstance.onSalesSelected(selectionEvent('sales.txt'));

    expect(importNayaxSales).not.toHaveBeenCalled();
    expect(error).toHaveBeenCalledWith('Only .xlsx, .xls, or .csv files are supported.');
    expect(fixture.componentInstance.loading).toBe(false);
  });

  it('accepts the supported sales file types and reports the imported, updated and skipped counts', async () => {
    for (const name of ['sales.xlsx', 'sales.XLS', 'sales.csv']) {
      const importNayaxSales = jest.fn(() => of({ imported: 3, updated: 2, skipped: 1 }));
      const success = jest.fn();
      const { fixture } = await render({ importNayaxSales }, { success });

      fixture.componentInstance.onSalesSelected(selectionEvent(name));

      expect(importNayaxSales).toHaveBeenCalledTimes(1);
      expect((importNayaxSales.mock.calls[0] as unknown as File[])[0].name).toBe(name);
      expect(success).toHaveBeenCalledWith('Imported 3 sales, updated 2, skipped 1.');
      expect(fixture.componentInstance.loading).toBe(false);
      TestBed.resetTestingModule();
    }
  });

  it('does nothing when the file dialog is dismissed without a selection', async () => {
    const importNayaxSales = jest.fn();
    const error = jest.fn();
    const { fixture } = await render({ importNayaxSales }, { error });

    fixture.componentInstance.onSalesSelected(emptySelectionEvent());

    expect(importNayaxSales).not.toHaveBeenCalled();
    expect(error).not.toHaveBeenCalled();
  });

  it('reports the API error message and clears loading when the sales upload fails', async () => {
    const importNayaxSales = jest.fn(() => throwError(() => ({ error: { message: 'Duplicate transaction 1001.' } })));
    const error = jest.fn();
    const { fixture } = await render({ importNayaxSales }, { error });

    fixture.componentInstance.onSalesSelected(selectionEvent('sales.csv'));

    expect(error).toHaveBeenCalledWith('Duplicate transaction 1001.');
    expect(fixture.componentInstance.loading).toBe(false);
  });

  it('falls back to the generic sales failure message when the API returns no message', async () => {
    const importNayaxSales = jest.fn(() => throwError(() => ({})));
    const error = jest.fn();
    const { fixture } = await render({ importNayaxSales }, { error });

    fixture.componentInstance.onSalesSelected(selectionEvent('sales.csv'));

    expect(error).toHaveBeenCalledWith('Failed to import Nayax sales.');
  });

  it('keeps the sales file input restricted to the accepted extensions and disables the action while importing', async () => {
    const { fixture, host } = await render({ importNayaxSales: jest.fn(() => of({ imported: 0, updated: 0, skipped: 0 })) });

    expect(host.querySelector('input[type="file"]')?.getAttribute('accept')).toBe('.xlsx,.xls,.csv');

    fixture.componentInstance.loading = true;
    fixture.detectChanges();

    // The two imports this page runs, and not Download template, which calls no API. Import
    // products is a link to its own page since issue #888 and is not a control this flag reaches.
    const disabled = Array.from(host.querySelectorAll('button')).filter(button => button.disabled);
    expect(disabled).toHaveLength(2);
  });
});

describe('AdminImportsComponent Nayax sales template (issue #389)', () => {
  it('downloads a CSV template with the expected columns and sample row', async () => {
    const { fixture } = await render({});
    // jsdom implements neither object-URL function, so they are stubbed rather than spied on.
    const objectUrls = URL as unknown as { createObjectURL?: (blob: Blob) => string; revokeObjectURL?: (url: string) => void };
    const originals = { create: objectUrls.createObjectURL, revoke: objectUrls.revokeObjectURL };
    const blobs: Blob[] = [];
    const revoked: string[] = [];
    objectUrls.createObjectURL = blob => { blobs.push(blob); return 'blob:template'; };
    objectUrls.revokeObjectURL = url => { revoked.push(url); };
    const anchor = document.createElement('a');
    const click = jest.spyOn(anchor, 'click').mockImplementation(() => undefined);
    const createdTags: string[] = [];
    const createElement = jest.spyOn(document, 'createElement').mockImplementation(tag => {
      createdTags.push(tag);
      return anchor;
    });

    try {
      fixture.componentInstance.downloadTemplate();
    } finally {
      createElement.mockRestore();
      objectUrls.createObjectURL = originals.create;
      objectUrls.revokeObjectURL = originals.revoke;
    }

    expect(createdTags).toEqual(['a']);
    expect(anchor.download).toBe('nayax-sales-import-template.csv');
    expect(anchor.getAttribute('href')).toBe('blob:template');
    expect(click).toHaveBeenCalledTimes(1);
    expect(revoked).toEqual(['blob:template']);

    expect(blobs).toHaveLength(1);
    expect(blobs[0].type).toBe('text/csv;charset=utf-8;');
    expect(await readBlob(blobs[0])).toBe(
      'TransactionID,TransactionStatusId,MachineID,NayaxProductId,MachineName,SettlementValue,PaymentMethod,ProductName,Product Cost Price,MachineAuthorizationTime\n' +
      '1001,12,42,987654,Machine A,12.50,Card,Coke Zero,1.100000,2026-09-02 14:30:00'
    );
  });

  it('wires the template button to the download action', async () => {
    const { fixture, host } = await render({});
    const downloadTemplate = jest.spyOn(fixture.componentInstance, 'downloadTemplate').mockImplementation(() => undefined);

    const button = Array.from(host.querySelectorAll('button')).find(x => x.textContent?.includes('Download template'));
    button?.click();

    expect(downloadTemplate).toHaveBeenCalledTimes(1);
  });
});

describe('AdminImportsComponent product catalogue import (issues #739 and #888)', () => {
  it('links its Import products card to the dedicated page and renders no review of its own', async () => {
    const { host } = await render({});

    const card = Array.from(host.querySelectorAll('section.card')).find((section) =>
      section.querySelector('.card-title')?.textContent?.includes('Import products')
    );
    expect(card).toBeTruthy();

    // The card keeps its title and its description, so the page still says what the import is.
    expect(card!.querySelector('.card-title')?.textContent?.trim()).toBe('Import products');
    expect(card!.textContent).toContain('Review the Nayax catalogue against your own products');

    const link = card!.querySelector('a');
    expect(link?.textContent?.trim()).toBe('Import products');
    expect(link?.getAttribute('href')).toBe('/products/import');

    // Nothing opens here any more, and the page starts no import of its own.
    expect(card!.querySelector('button')).toBeNull();
    expect(host.querySelector('[data-testid="catalogue-review"]')).toBeNull();
  });
});

describe('AdminImportsComponent pending reimbursement XML import (issue #389)', () => {
  it('imports the pending XML files and reports the imported file and reimbursement counts', async () => {
    const importPendingXmlFiles = jest.fn(() =>
      of({ importedFiles: 2, importedReimbursements: 4, skippedFiles: 1, failedFiles: 0 }));
    const success = jest.fn();
    const { fixture, host } = await render({ importPendingXmlFiles }, { success });

    const button = Array.from(host.querySelectorAll('button')).find(x => x.textContent?.includes('Import XML files'));
    button?.click();

    expect(importPendingXmlFiles).toHaveBeenCalledTimes(1);
    expect(success).toHaveBeenCalledWith('Imported 2 file(s) and 4 reimbursement(s).');
    expect(fixture.componentInstance.loading).toBe(false);
  });

  it('reports the API error and clears loading when the XML import fails', async () => {
    const importPendingXmlFiles = jest.fn(() => throwError(() => ({ error: 'No pending XML files found.' })));
    const error = jest.fn();
    const { fixture } = await render({ importPendingXmlFiles }, { error });

    fixture.componentInstance.importXml();

    expect(error).toHaveBeenCalledWith('No pending XML files found.');
    expect(fixture.componentInstance.loading).toBe(false);
  });

  it('falls back to the generic XML failure message when the API returns no body', async () => {
    const importPendingXmlFiles = jest.fn(() => throwError(() => ({})));
    const error = jest.fn();
    const { fixture } = await render({ importPendingXmlFiles }, { error });

    fixture.componentInstance.importXml();

    expect(error).toHaveBeenCalledWith('The XML import could not be started.');
  });
});

describe('AdminImportsComponent in a support session (issue #661)', () => {
  function importButton(host: HTMLElement, label: string): HTMLButtonElement {
    const button = Array.from(host.querySelectorAll('button')).find((x) => x.textContent?.includes(label));
    if (!button) throw new Error(`No "${label}" button rendered.`);
    return button;
  }

  it('disables every import control while a session is active', async () => {
    const { host } = await render({}, undefined, of(true));

    expect(importButton(host, 'Choose sales file').disabled).toBe(true);
    expect(importButton(host, 'Import XML files').disabled).toBe(true);
  });

  it('leaves every import control enabled while no session is active', async () => {
    const { host } = await render({}, undefined, of(false));

    expect(importButton(host, 'Choose sales file').disabled).toBe(false);
    expect(importButton(host, 'Import XML files').disabled).toBe(false);
  });

  /**
   * The Import products card is a link, not a control this page disables (issue #888): it starts
   * nothing, and `/products/import` withholds the review and shows its own notice during a session,
   * so the import is unavailable from here either way. Hiding the link would hide where the import
   * lives from the one page that is meant to say so.
   */
  it('leaves the Import products link in place, because the destination refuses the import itself', async () => {
    const { host } = await render({}, undefined, of(true));

    const link = Array.from(host.querySelectorAll('a')).find((x) => x.textContent?.trim() === 'Import products');
    expect(link?.getAttribute('href')).toBe('/products/import');
  });

  it('leaves the client-side template download available, because it calls no API', async () => {
    const { host } = await render({}, undefined, of(true));

    expect(importButton(host, 'Download template').disabled).toBe(false);
  });

  it('calls no import API from a guarded handler while a session is active', async () => {
    const importPendingXmlFiles = jest.fn();
    const importNayaxSales = jest.fn();
    const { fixture } = await render({ importPendingXmlFiles, importNayaxSales }, undefined, of(true));

    fixture.componentInstance.importXml();
    fixture.componentInstance.onSalesSelected(selectionEvent('sales.csv'));

    expect(importPendingXmlFiles).not.toHaveBeenCalled();
    expect(importNayaxSales).not.toHaveBeenCalled();
    expect(fixture.componentInstance.loading).toBe(false);
  });
});

/**
 * The inline plan lock on the Nayax sales card (issue #979). `POST /api/imports/nayax-sales` carries
 * `[RequirePlan(PlanFeature.SalesFileImport)]` (issue #967), which is the `Pro` plan, and this is a
 * mixed page: only that one card is locked, the page itself is never route-locked, and the other
 * import workflows on it are untouched.
 */
describe('AdminImportsComponent Nayax sales plan lock (issue #979)', () => {
  function salesCard(host: HTMLElement): HTMLElement {
    const card = Array.from(host.querySelectorAll('section.card')).find((section) =>
      section.querySelector('.card-title')?.textContent?.includes('Import Nayax sales')
    );
    if (card === undefined) throw new Error('No Nayax sales card rendered.');
    return card as HTMLElement;
  }

  function planLock(host: HTMLElement): HTMLElement | null {
    return salesCard(host).querySelector('[data-testid="sales-import-plan-lock"]');
  }

  function button(host: HTMLElement, label: string): HTMLButtonElement | undefined {
    return Array.from(host.querySelectorAll('button')).find((x) => x.textContent?.includes(label));
  }

  it('says the same sentence a locked menu entry or page would, for the plan the feature needs', () => {
    // One sentence shape for one feature, rather than a second wording invented in this card.
    expect(salesFileImportPlanLockedMessage('Pro')).toBe(
      planLockSummary('SalesFileImport', 'Importing a Nayax sales file')
    );
  });

  it.each(['Free', 'Starter'])(
    'withholds the submission controls on %s and sends no import at all',
    async (plan: string) => {
      const importNayaxSales = jest.fn();
      const error = jest.fn();
      const { fixture, host } = await render(
        { importNayaxSales },
        { error },
        of(false),
        of(granted(enforced(plan)))
      );

      // Nothing to submit with: neither the file input nor the button that opens it is rendered,
      // so there is no disabled control to tab to and nothing to click programmatically either.
      expect(salesCard(host).querySelector('input[type="file"]')).toBeNull();
      expect(button(host, 'Choose sales file')).toBeUndefined();

      // And the handler refuses as well, so the import is unavailable rather than merely inert.
      fixture.componentInstance.onSalesSelected(selectionEvent('sales.csv'));

      expect(importNayaxSales).not.toHaveBeenCalled();
      expect(error).not.toHaveBeenCalled();
      expect(fixture.componentInstance.loading).toBe(false);
    }
  );

  it('names the plan the import needs, links to the Plan page and offers no retry', async () => {
    const { host } = await render({}, undefined, of(false), of(granted(enforced('Free'))));

    const lock = planLock(host);
    expect(lock).not.toBeNull();
    expect(lock!.textContent).toContain('Importing a Nayax sales file needs the Pro plan.');
    // Announced, and as a state rather than an error: a plan refusal is this business's own answer.
    expect(lock!.getAttribute('role')).toBe('status');
    // Keyboard-reachable by being a real link, not a disabled control or a click handler on text.
    const link = lock!.querySelector('a');
    expect(link?.getAttribute('href')).toBe('/plan');
    expect(link?.textContent?.trim()).toBe('View the Plan page');
    // No Retry anywhere in the card: retrying asks the same question the business was answered.
    expect(button(host, 'Retry')).toBeUndefined();
    // The sentence and the link wrap instead of overflowing a narrow card, which is the only
    // narrow-width behaviour jsdom can be asked about - it performs no layout.
    expect(lock!.className).toContain('flex-wrap');
  });

  it('leaves the other import workflows on this mixed page exactly as they were', async () => {
    const importPendingXmlFiles = jest.fn(() =>
      of({ importedFiles: 1, importedReimbursements: 2, skippedFiles: 0, failedFiles: 0 })
    );
    const success = jest.fn();
    const { host } = await render(
      { importPendingXmlFiles },
      { success },
      of(false),
      of(granted(enforced('Free')))
    );

    // The pending reimbursement XML import is not a plan-locked feature and still runs.
    const xml = button(host, 'Import XML files');
    expect(xml?.disabled).toBe(false);
    xml?.click();
    expect(importPendingXmlFiles).toHaveBeenCalledTimes(1);
    expect(success).toHaveBeenCalledWith('Imported 1 file(s) and 2 reimbursement(s).');

    // The product catalogue import keeps its link, and the template download calls no API at all.
    expect(
      Array.from(host.querySelectorAll('a')).find((x) => x.textContent?.trim() === 'Import products')?.getAttribute('href')
    ).toBe('/products/import');
    expect(button(host, 'Download template')?.disabled).toBe(false);

    // And the page is not locked as a whole: no other card carries the lock.
    expect(host.querySelectorAll('[data-testid="sales-import-plan-lock"]')).toHaveLength(1);
  });

  it('imports normally on Pro', async () => {
    const importNayaxSales = jest.fn(() => of({ imported: 2, updated: 0, skipped: 0 }));
    const success = jest.fn();
    const { fixture, host } = await render(
      { importNayaxSales },
      { success },
      of(false),
      of(granted(enforced('Pro')))
    );

    expect(planLock(host)).toBeNull();
    expect(button(host, 'Choose sales file')?.disabled).toBe(false);

    fixture.componentInstance.onSalesSelected(selectionEvent('sales.csv'));

    expect(importNayaxSales).toHaveBeenCalledTimes(1);
    expect(success).toHaveBeenCalledWith('Imported 2 sales, updated 0, skipped 0.');
  });

  it('keeps the support-session restriction on Pro, where the plan allows the import', async () => {
    const importNayaxSales = jest.fn();
    const { fixture, host } = await render(
      { importNayaxSales },
      undefined,
      of(true),
      of(granted(enforced('Pro')))
    );

    // The session rule is unchanged and is not replaced by a plan lock: the control is rendered and
    // disabled, because what withholds it there is the read-only session, not the plan.
    expect(planLock(host)).toBeNull();
    expect(button(host, 'Choose sales file')?.disabled).toBe(true);

    fixture.componentInstance.onSalesSelected(selectionEvent('sales.csv'));

    expect(importNayaxSales).not.toHaveBeenCalled();
  });

  it('locks nothing while enforcement is switched off, whatever plan is reported', async () => {
    const importNayaxSales = jest.fn(() => of({ imported: 1, updated: 0, skipped: 0 }));
    const { fixture, host } = await render(
      { importNayaxSales },
      undefined,
      of(false),
      of(granted(ENFORCEMENT_OFF))
    );

    expect(planLock(host)).toBeNull();

    fixture.componentInstance.onSalesSelected(selectionEvent('sales.csv'));

    expect(importNayaxSales).toHaveBeenCalledTimes(1);
  });

  const notKnown: readonly [string, Observable<MemberAccessState>][] = [
    ['nothing has been read yet', NEVER],
    ['the read could not be answered', of<MemberAccessState>({ status: 'unavailable' })],
    ['the API reported no entitlement at all', of(granted(undefined))]
  ];

  it.each(notKnown)('locks nothing while %s, and lets the backend answer for the business', async (_case, access) => {
    const importNayaxSales = jest.fn(() => of({ imported: 1, updated: 0, skipped: 0 }));
    const { fixture, host } = await render({ importNayaxSales }, undefined, of(false), access);

    expect(planLock(host)).toBeNull();
    expect(button(host, 'Choose sales file')).not.toBeUndefined();

    fixture.componentInstance.onSalesSelected(selectionEvent('sales.csv'));

    expect(importNayaxSales).toHaveBeenCalledTimes(1);
  });

  /**
   * The downgrade and unknown-entitlement case: the entitlement said the import was included - or
   * nothing could be read about it - the request was made, and `[RequirePlan]` refused it. The card
   * shows the plan the refusal itself named, not a generic toast and not a retry.
   */
  it('renders a plan_required refusal in the card instead of a generic error', async () => {
    const importNayaxSales = jest.fn(() =>
      throwError(
        () =>
          new HttpErrorResponse({
            status: 403,
            error: { code: 'plan_required', requiredPlan: 'Pro', planFeature: 'SalesFileImport' }
          })
      )
    );
    const error = jest.fn();
    const { fixture, host } = await render({ importNayaxSales }, { error }, of(false), of(granted(undefined)));

    fixture.componentInstance.onSalesSelected(selectionEvent('sales.csv'));
    fixture.detectChanges();

    expect(error).not.toHaveBeenCalled();
    expect(fixture.componentInstance.loading).toBe(false);
    expect(planLock(host)?.textContent).toContain('Importing a Nayax sales file needs the Pro plan.');
    // The controls are withheld from here on, so the refused request cannot be repeated.
    expect(button(host, 'Choose sales file')).toBeUndefined();
    fixture.componentInstance.onSalesSelected(selectionEvent('sales.csv'));
    expect(importNayaxSales).toHaveBeenCalledTimes(1);
  });

  it('still reports an ordinary import failure as the error it is', async () => {
    const importNayaxSales = jest.fn(() =>
      throwError(() => new HttpErrorResponse({ status: 400, error: { message: 'Duplicate transaction 1001.' } }))
    );
    const error = jest.fn();
    const { fixture, host } = await render({ importNayaxSales }, { error }, of(false), of(granted(enforced('Pro'))));

    fixture.componentInstance.onSalesSelected(selectionEvent('sales.csv'));
    fixture.detectChanges();

    expect(error).toHaveBeenCalledWith('Duplicate transaction 1001.');
    // A failure that is not about the plan leaves the card usable: this one is worth retrying.
    expect(planLock(host)).toBeNull();
    expect(button(host, 'Choose sales file')).not.toBeUndefined();
  });

  it('opens the card again once a re-read says the plan includes the import', async () => {
    const importNayaxSales = jest.fn(() =>
      throwError(
        () => new HttpErrorResponse({ status: 403, error: { code: 'plan_required', requiredPlan: 'Pro' } })
      )
    );
    const access = new BehaviorSubject<MemberAccessState>(granted(undefined));
    const { fixture, host } = await render({ importNayaxSales }, undefined, of(false), access);

    fixture.componentInstance.onSalesSelected(selectionEvent('sales.csv'));
    fixture.detectChanges();
    expect(planLock(host)).not.toBeNull();

    // An upgrade read on the next navigation or refresh: the refusal belonged to the plan the
    // business was on, so it is spent, and the card stops being locked by it.
    access.next(granted(enforced('Pro')));
    fixture.detectChanges();

    expect(planLock(host)).toBeNull();
    expect(button(host, 'Choose sales file')).not.toBeUndefined();
  });

  it('keeps the lock when a re-read confirms the plan does not include the import', async () => {
    const importNayaxSales = jest.fn(() =>
      throwError(
        () => new HttpErrorResponse({ status: 403, error: { code: 'plan_required', requiredPlan: 'Pro' } })
      )
    );
    const access = new BehaviorSubject<MemberAccessState>(granted(undefined));
    const { fixture, host } = await render({ importNayaxSales }, undefined, of(false), access);

    fixture.componentInstance.onSalesSelected(selectionEvent('sales.csv'));
    access.next(granted(enforced('Starter')));
    fixture.detectChanges();

    expect(planLock(host)?.textContent).toContain('needs the Pro plan.');
  });
});
