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
 */
export class ListLoadState {
  private activeCount = 0;
  private latestToken = 0;
  private latestFailed = false;

  /** True only once all requests have settled and the latest one succeeded. */
  loaded = false;

  /** True only once all requests have settled and the latest one failed. */
  loadFailed = false;

  start(): number {
    this.activeCount++;
    this.loaded = false;
    this.loadFailed = false;
    return ++this.latestToken;
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

  private settle(token: number, failed: boolean): void {
    this.activeCount--;
    if (this.isCurrent(token)) {
      this.latestFailed = failed;
    }

    const allSettled = this.activeCount === 0;
    this.loaded = allSettled && !this.latestFailed;
    this.loadFailed = allSettled && this.latestFailed;
  }
}
