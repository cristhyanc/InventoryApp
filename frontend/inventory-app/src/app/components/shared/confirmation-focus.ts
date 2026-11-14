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
 * a refusal leaves focus on the opener, which is still on the row. Focus the user moved somewhere
 * else while the request was in flight is theirs to keep and is left alone.
 */
export function handOffFocusFromRemovedOpener(
  opener: HTMLElement | null,
  fallback: HTMLElement | null = null
): void {
  const focused = document.activeElement;
  // Already dropped (the row was removed before this ran), or still on the control that is about to
  // be removed - the two cases where nobody but this handoff decides where focus ends up.
  const dropped = !(focused instanceof HTMLElement) || focused === document.body || !focused.isConnected;
  if (!dropped && focused !== opener) {
    return;
  }

  if (fallback?.isConnected) {
    fallback.focus();
    return;
  }

  focusPageHeading();
}
