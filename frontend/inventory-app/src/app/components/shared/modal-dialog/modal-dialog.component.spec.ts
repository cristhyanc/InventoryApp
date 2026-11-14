import { ChangeDetectorRef, Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { ModalDialogComponent } from './modal-dialog.component';

/**
 * A host that opens and closes the shell exactly as a real owner does - through `@if` over its own
 * state - with a button outside the dialog as the control that opened it.
 */
@Component({
  standalone: true,
  imports: [ModalDialogComponent],
  template: `
    <button type="button" id="opener" (click)="open = true">Open</button>

    @if (open) {
      <app-modal-dialog
        dialogTitle="Edit supplier"
        closeLabel="Close this form"
        testId="test-dialog"
        [closeDisabled]="busy"
        (closeRequested)="requests = requests + 1"
      >
        <input id="first" />
        <input id="second" [disabled]="true" />

        <div dialog-actions>
          <button type="button" id="primary" data-dialog-autofocus>Save</button>
        </div>
      </app-modal-dialog>
    }
  `
})
class DialogHostComponent {
  open = false;
  busy = false;
  requests = 0;
}

afterEach(() => TestBed.resetTestingModule());

async function render() {
  await TestBed.configureTestingModule({ imports: [DialogHostComponent] }).compileComponents();
  const fixture = TestBed.createComponent(DialogHostComponent);
  fixture.detectChanges();

  const host = fixture.nativeElement as HTMLElement;
  // Attached to the document, so `document.activeElement` is a real answer rather than the body.
  document.body.appendChild(host);
  const opener = host.querySelector('#opener') as HTMLButtonElement;
  opener.focus();
  opener.click();
  fixture.detectChanges();

  return {
    fixture,
    host,
    opener,
    panel: host.querySelector('[data-testid="test-dialog"]') as HTMLDialogElement
  };
}

/**
 * The shared dialog shell (issue #1174). Only the half the component implements itself is asserted
 * here: the top layer, the inert page behind the panel, the `::backdrop` and the browser's own
 * focus trap are the platform's and cannot be emulated under jsdom (see `setup-jest.ts`).
 */
describe('ModalDialogComponent (issue #1174)', () => {
  it('opens as a modal labelled by its own heading, with no role of its own', async () => {
    const { panel } = await render();

    expect(panel.tagName).toBe('DIALOG');
    expect(panel.hasAttribute('open')).toBe(true);
    expect(panel.hasAttribute('role')).toBe(false);
    expect(panel.hasAttribute('aria-modal')).toBe(false);

    const titleId = panel.getAttribute('aria-labelledby');
    expect(panel.querySelector(`#${titleId}`)?.textContent?.trim()).toBe('Edit supplier');
  });

  it('keeps the reviewed 600px width by default and widens only on request (issue #1311)', async () => {
    const { fixture, panel } = await render();

    expect(panel.classList.contains('app-dialog-wide')).toBe(false);

    const shell = fixture.debugElement.query(By.directive(ModalDialogComponent));
    (shell.componentInstance as ModalDialogComponent).size = 'wide';
    shell.injector.get(ChangeDetectorRef).detectChanges();

    expect(panel.classList.contains('app-dialog-wide')).toBe(true);
  });

  it('projects content into the scrollable body and the action row into the footer', async () => {
    const { panel } = await render();

    expect(panel.querySelector('.app-dialog-body #first')).not.toBeNull();
    expect(panel.querySelector('.app-dialog-footer #primary')).not.toBeNull();
    expect(panel.querySelector('.app-dialog-body #primary')).toBeNull();
  });

  it('focuses the control the owner nominated', async () => {
    const { host } = await render();

    expect(document.activeElement).toBe(host.querySelector('#primary'));
  });

  it('asks to close from the Close control, Escape and the backdrop, and closes nothing itself', async () => {
    const { fixture, host, panel } = await render();

    (host.querySelector('.app-dialog-close') as HTMLButtonElement).click();
    panel.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
    // `::backdrop` is the panel's own pseudo-element, so a click beside the panel arrives on it.
    panel.dispatchEvent(new MouseEvent('click', { bubbles: true }));
    fixture.detectChanges();

    expect(fixture.componentInstance.requests).toBe(3);
    // The owner has not closed it, so it is still open: dismissal is never the shell's decision.
    expect(panel.hasAttribute('open')).toBe(true);
  });

  it('asks nothing when a click lands on its own content', async () => {
    const { fixture, host } = await render();

    (host.querySelector('#first') as HTMLInputElement).click();
    fixture.detectChanges();

    expect(fixture.componentInstance.requests).toBe(0);
  });

  it('names the Close control as the owner asked, and holds it still while the owner is busy', async () => {
    const { fixture, host } = await render();
    const close = host.querySelector('.app-dialog-close') as HTMLButtonElement;

    expect(close.getAttribute('aria-label')).toBe('Close this form');

    fixture.componentInstance.busy = true;
    fixture.detectChanges();

    expect(close.disabled).toBe(true);
  });

  /** The cycle is written down in the component, so a browser's own answer is never assumed. */
  it('cycles Tab and Shift+Tab inside the dialog, skipping a disabled control', async () => {
    const { host, panel } = await render();
    const stops = ['.app-dialog-close', '#first', '#primary'].map(
      (selector) => host.querySelector(selector) as HTMLElement
    );

    stops[0].focus();
    for (const expected of [stops[1], stops[2], stops[0]]) {
      panel.dispatchEvent(new KeyboardEvent('keydown', { key: 'Tab', bubbles: true }));
      expect(document.activeElement).toBe(expected);
    }

    panel.dispatchEvent(new KeyboardEvent('keydown', { key: 'Tab', shiftKey: true, bubbles: true }));
    expect(document.activeElement).toBe(stops[2]);
  });

  it('returns focus to the control that opened it once it closes', async () => {
    const { fixture, opener } = await render();

    fixture.componentInstance.open = false;
    fixture.detectChanges();

    expect(document.activeElement).toBe(opener);
  });
});
