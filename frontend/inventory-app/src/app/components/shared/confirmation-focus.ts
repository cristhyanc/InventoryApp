/**
 * Where keyboard focus goes when a confirmation closes, and where it goes next when the control it
 * was returned to disappears with the row the confirmed action removed (issue #1277).
 *
 * `ConfirmationDialogComponent` returns focus to the control that opened it the instant it closes,
 * which is exactly right for a cancelled or a refused delete: the row is still there and so is its
 * button. A *confirmed* delete is different, because it is asynchronous - the caller clears its
 * pending state, the dialog closes and focus lands back on the row's Delete button while the
 * request is still in flight. When the request succeeds and the re-read list drops that row, the
 * focused button goes with it and focus falls to the document body, where Tab starts again from the
 * top of the page. Only the caller knows that moment, so the caller finishes the handoff: it takes
 * the opener the dialog hands it with `confirmed` and calls
 * {@link handOffFocusFromRemovedOpener} from the action's own success path.
 */

/**
 * What a list renders one record in: a table row, or a card in a list of cards. Every control of
 * the record a confirmed delete removes is inside the opener's own one, which is how the handoff
 * tells "the user moved to Edit on the row that is about to go" from "the user moved somewhere that
 * will still be there".
 */
const RECORD_CONTAINERS = 'tr,li';

/**
 * The page's own heading, as the last place focus can land. A heading carries no tabindex by
 * default, so it is not programmatically focusable until one is added - the same `tabindex="-1"`
 * the landing page's own `#main-content` already uses to be a focus target without joining the tab
 * order.
 */
export function focusPageHeading(): void {
  const heading = document.querySelector<HTMLElement>('h1');
  if (heading === null) {
    return;
  }
  if (!heading.hasAttribute('tabindex')) {
    heading.setAttribute('tabindex', '-1');
  }
  heading.focus();
}

/**
 * Hands focus on once a confirmed action has succeeded and the control the dialog returned focus to
 * is going away with the refreshed list: to `fallback` when the caller named one, otherwise to the
 * page's heading.
 *
 * Call it only from such an action's success path, because that is what says the opener is doomed;
 * a refusal leaves focus on the opener, which is still on the row. Focus the user moved outside the
 * record being removed while the request was in flight is theirs to keep and is left alone.
 */
export function handOffFocusFromRemovedOpener(
  opener: HTMLElement | null,
  fallback: HTMLElement | null = null
): void {
  if (!focusIsThisHandoffsToMove(opener)) {
    return;
  }

  if (fallback?.isConnected) {
    fallback.focus();
    return;
  }

  focusPageHeading();
}

/**
 * Whether where focus is right now is this handoff's to move. It is in three cases: focus was
 * already dropped (the row went before this ran); it is still on the opener the dialog returned it
 * to; or the user moved it to another control of the same record - the row being deleted takes its
 * Edit button with it just as surely as the Delete button the dialog returned focus to, so leaving
 * focus there would drop it on the document body a moment later. Focus the user moved outside that
 * record lands on something that is still there, so it is left exactly where they put it.
 */
function focusIsThisHandoffsToMove(opener: HTMLElement | null): boolean {
  const focused = document.activeElement;
  if (!(focused instanceof HTMLElement) || focused === document.body || !focused.isConnected) {
    return true;
  }
  if (focused === opener) {
    return true;
  }

  const record = opener?.closest(RECORD_CONTAINERS) ?? null;
  return record !== null && record.contains(focused);
}
