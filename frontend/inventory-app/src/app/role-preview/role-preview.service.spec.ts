import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { MsalBroadcastService, MsalService } from '@azure/msal-angular';
import { EventType } from '@azure/msal-browser';
import { Observable, Subject, of } from 'rxjs';
import { AccessService, MemberAccessState } from '../access/access.service';
import { SignedInIdentityDoubles, signedInIdentity } from '../access/identity-cache.spec-helpers';
import { BusinessService } from '../services/business.service';
import { ToastService } from '../services/toast.service';
import { SupportSessionService } from '../support-session/support-session.service';
import { PreviewRole, ROLE_PREVIEW_ENDED_MESSAGE } from './role-preview';
import { RolePreviewService } from './role-preview.service';

const UNAVAILABLE: MemberAccessState = { status: 'unavailable' };

interface Harness {
  readonly service: RolePreviewService;
  readonly identity: SignedInIdentityDoubles;
  /** The support session's own transition signal, which ends a preview. */
  readonly supportContextChanged: Subject<void>;
  readonly businessReset: jest.Mock;
  readonly accessRefresh: jest.Mock;
  readonly navigate: jest.Mock;
  readonly toastWarning: jest.Mock;
  /** Every role the service published, in order. */
  readonly published: (PreviewRole | null)[];
  /** Whether the shell is being asked to withhold the routed page, in order. */
  readonly changing: boolean[];
  /** How many times the effective context was reported as changed. */
  contextChanges(): number;
  /** Settles the navigation the transition made, as the router eventually does. */
  settleNavigation(): Promise<void>;
}

function configure(options: { navigation?: () => Promise<boolean> } = {}): Harness {
  const identity = signedInIdentity();
  const supportContextChanged = new Subject<void>();
  const businessReset = jest.fn();
  const accessRefresh = jest.fn(() => of(UNAVAILABLE) as Observable<MemberAccessState>);
  const navigate = jest.fn(options.navigation ?? (() => Promise.resolve(true)));
  const toastWarning = jest.fn();

  TestBed.configureTestingModule({
    providers: [
      { provide: BusinessService, useValue: { reset: businessReset } },
      { provide: AccessService, useValue: { refresh: accessRefresh } },
      { provide: Router, useValue: { navigate } },
      { provide: MsalService, useValue: identity.msal },
      { provide: MsalBroadcastService, useValue: identity.broadcast },
      { provide: ToastService, useValue: { warning: toastWarning } },
      { provide: SupportSessionService, useValue: { contextChanged$: supportContextChanged } }
    ]
  });

  const service = TestBed.inject(RolePreviewService);
  const published: (PreviewRole | null)[] = [];
  const changing: boolean[] = [];
  let contextChanges = 0;
  service.role$.subscribe((role) => published.push(role));
  service.contextChanging$.subscribe((value) => changing.push(value));
  service.contextChanged$.subscribe(() => contextChanges++);

  return {
    service,
    identity,
    supportContextChanged,
    businessReset,
    accessRefresh,
    navigate,
    toastWarning,
    published,
    changing,
    contextChanges: () => contextChanges,
    settleNavigation: async () => {
      await Promise.resolve();
      await Promise.resolve();
    }
  };
}

/**
 * `RolePreviewService` (issue #721): what a preview is while it lasts, what starting and exiting
 * one does to the page, and every way one ends.
 */
describe('RolePreviewService (issue #721)', () => {
  afterEach(() => TestBed.resetTestingModule());

  it('previews no role until one is chosen', () => {
    const harness = configure();

    expect(harness.service.currentRole()).toBeNull();
    expect(harness.published).toEqual([null]);
    expect(harness.navigate).not.toHaveBeenCalled();
  });

  it('starts a preview and takes the whole context transition', async () => {
    const harness = configure();

    harness.service.start('Operator');

    // The role is set before anything is issued, so the capability re-read below is the first
    // request answered as the previewed role rather than the last one answered as the caller.
    expect(harness.service.currentRole()).toBe('Operator');
    expect(harness.businessReset).toHaveBeenCalledTimes(1);
    expect(harness.contextChanges()).toBe(1);
    expect(harness.accessRefresh).toHaveBeenCalledTimes(1);
    expect(harness.navigate).toHaveBeenCalledWith(['/'], { onSameUrlNavigation: 'reload' });
    expect(harness.changing).toEqual([false, true]);

    await harness.settleNavigation();

    expect(harness.changing).toEqual([false, true, false]);
  });

  it('exits a preview and takes the same transition back', async () => {
    const harness = configure();
    harness.service.start('Manager');
    await harness.settleNavigation();

    harness.service.exit();

    expect(harness.service.currentRole()).toBeNull();
    expect(harness.published).toEqual([null, 'Manager', null]);
    expect(harness.businessReset).toHaveBeenCalledTimes(2);
    expect(harness.accessRefresh).toHaveBeenCalledTimes(2);
    expect(harness.navigate).toHaveBeenCalledTimes(2);
  });

  it('does nothing when the role already in view is chosen again', () => {
    const harness = configure();
    harness.service.start('Manager');

    harness.service.start('Manager');

    expect(harness.navigate).toHaveBeenCalledTimes(1);
    expect(harness.published).toEqual([null, 'Manager']);
  });

  it('does nothing when there is no preview to exit', () => {
    const harness = configure();

    harness.service.exit();

    expect(harness.navigate).not.toHaveBeenCalled();
    expect(harness.businessReset).not.toHaveBeenCalled();
  });

  it('switches directly from one previewed role to another', () => {
    const harness = configure();
    harness.service.start('Manager');

    harness.service.start('Operator');

    expect(harness.published).toEqual([null, 'Manager', 'Operator']);
    expect(harness.navigate).toHaveBeenCalledTimes(2);
  });

  /** Two transitions in flight at once release the page only when the last of them settles. */
  it('keeps the page withheld until the last overlapping transition settles', async () => {
    const settle: ((value: boolean) => void)[] = [];
    const harness = configure({ navigation: () => new Promise<boolean>((resolve) => settle.push(resolve)) });

    harness.service.start('Manager');
    harness.service.exit();
    expect(harness.changing).toEqual([false, true]);

    settle[0](true);
    await harness.settleNavigation();
    expect(harness.changing).toEqual([false, true]);

    settle[1](true);
    await harness.settleNavigation();

    expect(harness.changing).toEqual([false, true, false]);
  });

  it('releases the page even when the navigation is refused', async () => {
    const harness = configure({ navigation: () => Promise.reject(new Error('refused')) });

    harness.service.start('Owner');
    await harness.settleNavigation();

    expect(harness.changing).toEqual([false, true, false]);
  });

  it('ends the preview and explains it when the API refuses the preview itself', () => {
    const harness = configure();
    harness.service.start('Operator');

    harness.service.handleRefusedPreview();

    expect(harness.service.currentRole()).toBeNull();
    expect(harness.toastWarning).toHaveBeenCalledTimes(1);
    expect(harness.toastWarning).toHaveBeenCalledWith(ROLE_PREVIEW_ENDED_MESSAGE, 'Role preview ended');
    expect(harness.navigate).toHaveBeenCalledTimes(2);
  });

  it('ignores a refusal that arrives after the preview has already ended', () => {
    const harness = configure();
    harness.service.start('Operator');
    harness.service.exit();

    harness.service.handleRefusedPreview();

    expect(harness.toastWarning).not.toHaveBeenCalled();
    expect(harness.navigate).toHaveBeenCalledTimes(2);
  });

  /**
   * A support session starting or ending already drops the business, re-reads the capabilities and
   * rebuilds the page, so the preview is only forgotten here - a second transition would navigate
   * twice. The role is cleared synchronously on the signal, which is published *before* that
   * transition's own capability read, so the read is made without the header.
   */
  it('forgets the preview on a support-context transition, without a transition of its own', () => {
    const harness = configure();
    harness.service.start('Manager');
    const navigations = harness.navigate.mock.calls.length;

    harness.supportContextChanged.next();

    expect(harness.service.currentRole()).toBeNull();
    expect(harness.published).toEqual([null, 'Manager', null]);
    expect(harness.navigate).toHaveBeenCalledTimes(navigations);
    expect(harness.toastWarning).not.toHaveBeenCalled();
  });

  it('does nothing on a support-context transition while no preview is active', () => {
    const harness = configure();

    harness.supportContextChanged.next();

    expect(harness.published).toEqual([null]);
    expect(harness.businessReset).not.toHaveBeenCalled();
  });

  it.each([
    ['the signed-in account changes', (identity: SignedInIdentityDoubles) => identity.switchAccount('member-2')],
    [
      'the administrator signs out',
      (identity: SignedInIdentityDoubles) => {
        identity.signIn(null);
        identity.raise(EventType.LOGOUT_SUCCESS);
      }
    ]
  ])('ends the preview when %s', (_case, change) => {
    const harness = configure();
    harness.service.start('Operator');
    const navigations = harness.navigate.mock.calls.length;

    change(harness.identity);

    expect(harness.service.currentRole()).toBeNull();
    // The shell discards the previous identity's business context and both caches drop their
    // answers themselves (issues #603 and #604), so nothing is navigated or re-read from here.
    expect(harness.navigate).toHaveBeenCalledTimes(navigations);
    expect(harness.toastWarning).not.toHaveBeenCalled();
  });

  /**
   * A reload and a new tab start with no preview, which is the whole of issue #721's persistence
   * design: there is nothing to resume, and nothing to find in a saved page or a shared link.
   */
  it('persists nothing, so a new instance of this tab previews nothing', () => {
    const harness = configure();
    harness.service.start('Operator');

    expect(window.localStorage.length).toBe(0);
    expect(window.sessionStorage.length).toBe(0);
    expect(window.location.search).not.toContain('Operator');

    TestBed.resetTestingModule();

    expect(configure().service.currentRole()).toBeNull();
  });

  it('keeps the preview when a notification reports the same identity again', () => {
    const harness = configure();
    harness.service.start('Operator');

    harness.identity.raise(EventType.ACTIVE_ACCOUNT_CHANGED);
    harness.identity.inProgress$.next(harness.identity.inProgress$.value);

    expect(harness.service.currentRole()).toBe('Operator');
  });
});
