import { focusPageHeading, handOffFocusFromRemovedOpener } from './confirmation-focus';

/**
 * The handoff a caller finishes after a confirmed, asynchronous delete (issue #1277): the dialog
 * returns focus to the row's own control as it closes, and the refreshed list then removes that
 * control, so the caller says where focus goes next.
 */
describe('handOffFocusFromRemovedOpener (issue #1277)', () => {
  let heading: HTMLHeadingElement;
  let opener: HTMLButtonElement;
  let fallback: HTMLButtonElement;
  let elsewhere: HTMLButtonElement;

  beforeEach(() => {
    heading = document.createElement('h1');
    heading.textContent = 'Categories';
    opener = document.createElement('button');
    fallback = document.createElement('button');
    elsewhere = document.createElement('button');
    document.body.append(heading, opener, fallback, elsewhere);
  });

  afterEach(() => {
    for (const element of [heading, opener, fallback, elsewhere]) {
      element.remove();
    }
  });

  it('moves focus to the page heading, made focusable, when the opener still has it', () => {
    opener.focus();

    handOffFocusFromRemovedOpener(opener);

    expect(document.activeElement).toBe(heading);
    expect(heading.getAttribute('tabindex')).toBe('-1');
  });

  it('prefers the fallback the caller named over the page heading', () => {
    opener.focus();

    handOffFocusFromRemovedOpener(opener, fallback);

    expect(document.activeElement).toBe(fallback);
  });

  it('falls back to the page heading when the element the caller named is gone too', () => {
    opener.focus();
    fallback.remove();

    handOffFocusFromRemovedOpener(opener, fallback);

    expect(document.activeElement).toBe(heading);
  });

  it('recovers focus when the opener was removed before the handoff ran', () => {
    opener.focus();
    opener.remove();

    handOffFocusFromRemovedOpener(opener);

    expect(document.activeElement).toBe(heading);
  });

  it('leaves focus the user moved elsewhere while the request was in flight alone', () => {
    elsewhere.focus();

    handOffFocusFromRemovedOpener(opener);

    expect(document.activeElement).toBe(elsewhere);
  });

  it('keeps a tabindex the heading already has', () => {
    heading.setAttribute('tabindex', '0');
    opener.focus();

    focusPageHeading();

    expect(heading.getAttribute('tabindex')).toBe('0');
    expect(document.activeElement).toBe(heading);
  });
});
