import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ConfirmationDialogComponent } from './confirmation-dialog.component';

/** A host that opens and closes the dialog through `@if`, with a button outside it as the opener. */
@Component({
  standalone: true,
  imports: [ConfirmationDialogComponent],
  template: `
    <button type="button" id="opener" (click)="open = true">Delete</button>

    @if (open) {
      <app-confirmation-dialog
        title="Delete widget?"
        message="This cannot be undone."
        confirmLabel="Delete"
        cancelLabel="Cancel"
        (confirmed)="confirmed = confirmed + 1"
        (canceled)="open = false"
      ></app-confirmation-dialog>
    }
  `
})
class DialogHostComponent {
  open = false;
  confirmed = 0;
}

/** A host whose opener is removed before the dialog closes, to exercise the fallback target. */
@Component({
  standalone: true,
  imports: [ConfirmationDialogComponent],
  template: `
    <button type="button" id="opener" (click)="openDialog()">Delete</button>
    <button type="button" id="fallback">Fallback target</button>

    @if (open) {
      <app-confirmation-dialog
        title="Delete widget?"
        message="This cannot be undone."
        [returnFocusTo]="useFallback ? fallbackElement() : null"
        (canceled)="close()"
      ></app-confirmation-dialog>
    }
  `
})
class RemovedOpenerHostComponent {
  open = false;
  useFallback = false;

  openDialog(): void {
    this.open = true;
  }

  close(): void {
    document.getElementById('opener')?.remove();
    this.open = false;
  }

  fallbackElement(): HTMLElement {
    return document.getElementById('fallback') as HTMLElement;
  }
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
    panel: host.querySelector('.confirm-modal') as HTMLElement,
    cancelButton: () => host.querySelector('.confirm-actions button') as HTMLButtonElement
  };
}

/**
 * The shared confirmation (audit #1225, finding FE-06): a title, a message and a Confirm/Cancel
 * pair, now a self-contained `alertdialog` that traps Tab, cancels on Escape and returns focus on
 * close, the way `components/shared/modal-dialog` already does for its own shell.
 */
describe('ConfirmationDialogComponent (issue #1277)', () => {
  it('is an alertdialog labelled by its title and described by its message', async () => {
    const { panel } = await render();

    expect(panel.getAttribute('role')).toBe('alertdialog');
    expect(panel.getAttribute('aria-modal')).toBe('true');

    const labelledBy = panel.getAttribute('aria-labelledby');
    const describedBy = panel.getAttribute('aria-describedby');
    expect(panel.querySelector(`#${labelledBy}`)?.textContent).toBe('Delete widget?');
    expect(panel.querySelector(`#${describedBy}`)?.textContent).toBe('This cannot be undone.');
  });

  it('moves focus to Cancel on open, not to the destructive action', async () => {
    const { cancelButton } = await render();

    expect(document.activeElement).toBe(cancelButton());
  });

  it('cycles Tab and Shift+Tab between Cancel and the confirm action, never leaving the dialog', async () => {
    const { panel, cancelButton } = await render();
    const confirmButton = panel.querySelectorAll('.confirm-actions button')[1] as HTMLButtonElement;

    panel.dispatchEvent(new KeyboardEvent('keydown', { key: 'Tab', bubbles: true }));
    expect(document.activeElement).toBe(confirmButton);

    panel.dispatchEvent(new KeyboardEvent('keydown', { key: 'Tab', bubbles: true }));
    expect(document.activeElement).toBe(cancelButton());

    panel.dispatchEvent(new KeyboardEvent('keydown', { key: 'Tab', shiftKey: true, bubbles: true }));
    expect(document.activeElement).toBe(confirmButton);
  });

  it('cancels on Escape without confirming', async () => {
    const { fixture, panel } = await render();

    panel.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
    fixture.detectChanges();

    expect(fixture.componentInstance.confirmed).toBe(0);
    expect(fixture.componentInstance.open).toBe(false);
  });

  it('cancels on a click outside the panel, and does nothing for a click inside it', async () => {
    const { fixture, host, panel } = await render();

    panel.click();
    fixture.detectChanges();
    expect(fixture.componentInstance.open).toBe(true);

    (host.querySelector('.modal-backdrop') as HTMLElement).click();
    fixture.detectChanges();
    expect(fixture.componentInstance.open).toBe(false);
  });

  it('returns focus to the control that opened it once it closes', async () => {
    const { fixture, opener } = await render();

    fixture.componentInstance.open = false;
    fixture.detectChanges();

    expect(document.activeElement).toBe(opener);
  });

  it('confirms and emits exactly once', async () => {
    const { fixture, panel } = await render();
    const confirmButton = panel.querySelectorAll('.confirm-actions button')[1] as HTMLButtonElement;

    confirmButton.click();
    fixture.detectChanges();

    expect(fixture.componentInstance.confirmed).toBe(1);
  });

  it('falls back to the caller-supplied element when the opener is gone when it closes', async () => {
    await TestBed.configureTestingModule({ imports: [RemovedOpenerHostComponent] }).compileComponents();
    const fixture = TestBed.createComponent(RemovedOpenerHostComponent);
    fixture.detectChanges();
    const host = fixture.nativeElement as HTMLElement;
    document.body.appendChild(host);

    fixture.componentInstance.useFallback = true;
    (host.querySelector('#opener') as HTMLButtonElement).click();
    fixture.detectChanges();

    const panel = host.querySelector('.confirm-modal') as HTMLElement;
    panel.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
    fixture.detectChanges();

    expect(host.querySelector('#opener')).toBeNull();
    expect(document.activeElement).toBe(host.querySelector('#fallback'));
  });

  it('falls back to the page heading when the opener is gone and no fallback was supplied', async () => {
    await TestBed.configureTestingModule({ imports: [RemovedOpenerHostComponent] }).compileComponents();
    const fixture = TestBed.createComponent(RemovedOpenerHostComponent);
    fixture.detectChanges();
    const host = fixture.nativeElement as HTMLElement;
    document.body.appendChild(host);
    const heading = document.createElement('h1');
    heading.textContent = 'Widgets';
    document.body.appendChild(heading);

    (host.querySelector('#opener') as HTMLButtonElement).click();
    fixture.detectChanges();

    const panel = host.querySelector('.confirm-modal') as HTMLElement;
    panel.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
    fixture.detectChanges();

    expect(document.activeElement).toBe(heading);
    heading.remove();
  });

  it('does not manage focus at all when the caller opts out', async () => {
    @Component({
      standalone: true,
      imports: [ConfirmationDialogComponent],
      template: `
        <button type="button" id="opener" (click)="open = true">Delete</button>
        @if (open) {
          <app-confirmation-dialog title="Delete?" [restoreFocus]="false" (canceled)="open = false"></app-confirmation-dialog>
        }
      `
    })
    class OptOutHostComponent {
      open = false;
    }

    await TestBed.configureTestingModule({ imports: [OptOutHostComponent] }).compileComponents();
    const fixture = TestBed.createComponent(OptOutHostComponent);
    fixture.detectChanges();
    const host = fixture.nativeElement as HTMLElement;
    document.body.appendChild(host);

    const opener = host.querySelector('#opener') as HTMLButtonElement;
    opener.focus();
    opener.click();
    fixture.detectChanges();

    // The dialog still focuses Cancel on open - only the close-time restore is skipped.
    expect(document.activeElement).toBe(host.querySelector('.confirm-actions button'));

    document.body.focus();
    fixture.componentInstance.open = false;
    fixture.detectChanges();

    expect(document.activeElement).not.toBe(opener);
  });
});
