import { TestBed } from '@angular/core/testing';
import { Subject, of, throwError } from 'rxjs';
import { ImportService, ReimbursementReportUploadAccepted } from '../../../../services/import.service';
import { ReimbursementReportUploadComponent } from './reimbursement-report-upload.component';

async function render(imports: Partial<ImportService> = {}, disabled = false) {
  await TestBed.configureTestingModule({
    imports: [ReimbursementReportUploadComponent],
    providers: [{ provide: ImportService, useValue: imports }]
  }).compileComponents();

  const fixture = TestBed.createComponent(ReimbursementReportUploadComponent);
  fixture.componentRef.setInput('disabled', disabled);
  fixture.detectChanges();
  return { fixture, host: fixture.nativeElement as HTMLElement, component: fixture.componentInstance };
}

function testId(host: HTMLElement, id: string): HTMLElement | null {
  return host.querySelector<HTMLElement>(`[data-testid="${id}"]`);
}

function uploadButton(host: HTMLElement): HTMLButtonElement {
  const button = testId(host, 'reimbursement-upload-button');
  if (button === null) throw new Error('No upload button rendered.');
  return button as HTMLButtonElement;
}

/** A `change` event carrying one chosen file, the shape the real file input dispatches. */
function selectionEvent(fileName: string): Event {
  const input = document.createElement('input');
  const file = new File(['zip bytes'], fileName, { type: 'application/zip' });
  Object.defineProperty(input, 'files', { value: [file], writable: true });
  return { target: input } as unknown as Event;
}

function emptySelectionEvent(): Event {
  const input = document.createElement('input');
  Object.defineProperty(input, 'files', { value: [], writable: true });
  return { target: input } as unknown as Event;
}

/**
 * The upload half of the Reimbursement Report page (issue #1152): the ZIP picker, the Upload button,
 * the busy state, the note and the `400` the server answers a bad archive with.
 */
describe('ReimbursementReportUploadComponent (issue #1152)', () => {
  it('offers a .zip picker and says to upload the attachment exactly as Nayax emailed it', async () => {
    const { host } = await render();

    const input = host.querySelector<HTMLInputElement>('input[type="file"]');
    expect(input?.getAttribute('accept')).toContain('.zip');
    expect(testId(host, 'reimbursement-upload-note')?.textContent).toContain('exactly as Nayax');
  });

  it('shows the chosen file name and only then offers to upload it', async () => {
    const uploadReimbursementReport = jest.fn();
    const { fixture, host, component } = await render({ uploadReimbursementReport });

    expect(uploadButton(host).disabled).toBe(true);

    component.onSelected(selectionEvent('Reports_09-11-26.zip'));
    fixture.detectChanges();

    expect(testId(host, 'reimbursement-selected-file')?.textContent).toContain('Reports_09-11-26.zip');
    expect(uploadButton(host).disabled).toBe(false);
    expect(uploadReimbursementReport).not.toHaveBeenCalled();
  });

  it('keeps nothing selected when the picker was dismissed without a file', async () => {
    const { fixture, host, component } = await render({ uploadReimbursementReport: jest.fn() });

    component.onSelected(emptySelectionEvent());
    fixture.detectChanges();

    expect(testId(host, 'reimbursement-selected-file')).toBeNull();
    expect(uploadButton(host).disabled).toBe(true);
  });

  it('uploads the chosen file, reports that it was accepted and tells the page the job id', async () => {
    const accepted: ReimbursementReportUploadAccepted = { jobId: 17 };
    const uploadReimbursementReport = jest.fn(() => of(accepted));
    const { fixture, host, component } = await render({ uploadReimbursementReport });
    const emitted: number[] = [];
    component.uploaded.subscribe((jobId) => emitted.push(jobId));

    component.onSelected(selectionEvent('Reports_09-11-26.zip'));
    fixture.detectChanges();
    uploadButton(host).click();
    fixture.detectChanges();

    expect(uploadReimbursementReport).toHaveBeenCalledTimes(1);
    expect((uploadReimbursementReport.mock.calls[0] as unknown as File[])[0].name).toBe('Reports_09-11-26.zip');
    expect(emitted).toEqual([17]);
    expect(testId(host, 'reimbursement-upload-accepted')?.textContent).toContain('queued');
    // The accepted file is cleared, so the same archive is not uploaded twice by a second click.
    expect(testId(host, 'reimbursement-selected-file')).toBeNull();
    expect(uploadButton(host).disabled).toBe(true);
  });

  /**
   * The refusal is the server's own sentence, shown where the upload was attempted rather than as a
   * toast: a rejected archive stores nothing at all, so what the operator needs is the reason beside
   * the file they chose.
   */
  it("shows the server's own 400 sentence and emits nothing", async () => {
    const uploadReimbursementReport = jest.fn(() =>
      throwError(() => ({ status: 400, error: 'This file could not be read as a ZIP archive.' }))
    );
    const { fixture, host, component } = await render({ uploadReimbursementReport });
    const emitted: number[] = [];
    component.uploaded.subscribe((jobId) => emitted.push(jobId));

    component.onSelected(selectionEvent('Reports.zip'));
    fixture.detectChanges();
    uploadButton(host).click();
    fixture.detectChanges();

    const error = testId(host, 'reimbursement-upload-error');
    expect(error?.textContent).toContain('This file could not be read as a ZIP archive.');
    expect(error?.getAttribute('role')).toBe('alert');
    expect(emitted).toEqual([]);
    expect(testId(host, 'reimbursement-upload-accepted')).toBeNull();
  });

  it('falls back to a generic sentence when a failure carries no message at all', async () => {
    const uploadReimbursementReport = jest.fn(() => throwError(() => ({})));
    const { fixture, host, component } = await render({ uploadReimbursementReport });

    component.onSelected(selectionEvent('Reports.zip'));
    fixture.detectChanges();
    uploadButton(host).click();
    fixture.detectChanges();

    expect(testId(host, 'reimbursement-upload-error')?.textContent).toContain('could not be uploaded');
  });

  it('reads a ProblemDetails body when one is returned instead of a bare sentence', async () => {
    const uploadReimbursementReport = jest.fn(() =>
      throwError(() => ({ status: 400, error: { message: 'The archive holds no reimbursement XML.' } }))
    );
    const { fixture, host, component } = await render({ uploadReimbursementReport });

    component.onSelected(selectionEvent('Reports.zip'));
    fixture.detectChanges();
    uploadButton(host).click();
    fixture.detectChanges();

    expect(testId(host, 'reimbursement-upload-error')?.textContent).toContain('The archive holds no reimbursement XML.');
  });

  it('shows a busy state while the upload is in flight and sends nothing on a second click', async () => {
    const answer = new Subject<ReimbursementReportUploadAccepted>();
    const uploadReimbursementReport = jest.fn(() => answer.asObservable());
    const { fixture, host, component } = await render({ uploadReimbursementReport });

    component.onSelected(selectionEvent('Reports.zip'));
    fixture.detectChanges();
    uploadButton(host).click();
    fixture.detectChanges();

    expect(uploadButton(host).disabled).toBe(true);
    expect(uploadButton(host).textContent).toContain('Uploading');

    component.upload();
    expect(uploadReimbursementReport).toHaveBeenCalledTimes(1);

    answer.next({ jobId: 3 });
    answer.complete();
    fixture.detectChanges();
    expect(uploadButton(host).textContent).toContain('Upload');
  });

  /**
   * A read-only support session (issue #661): the endpoint refuses this `POST` centrally, so the
   * control is genuinely unavailable rather than merely inert - the handler checks it too.
   */
  it('withholds the upload while a support session is active', async () => {
    const uploadReimbursementReport = jest.fn();
    const { fixture, host, component } = await render({ uploadReimbursementReport }, true);

    component.onSelected(selectionEvent('Reports.zip'));
    fixture.detectChanges();

    expect(uploadButton(host).disabled).toBe(true);
    component.upload();
    expect(uploadReimbursementReport).not.toHaveBeenCalled();
  });

  /** A new attempt starts clean: last time's refusal is not left standing over this upload. */
  it('clears the previous refusal when the next upload starts', async () => {
    const answers = [
      throwError(() => ({ status: 400, error: 'Refused.' })),
      of<ReimbursementReportUploadAccepted>({ jobId: 9 })
    ];
    const uploadReimbursementReport = jest.fn(() => answers.shift()!);
    const { fixture, host, component } = await render({ uploadReimbursementReport });

    component.onSelected(selectionEvent('Reports.zip'));
    fixture.detectChanges();
    uploadButton(host).click();
    fixture.detectChanges();
    expect(testId(host, 'reimbursement-upload-error')).not.toBeNull();

    component.onSelected(selectionEvent('Reports-fixed.zip'));
    fixture.detectChanges();
    uploadButton(host).click();
    fixture.detectChanges();

    expect(testId(host, 'reimbursement-upload-error')).toBeNull();
    expect(testId(host, 'reimbursement-upload-accepted')).not.toBeNull();
  });

  /** No figure, no payment detail and no XML: the card says what it did, never what it imported. */
  it('reports no figure of any kind, because the upload has imported nothing yet', async () => {
    const { fixture, host, component } = await render({
      uploadReimbursementReport: jest.fn(() => of<ReimbursementReportUploadAccepted>({ jobId: 4 }))
    });

    component.onSelected(selectionEvent('Reports.zip'));
    fixture.detectChanges();
    uploadButton(host).click();
    fixture.detectChanges();

    expect(testId(host, 'reimbursement-upload-accepted')?.textContent).not.toContain('4');
  });
});
