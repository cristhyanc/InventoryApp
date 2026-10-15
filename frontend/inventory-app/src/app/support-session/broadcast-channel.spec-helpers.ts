/**
 * A same-process double for the global `BroadcastChannel`, scoped by channel name the way the real
 * one is scoped by origin: two instances constructed with the same name deliver to each other, and
 * neither ever delivers its own posted message back to itself - the one asymmetry that makes a
 * multi-tab test mean anything (`FakeBroadcastChannel` would otherwise just be an event emitter
 * talking to itself).
 *
 * `BroadcastChannel` does not exist in this build's Jest environment at all, which is why
 * `SupportSessionService` feature-detects it; a spec that wants the multi-tab behaviour installs
 * this as the global and resets it between tests ({@link installBroadcastChannel}).
 *
 * This file is test-only (`tsconfig.spec.json` compiles it; the application build never does).
 */
export class FakeBroadcastChannel {
  private static readonly channels = new Map<string, Set<FakeBroadcastChannel>>();
  private readonly listeners = new Set<(event: MessageEvent) => void>();
  private closed = false;

  constructor(private readonly name: string) {
    const peers = FakeBroadcastChannel.channels.get(name) ?? new Set();
    peers.add(this);
    FakeBroadcastChannel.channels.set(name, peers);
  }

  postMessage(data: unknown): void {
    if (this.closed) {
      return;
    }
    const peers = FakeBroadcastChannel.channels.get(this.name);
    if (!peers) {
      return;
    }
    for (const peer of peers) {
      if (peer === this || peer.closed) {
        continue;
      }
      for (const listener of peer.listeners) {
        listener({ data } as MessageEvent);
      }
    }
  }

  addEventListener(_type: 'message', listener: (event: MessageEvent) => void): void {
    this.listeners.add(listener);
  }

  removeEventListener(_type: 'message', listener: (event: MessageEvent) => void): void {
    this.listeners.delete(listener);
  }

  close(): void {
    this.closed = true;
    FakeBroadcastChannel.channels.get(this.name)?.delete(this);
  }

  static reset(): void {
    FakeBroadcastChannel.channels.clear();
  }
}

/** Installs the double as the global for one test and forgets every channel of the previous one. */
export function installBroadcastChannel(): void {
  (globalThis as unknown as { BroadcastChannel: unknown }).BroadcastChannel = FakeBroadcastChannel;
  FakeBroadcastChannel.reset();
}

/** Removes it again, so a spec that does not install it still sees the real environment. */
export function removeBroadcastChannel(): void {
  delete (globalThis as { BroadcastChannel?: unknown }).BroadcastChannel;
}
