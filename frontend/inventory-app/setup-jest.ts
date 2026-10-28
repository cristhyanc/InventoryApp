import { setupZoneTestEnv } from 'jest-preset-angular/setup-env/zone';

/**
 * jsdom 20 - the version jest-environment-jsdom 29 pins - implements `<dialog>`'s attributes but
 * none of its methods, so `showModal()` is `undefined` and a component that opens a native modal
 * dialog cannot be rendered under Jest at all. This supplies the element's open/close state
 * machine and its `close` event, and deliberately nothing else: the top layer, the inert page
 * behind the dialog, the `::backdrop` and the browser's own focus trap are the platform's and
 * cannot be emulated here. A spec therefore proves the behaviour a component implements itself -
 * its Tab cycle, its dismissal paths, where focus goes - and that it opens the dialog *modally*;
 * the platform's half is the browser's, and asserting it here would only be asserting this stub.
 */
function installJsdomDialogSupport(): void {
  const dialog = document.createElement('dialog') as HTMLDialogElement;
  if (typeof dialog.showModal === 'function') {
    return;
  }

  const prototype = Object.getPrototypeOf(dialog) as HTMLDialogElement;

  const open = function (this: HTMLDialogElement): void {
    this.setAttribute('open', '');
  };

  prototype.show = open;
  prototype.showModal = open;
  prototype.close = function (this: HTMLDialogElement, returnValue?: string): void {
    if (!this.hasAttribute('open')) {
      return;
    }

    if (returnValue !== undefined) {
      this.returnValue = returnValue;
    }

    this.removeAttribute('open');
    this.dispatchEvent(new Event('close'));
  };
}

setupZoneTestEnv();
installJsdomDialogSupport();
