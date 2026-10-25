import { Component, OnDestroy, OnInit } from '@angular/core';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { Observable, Subject, combineLatest, map, of, switchMap, takeUntil } from 'rxjs';
import { AccessService } from '../access/access.service';
import { NavLink, activeNavLink, primaryNavigation } from '../layout/navigation';
import { ProviderAvailabilityService } from './provider-availability.service';
import { ProviderFeature } from './provider-feature-permissions';
import {
  PROVIDER_LOCKED_FROM_PARAM,
  ProviderLock,
  providerFeatureReadLabels,
  providerLockOf,
  providerLockSentence
} from './provider-lock';

/** The destination this page is about, resolved from the declared navigation. */
interface LockedDestination {
  readonly label: string;
  readonly route: string | null;
  readonly feature: ProviderFeature;
}

/**
 * The "needs a provider" page (issue #729, child E2 of issue #329, decision P6): what an operator
 * sees instead of a live Nayax page while the provider cannot serve it - whether they clicked the
 * locked menu entry or typed the URL, because `requireProvider` sends both here.
 *
 * **It explains, and it never refuses.** Every endpoint behind the page it stands in for authorizes
 * and answers for itself (AGENTS.md § Nayax integration), so this page protects nothing: it replaces
 * a screen full of `409`s with one sentence and the one action that fixes it.
 *
 * **The two locks ask for opposite things, so they are never worded as one.** With no usable
 * connection it offers **Connect**, to a member who may manage integrations. With a usable
 * connection that Nayax has refused one read of, it says the page is waiting for a Nayax permission
 * and names the refused read and its endpoint - which is what a support request to Nayax has to ask
 * for - and offers no Connect button, because re-entering credentials that already work fixes
 * nothing. The Nayax permission *key* behind that read has no published name (see
 * `provider-lock.ts`), so none is printed.
 *
 * **It is never a dead end, and never claims the business has lost anything.** It links to the
 * Dashboard and says what still works without a provider: inventory, purchases, expenses, and the
 * two reports issue #868 deliberately leaves unlocked, Bookkeeping and GST. Since that issue the
 * destination may be one of the five reports over stored provider records rather than a live Nayax
 * page, so the sentence it leads with comes from `providerLockSentence`, which words a connection
 * lock from what the destination actually needs; the sales those reports read are still stored and
 * are reported again as soon as Nayax is connected.
 */
@Component({
  selector: 'app-provider-locked',
  standalone: true,
  imports: [RouterLink],
  template: `
    <div class="page">
      <header class="page-header">
        <div>
          <h1 class="page-title">{{ title }}</h1>
          <p class="page-subtitle">{{ sentence }}</p>
        </div>
      </header>

      <section class="card">
        <div class="card-body">
          @if (lock === 'permission') {
            <p class="text-md-body">
              Ask whoever administers this business's Nayax account to grant this application permission for that
              area, then re-test the connection on the Integrations page. Nothing else is affected: the pages whose
              own Nayax reads are granted keep working.
            </p>
            @if (readLabels.length > 0) {
              <ul class="mt-3 list-disc pl-5 text-md-body">
                @for (read of readLabels; track read) {
                  <li>{{ read }}</li>
                }
              </ul>
            }
          } @else if (lock === 'connect') {
            <p class="text-md-body">
              Nothing has been lost. Inventory, purchases and expenses work without a provider, the Bookkeeping and
              GST reports stay open, and the sales already imported are still stored - the reports over them open
              again, unchanged, as soon as Nayax is connected.
            </p>
          } @else {
            <p class="text-md-body">
              Nayax can serve this page now. Open it again, or carry on from the Dashboard.
            </p>
          }

          <div class="mt-4 flex flex-wrap items-center gap-3">
            @if (lock === 'connect' && canManageIntegration) {
              <a routerLink="/admin/nayax-settings" class="btn btn-primary inline-flex">Connect Nayax</a>
            }
            @if (lock === 'open' && destinationRoute) {
              <a [routerLink]="destinationRoute" class="btn btn-primary inline-flex">Open {{ destinationLabel }}</a>
            }
            <a routerLink="/" class="btn btn-secondary inline-flex">Go to the Dashboard</a>
          </div>

          @if (lock === 'connect' && !canManageIntegration) {
            <p class="mt-3 text-md-body text-md-gray-600">
              Ask the business Owner to connect Nayax; your role does not include the Integrations page.
            </p>
          }
        </div>
      </section>
    </div>
  `
})
export class ProviderLockedComponent implements OnInit, OnDestroy {
  /** Starts as the connection lock, which is the state this page is reached in by default. */
  lock: ProviderLock = 'connect';

  title = 'This page needs Nayax';

  sentence = '';

  readLabels: readonly string[] = [];

  destinationLabel = 'This page';

  destinationRoute: string | null = null;

  /** Whether the Connect button is offered: only a member who may manage integrations can act. */
  canManageIntegration = false;

  private readonly destroying$ = new Subject<void>();

  constructor(
    private readonly route: ActivatedRoute,
    private readonly availability: ProviderAvailabilityService,
    private readonly access: AccessService
  ) {}

  ngOnInit(): void {
    const destination$ = this.route.queryParamMap.pipe(
      map((params) => destinationOf(params.get(PROVIDER_LOCKED_FROM_PARAM)))
    );

    combineLatest([destination$, this.access.allows('Integration.Manage')])
      .pipe(
        switchMap(([destination, canManage]) => this.describe(destination, canManage)),
        takeUntil(this.destroying$)
      )
      .subscribe();
  }

  ngOnDestroy(): void {
    this.destroying$.next();
    this.destroying$.complete();
  }

  /**
   * Re-reads the availability answer for the destination's own feature rather than trusting the
   * guard that redirected here: the URL is a question, not a verdict, so a page opened directly
   * reports the state the signal is actually in - including `open`, which a connection made in
   * another tab leaves it in.
   */
  private describe(destination: LockedDestination | null, canManage: boolean): Observable<void> {
    this.canManageIntegration = canManage;
    this.destinationLabel = destination?.label ?? 'This page';
    this.destinationRoute = destination?.route ?? null;

    if (destination === null) {
      // No destination was named, so nothing can be read about one feature. The page still has to
      // say something true: this is the generic "a provider is needed" explanation.
      this.lock = 'connect';
      this.readLabels = [];
      this.title = 'This page needs Nayax';
      this.sentence = 'The page you asked for reads live data from Nayax, and no Nayax connection was named.';
      return of(undefined);
    }

    return this.availability.featureAvailability(destination.feature).pipe(
      map((answer) => {
        this.lock = providerLockOf(answer);
        this.readLabels = this.lock === 'permission' ? providerFeatureReadLabels(destination.feature) : [];
        this.title =
          this.lock === 'permission'
            ? `${destination.label} is waiting for a Nayax permission`
            : `${destination.label} needs Nayax`;
        this.sentence = providerLockSentence(this.lock, destination.label, destination.feature);
      })
    );
  }
}

/**
 * The declared destination a refused URL belongs to, or `null` for a URL that is not a live provider
 * destination at all - a hand-typed `/needs-provider`, or an address this build does not know.
 * Nothing is inferred from the URL itself: it only selects an entry of `primaryNavigation`, and an
 * entry that declares no live Nayax read is not a destination this page can explain.
 */
function destinationOf(from: string | null): LockedDestination | null {
  if (from === null) {
    return null;
  }

  const navLink: NavLink | undefined = activeNavLink(primaryNavigation, from);
  if (navLink?.lockedWithout === undefined) {
    return null;
  }

  return { label: navLink.label, route: navLink.route, feature: navLink.lockedWithout };
}
