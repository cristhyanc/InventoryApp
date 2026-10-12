import { Component } from '@angular/core';
import { RouterLink } from '@angular/router';

/**
 * The page a member reaches when their role does not allow the route they asked for (issue #528).
 *
 * It names no capability, no role and none of the roles that would have been allowed, for the same
 * reason the API's refusal is a bare `403` that describes none of them: the role matrix is not
 * something a refusal teaches. It offers the Dashboard, which every role may open, so the page is
 * never a dead end.
 *
 * It is deliberately not the "access unavailable" page of issue #529. That one is for a person
 * whose *membership* is unusable - deactivated, revoked or ambiguous - and it is reached from
 * account-state routing rather than from a capability. This page is for a member in good standing
 * who asked for a page their role does not include.
 */
@Component({
  selector: 'app-access-not-allowed',
  standalone: true,
  imports: [RouterLink],
  template: `
    <div class="page">
      <header class="page-header">
        <div>
          <h1 class="page-title">You don't have access to this</h1>
          <p class="page-subtitle">Your role doesn't include this page. Ask the business Owner if you need it.</p>
        </div>
      </header>

      <section class="card">
        <div class="card-body">
          <p class="text-md-body">
            Nothing was changed, and the rest of the application is unaffected. You can keep working from the pages
            your role does include.
          </p>
          <a routerLink="/" class="btn btn-primary mt-4 inline-flex">Go to the Dashboard</a>
        </div>
      </section>
    </div>
  `
})
export class AccessNotAllowedComponent {}
