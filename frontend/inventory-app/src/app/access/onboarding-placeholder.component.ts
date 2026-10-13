import { Component, inject } from '@angular/core';
import { ActivatedRoute } from '@angular/router';

/**
 * A minimal stand-in for a route {@link accountStateGuard} (issue #529) sends a person to, until
 * the real page it names is built: `welcome` and `application-rejected` by #508's tasks, and
 * `application-pending` by #508's tasks. Building any of those pages is explicitly out of scope
 * here - this component exists only so the route, the redirect and the guard bypass around it are
 * real and testable before that work starts. `invite/:token` carried this placeholder until issue
 * #535 replaced it with `InviteAcceptanceComponent`.
 *
 * It reads its heading from the route's own `data`, rather than being one component per route,
 * because every one of these pages is this same placeholder today and the real pages that replace
 * them will each own their own component and route entry instead of this shared one.
 */
@Component({
  selector: 'app-onboarding-placeholder',
  standalone: true,
  template: `
    <div class="page">
      <header class="page-header">
        <div>
          <h1 class="page-title">{{ heading }}</h1>
        </div>
      </header>
    </div>
  `
})
export class OnboardingPlaceholderComponent {
  readonly heading: string = inject(ActivatedRoute).snapshot.data['heading'] ?? '';
}
