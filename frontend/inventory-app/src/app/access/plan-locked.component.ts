import { Component, OnDestroy, OnInit } from '@angular/core';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { Observable, Subject, map, of, switchMap, takeUntil } from 'rxjs';
import { AccessService } from './access.service';
import { NavLink, activeNavLink, primaryNavigation } from '../layout/navigation';
import { PlanFeature, requiredPlanOf } from './plan-feature';
import { PLAN_LOCKED_FROM_PARAM, planLockOf, planLockSentence } from './plan-lock';

/** The destination this page is about, resolved from the declared navigation. */
interface LockedDestination {
  readonly label: string;
  readonly route: string | null;
  readonly feature: PlanFeature;
}

/**
 * The "needs a plan" page (issue #979, in the same shape `ProviderLockedComponent` gives a provider
 * lock): what a member sees instead of a route this business has not bought, whether they clicked
 * the locked menu entry or typed the URL, because `requirePlan` sends both here.
 *
 * **It explains, and it never refuses.** Every endpoint behind the page it stands in for enforces
 * the same plan itself (`[RequirePlan]`, or the Nayax credential gate's own `PlanRequired` refusal),
 * so this page protects nothing: it replaces a `403` or a `409` with one sentence and a link to the
 * Plan page (issue #970), which is the one place this application lets an Owner see what their plan
 * includes. It is deliberately never a Connect action, unlike the provider lock beside it: buying a
 * plan is not something this page can do, and billing is explicitly out of scope of issue #979.
 *
 * **It re-reads the entitlement for the destination's own feature** rather than trusting the guard
 * that redirected here, for the same reason `ProviderLockedComponent` does: the URL is a question,
 * not a verdict, so a page opened directly reports the plan this business is on right now -
 * including "open", which an upgrade made in another tab leaves it in.
 */
@Component({
  selector: 'app-plan-locked',
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
          @if (lock === 'open') {
            <p class="text-md-body">This business's plan now includes it. Open it again, or carry on from the Dashboard.</p>
          }

          <div class="mt-4 flex flex-wrap items-center gap-3">
            @if (lock === 'open' && destinationRoute) {
              <a [routerLink]="destinationRoute" class="btn btn-primary inline-flex">Open {{ destinationLabel }}</a>
            } @else {
              <a routerLink="/plan" class="btn btn-primary inline-flex">View the Plan page</a>
            }
            <a routerLink="/" class="btn btn-secondary inline-flex">Go to the Dashboard</a>
          </div>
        </div>
      </section>
    </div>
  `
})
export class PlanLockedComponent implements OnInit, OnDestroy {
  /** Starts as the locked state, which is the state this page is reached in by default. */
  lock: 'open' | 'locked' = 'locked';

  title = 'This page needs a different plan';

  sentence = '';

  destinationLabel = 'This page';

  destinationRoute: string | null = null;

  private readonly destroying$ = new Subject<void>();

  constructor(
    private readonly route: ActivatedRoute,
    private readonly access: AccessService
  ) {}

  ngOnInit(): void {
    this.route.queryParamMap
      .pipe(
        map((params) => destinationOf(params.get(PLAN_LOCKED_FROM_PARAM))),
        switchMap((destination) => this.describe(destination)),
        takeUntil(this.destroying$)
      )
      .subscribe();
  }

  ngOnDestroy(): void {
    this.destroying$.next();
    this.destroying$.complete();
  }

  private describe(destination: LockedDestination | null): Observable<void> {
    this.destinationLabel = destination?.label ?? 'This page';
    this.destinationRoute = destination?.route ?? null;

    if (destination === null) {
      // No destination was named, so nothing can be read about one feature. The page still has to
      // say something true: this is the generic "a different plan is needed" explanation.
      this.lock = 'locked';
      this.title = 'This page needs a different plan';
      this.sentence = 'The page you asked for is not included in this business’s current plan.';
      return of(undefined);
    }

    return this.access.access$.pipe(
      map((state) => {
        const entitlement = state.status === 'granted' ? state.access.entitlement : undefined;
        this.lock = planLockOf(entitlement, destination.feature);
        this.title =
          this.lock === 'locked'
            ? `${destination.label} needs the ${requiredPlanOf(destination.feature)} plan`
            : `${destination.label} is available`;
        this.sentence =
          this.lock === 'locked'
            ? planLockSentence(destination.feature, destination.label)
            : `This business's plan now includes ${destination.label}.`;
      })
    );
  }
}

/**
 * The declared destination a refused URL belongs to, or `null` for a URL that is not a plan-locked
 * destination at all - a hand-typed `/needs-plan`, or an address this build does not know. Nothing
 * is inferred from the URL itself: it only selects an entry of `primaryNavigation`, and an entry
 * that declares no plan feature is not a destination this page can explain.
 */
function destinationOf(from: string | null): LockedDestination | null {
  if (from === null) {
    return null;
  }

  const navLink: NavLink | undefined = activeNavLink(primaryNavigation, from);
  if (navLink?.planLockedWithout === undefined) {
    return null;
  }

  return { label: navLink.label, route: navLink.route, feature: navLink.planLockedWithout };
}
