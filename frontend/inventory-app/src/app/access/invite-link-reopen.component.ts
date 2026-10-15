import { Component } from '@angular/core';
import { RouterLink } from '@angular/router';

/**
 * The page at the tokenless `/invite` (issue #681, finding 4 of the #328 implementation review).
 *
 * `/invite` is where the address bar is left once an invite link's token has been stripped out of
 * it. The acceptance page answers there only while the tab holds a pending invite it has not read
 * yet (`pending-invite-token.ts`); once it has, a reload or a Back into that entry arrives here with
 * no token at all - the page held it only in memory, deliberately, and it is gone. Rather than
 * falling through to the wildcard and landing on the dashboard or the welcome page with no
 * explanation, this page says what happened and how to continue: open the invite link again.
 *
 * It carries no guard and makes no request: it explains, and does nothing else.
 */
@Component({
  selector: 'app-invite-link-reopen',
  standalone: true,
  imports: [RouterLink],
  template: `
    <div class="page">
      <header class="page-header">
        <div>
          <h1 class="page-title">Open your invite link again</h1>
          <p class="page-subtitle">This page no longer has the invite it was opened with.</p>
        </div>
      </header>

      <section class="card">
        <div class="card-body">
          <p class="text-md-body">
            For your security, the invite is removed from the address bar as soon as the page opens, so reloading or
            coming back to this page loses it. Open the invite link you were sent again to accept it. Nothing was
            changed.
          </p>
          <a routerLink="/" class="btn btn-primary mt-4 inline-flex">Go to the home page</a>
        </div>
      </section>
    </div>
  `
})
export class InviteLinkReopenComponent {}
