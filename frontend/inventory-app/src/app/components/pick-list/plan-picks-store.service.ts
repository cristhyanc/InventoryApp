import { Injectable, OnDestroy } from '@angular/core';
import { MsalService } from '@azure/msal-angular';
import { BehaviorSubject, Observable, Subject, combineLatest, distinctUntilChanged, filter, map, take, takeUntil } from 'rxjs';
import { AccessService, MemberAccessState } from '../../access/access.service';
import { activeAccountKey } from '../../access/signed-in-account';
import { RolePreviewService } from '../../role-preview/role-preview.service';
import { SupportSessionService } from '../../support-session/support-session.service';
import { PlanPicksScope, StoredPlanPicks, clearStoredPlanPicks, readStoredPlanPicks, writeStoredPlanPicks } from './plan-picks-storage';

/**
 * A stored plan, read for one scope, together with the token that says whether that scope is still
 * the one in effect (issue #1021).
 *
 * The page cannot apply a restored plan the moment it is read: it has to wait for the machine list
 * before it knows which of the stored machines still exist. The token is what makes that wait safe -
 * {@link PlanPicksStoreService.isCurrentScope} refuses a plan whose identity, business or session
 * context moved on while the page was still getting ready for it.
 */
export interface PlanPicksRestoration {
  readonly scopeToken: number;
  readonly plan: StoredPlanPicks;
}

/** The scope in effect, and the token identifying it; `scope` is `null` while nothing may be stored. */
interface ResolvedScope {
  readonly token: number;
  readonly scope: PlanPicksScope | null;
}

/**
 * `undefined` while the scope is not known yet (the capabilities have not been read), `null` when it
 * is known that nothing may be stored or restored, and the scope itself otherwise.
 */
type ScopeAnswer = PlanPicksScope | null | undefined;

function scopeKey(answer: ScopeAnswer): string {
  if (answer === undefined) {
    return 'pending';
  }
  return answer === null ? 'none' : `${answer.accountKey}\u0000${answer.businessName}`;
}

/**
 * Whether a Plan picks plan may be kept in this tab right now, and the storage access that answers
 * it (issue #1021). `PickListComponent` is the only consumer.
 *
 * **Three questions decide it, and all three must be answered before anything is read.**
 *
 * - *Whose plan is it?* The signed-in identity (`activeAccountKey`, the same key every other cached
 *   answer in this application is scoped by) and the business `GET /api/me/access` reports. A stored
 *   plan names both and is restored for neither anybody else nor another business.
 * - *Is the access known?* `unread` is not "no business": it is "not read yet", and a plan must not
 *   be restored from it - nor from `unavailable`, which is a refused or failed capability read and
 *   therefore an identity with no confirmed membership to restore a business's figures for.
 * - *Is this an ordinary context?* A support session and a role preview are both deliberately
 *   memory-only contexts (`SupportSessionService`, `RolePreviewService`): the administrator is
 *   looking at somebody else's business, or at the application as another role, and neither may
 *   leave a business's product names and storage figures behind in the tab. Nothing is written or
 *   restored while either is active.
 *
 * **A scope change invalidates a restoration in flight.** Every distinct answer takes a new token
 * ({@link PlanPicksRestoration}), so a plan read for the scope the page opened in is dropped rather
 * than applied if the identity, the business or the session context changed while the page was
 * waiting for its machine list.
 *
 * **It observes lazily.** The capability read and the support-session probe are started by
 * subscribing to them, so this service subscribes on its first use - which is the Pick List page
 * asking - rather than in its constructor.
 */
@Injectable({ providedIn: 'root' })
export class PlanPicksStoreService implements OnDestroy {
  /** The scope in effect, or `null` while the first answer has not arrived. */
  private readonly resolved = new BehaviorSubject<ResolvedScope | null>(null);

  private token = 0;
  private observing = false;
  private readonly destroying$ = new Subject<void>();

  constructor(
    private readonly authService: MsalService,
    private readonly access: AccessService,
    private readonly supportSession: SupportSessionService,
    private readonly rolePreview: RolePreviewService
  ) {}

  ngOnDestroy(): void {
    this.destroying$.next();
    this.destroying$.complete();
  }

  /**
   * This tab's stored plan, once the scope is known: the plan and the token it was read under, or
   * `null` when there is nothing to restore or nothing may be restored at all. It emits exactly
   * once, so the caller has one answer to act on and nothing to unsubscribe.
   */
  restore(): Observable<PlanPicksRestoration | null> {
    this.ensureObserving();
    return this.resolved.pipe(
      filter((resolved): resolved is ResolvedScope => resolved !== null),
      take(1),
      map((resolved) => {
        if (resolved.scope === null) {
          return null;
        }
        const plan = readStoredPlanPicks(resolved.scope);
        return plan === null ? null : { scopeToken: resolved.token, plan };
      })
    );
  }

  /**
   * Whether `scopeToken` still names the scope in effect, asked before a plan read under it is put
   * on screen.
   */
  isCurrentScope(scopeToken: number): boolean {
    const resolved = this.resolved.value;
    return resolved !== null && resolved.scope !== null && resolved.token === scopeToken;
  }

  /**
   * Stores `plan` for the scope in effect, and answers whether the complete plan is now stored.
   * `false` means the caller must go on treating it as unsaved work: the scope is unknown or
   * ineligible, or the write itself was refused.
   */
  save(plan: StoredPlanPicks): boolean {
    this.ensureObserving();
    const resolved = this.resolved.value;
    if (resolved === null || resolved.scope === null) {
      return false;
    }
    return writeStoredPlanPicks(resolved.scope, plan);
  }

  /** Removes this tab's stored plan, whatever the scope: it is this tab's own entry either way. */
  clear(): void {
    clearStoredPlanPicks();
  }

  private ensureObserving(): void {
    if (this.observing) {
      return;
    }
    this.observing = true;

    combineLatest([this.access.access$, this.supportSession.active$, this.rolePreview.active$])
      .pipe(
        map(([access, sessionActive, previewActive]) => this.eligibleScope(access, sessionActive, previewActive)),
        distinctUntilChanged((left, right) => scopeKey(left) === scopeKey(right)),
        takeUntil(this.destroying$)
      )
      .subscribe((scope) => {
        // A scope that went back to "not known yet" - an identity change, whose capability read has
        // not answered - publishes the unresolved state again rather than a token nothing may be
        // restored under: the next real answer gets a token of its own.
        this.resolved.next(scope === undefined ? null : { token: ++this.token, scope });
      });
  }

  private eligibleScope(access: MemberAccessState, sessionActive: boolean, previewActive: boolean): ScopeAnswer {
    if (sessionActive || previewActive) {
      return null;
    }
    if (access.status === 'unread') {
      return undefined;
    }
    if (access.status !== 'granted' || access.access.businessName === '') {
      return null;
    }

    const accountKey = activeAccountKey(this.authService);
    return accountKey === null || accountKey === '' ? null : { accountKey, businessName: access.access.businessName };
  }
}
