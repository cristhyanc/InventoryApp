/**
 * Tracks concurrent list loads for a routed page so a template can distinguish
 * "a request is still outstanding", "loaded successfully" and "the load failed"
 * instead of presenting an in-flight or failed request as confirmed empty data.
 *
 * A caller takes a token with `start()` before issuing a request and settles it
 * with `succeed(token)` or `fail(token)`. Only the latest token may update the
 * displayed collection (`isCurrent`), and both `loaded` and `loadFailed` stay
 * false until every outstanding request has settled, so an earlier response can
 * never reveal a stale table or empty state while a later request is still running.
 *
 * A request that is unsubscribed before it answers - typically one a `switchMap` replaced with a
 * newer search - is never settled by `succeed` or `fail`, so the caller also calls
 * `release(token)` when the request ends for any reason (an RxJS `finalize`). Without it the
 * replaced request would count as outstanding for good and the page would show neither its rows
 * nor an empty state. Each token settles once: whichever of `succeed`, `fail` or `release` comes
 * first counts, and the others do nothing.
 */
export class ListLoadState {
  private readonly pending = new Set<number>();
  private latestToken = 0;
  private latestFailed = false;

  /** True only once all requests have settled and the latest one succeeded. */
  loaded = false;

  /** True only once all requests have settled and the latest one failed. */
  loadFailed = false;

  start(): number {
    const token = ++this.latestToken;
    this.pending.add(token);
    this.loaded = false;
    this.loadFailed = false;
    return token;
  }

  isCurrent(token: number): boolean {
    return token === this.latestToken;
  }

  succeed(token: number): void {
    this.settle(token, false);
  }

  fail(token: number): void {
    this.settle(token, true);
  }

  /**
   * Stops counting a request that ended without an answer (unsubscribed or replaced). It is never
   * a failure: a replaced request is not the latest one, and a request that simply stopped being
   * listened to has nothing to report. A token that already settled is left alone.
   */
  release(token: number): void {
    this.settle(token, null);
  }

  private settle(token: number, failed: boolean | null): void {
    if (!this.pending.delete(token)) {
      return;
    }

    if (failed !== null && this.isCurrent(token)) {
      this.latestFailed = failed;
    }

    const allSettled = this.pending.size === 0;
    this.loaded = allSettled && !this.latestFailed;
    this.loadFailed = allSettled && this.latestFailed;
  }
}
