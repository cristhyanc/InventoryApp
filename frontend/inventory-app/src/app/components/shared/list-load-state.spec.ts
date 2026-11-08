import { ListLoadState } from './list-load-state';

describe('ListLoadState', () => {
  it('reports loaded once the only request succeeds', () => {
    const state = new ListLoadState();
    const token = state.start();
    expect(state.loaded).toBe(false);

    state.succeed(token);

    expect(state.loaded).toBe(true);
    expect(state.loadFailed).toBe(false);
  });

  it('stays unsettled while an earlier request is still outstanding', () => {
    const state = new ListLoadState();
    const first = state.start();
    const second = state.start();

    state.succeed(second);
    expect(state.loaded).toBe(false);

    state.succeed(first);
    expect(state.loaded).toBe(true);
  });

  it('reports the latest request failing, not an earlier one', () => {
    const state = new ListLoadState();
    const first = state.start();
    const second = state.start();

    state.fail(first);
    state.succeed(second);

    expect(state.loaded).toBe(true);
    expect(state.loadFailed).toBe(false);
  });

  /**
   * A request a newer one replaced (a `switchMap` unsubscribing it) never answers, so nothing ever
   * calls `succeed` or `fail` for it. Released, it must stop counting as outstanding, or the list
   * stays in neither the loaded nor the failed state for good: a blank card.
   */
  it('settles once a superseded request is released, so the newer answer shows', () => {
    const state = new ListLoadState();
    const superseded = state.start();
    const latest = state.start();

    state.release(superseded);
    state.succeed(latest);

    expect(state.loaded).toBe(true);
    expect(state.loadFailed).toBe(false);
  });

  it('ignores a release after the request already settled, so it is never counted twice', () => {
    const state = new ListLoadState();
    const first = state.start();
    state.succeed(first);
    state.release(first);

    const second = state.start();
    expect(state.loaded).toBe(false);

    state.succeed(second);
    expect(state.loaded).toBe(true);
  });

  it('ignores a second succeed or fail for the same request', () => {
    const state = new ListLoadState();
    const first = state.start();
    state.succeed(first);
    state.fail(first);

    const second = state.start();
    state.succeed(second);
    expect(state.loaded).toBe(true);
    expect(state.loadFailed).toBe(false);
  });

  it('does not report a released request as failed', () => {
    const state = new ListLoadState();
    const only = state.start();

    state.release(only);

    expect(state.loadFailed).toBe(false);
  });
});
