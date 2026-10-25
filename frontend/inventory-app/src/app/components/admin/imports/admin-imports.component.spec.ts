import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { Observable, of, throwError } from 'rxjs';
import { AdminImportsComponent } from './admin-imports.component';
import { ImportService } from '../../../services/import.service';
import { ToastService } from '../../../services/toast.service';
import { SupportSessionService } from '../../../support-session/support-session.service';

/**
 * Since issue #888 this page runs two imports - the Nayax sales upload and the pending
 * reimbursement XML - and hosts no catalogue review: the product catalogue import has its own page
 * at `/products/import` (`ProductImportPageComponent`, with its own spec) and this page's Import
 * products card is a link to it.
 */
async function render(
  importService: Partial<ImportService>,
  toast?: Partial<ToastService>,
  sessionActive: Observable<boolean> = of(false)
) {
  await TestBed.configureTestingModule({
    imports: [AdminImportsComponent],
    providers: [
      provideRouter([]),
      { provide: ImportService, useValue: importService },
      { provide: ToastService, useValue: { success: jest.fn(), error: jest.fn(), warning: jest.fn(), info: jest.fn(), ...toast } },
      { provide: SupportSessionService, useValue: { active$: sessionActive } }
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
