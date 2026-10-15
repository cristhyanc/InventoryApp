import { Component } from '@angular/core';
import { RouterLink } from '@angular/router';

/**
 * The page at the tokenless `/invite` (issue #681, finding 4 of the #328 implementation review).
 *
 * `/invite` is where the address bar is left once an invite link's token has been stripped out of
 * it, so a reload, a Back into that entry, or a sign-in redirect started from it arrives here with
 * no token at all - the token was only ever held in memory, deliberately, and is gone. Rather than
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
