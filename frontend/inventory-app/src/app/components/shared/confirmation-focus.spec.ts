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

/**
 * Focus the user moved somewhere else while the delete was still in flight (issue #1277). The row
 * being deleted takes every control in it with it, not only the one the dialog returned focus to,
 * so the handoff has to tell "moved to Edit on that same row" - which is about to be removed too -
 * from "moved to something that will still be there".
 */
describe('handOffFocusFromRemovedOpener when focus moved while the request was in flight', () => {
  let heading: HTMLHeadingElement;
  let table: HTMLTableElement;
  let rowDelete: HTMLButtonElement;
  let rowEdit: HTMLButtonElement;
  let otherRowDelete: HTMLButtonElement;
  let outsideTheRow: HTMLInputElement;

  /** One list row with its own Edit and Delete buttons, as every one of these lists renders. */
  function row(body: HTMLTableSectionElement): { edit: HTMLButtonElement; remove: HTMLButtonElement } {
    const tr = document.createElement('tr');
    const cell = document.createElement('td');
    const edit = document.createElement('button');
    const remove = document.createElement('button');
    cell.append(edit, remove);
    tr.append(cell);
    body.append(tr);
    return { edit, remove };
  }

  beforeEach(() => {
    heading = document.createElement('h1');
    heading.textContent = 'Categories';
    table = document.createElement('table');
    const body = document.createElement('tbody');
    table.append(body);
    const deleted = row(body);
    rowEdit = deleted.edit;
    rowDelete = deleted.remove;
    otherRowDelete = row(body).remove;
    outsideTheRow = document.createElement('input');
    document.body.append(heading, table, outsideTheRow);
  });

  afterEach(() => {
    for (const element of [heading, table, outsideTheRow]) {
      element.remove();
    }
  });

  it('hands focus on when the user moved it to another control in the row being deleted', () => {
    rowEdit.focus();

    handOffFocusFromRemovedOpener(rowDelete);

    expect(document.activeElement).toBe(heading);
    expect(heading.getAttribute('tabindex')).toBe('-1');
  });

  it('leaves focus the user moved outside the row being deleted alone', () => {
    outsideTheRow.focus();

    handOffFocusFromRemovedOpener(rowDelete);

    expect(document.activeElement).toBe(outsideTheRow);
  });

  it('leaves focus the user moved to another row alone, because that row stays', () => {
    otherRowDelete.focus();

    handOffFocusFromRemovedOpener(rowDelete);

    expect(document.activeElement).toBe(otherRowDelete);
  });
});
