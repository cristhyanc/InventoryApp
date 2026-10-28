import { DOCUMENT, NgTemplateOutlet } from '@angular/common';
import { Component, Inject, OnDestroy, OnInit } from '@angular/core';
import { Meta, Title } from '@angular/platform-browser';
import { Router, RouterLink } from '@angular/router';
import { MsalService } from '@azure/msal-angular';
import { Subject, catchError, of, takeUntil } from 'rxjs';
import { BUSINESS_APPLICATION_ROUTE } from '../../access/account-state.guard';
import { isExternalIdConfigured, loginRequest } from '../../auth-config';
import { SIGN_IN_ROUTE } from '../../auth/intended-destination';
import { IconComponent } from '../../components/shared/icon.component';
import {
  CORE_INVENTORY_POINTS,
  INTEGRATION_FACTS,
  LANDING_DESCRIPTION,
  LANDING_FAQS,
  LANDING_FEATURES,
  LANDING_SECTIONS,
  LANDING_STEPS,
  LANDING_TITLE,
  NAYAX_POINTS
} from './landing-content';
import { LandingDashboardPreviewComponent } from './landing-dashboard-preview.component';
import { LandingHeaderComponent } from './landing-header.component';
import { APP_ENTRY_URL } from './landing-routes';

/**
 * The public Restockroo landing page (issue #954), shown at `/` to a signed-out visitor and at
 * `/home` to everyone. It is rendered in the public layout (`PUBLIC_LAYOUT_DATA`), so the
 * authenticated shell is not on the page.
 *
 * **It makes no request.** The copy and the example dashboard are static (`landing-content.ts`),
 * and the page injects no API service: a visitor can read it, refresh it and leave without the
 * application asking any business, account or Nayax endpoint anything.
 *
 * **Its actions reuse the existing journeys, unchanged:**
 *
 * - **Sign in** is the header's own sign-in decision: with a customer identity authority configured
 *   it opens the `/sign-in` chooser, and otherwise it redirects straight to the workforce authority,
 *   asking to return to `/` - the application's entry route - rather than to the landing page.
 * - **Get started** opens the business application route (`/apply`). That route is guarded, so a
 *   signed-out visitor is taken through sign-in first (and the chooser offers creating an account
 *   where a customer identity authority is configured), and the application form sends anyone it
 *   is not for - a member, or anyone while self-service onboarding is switched off - on to the page
 *   their account state calls for.
 * - **Open app**, offered instead of both to a signed-in person, is a link to `/`.
 *
 * The page publishes its own title and description while it is shown and puts back what it found
 * when it is left, because the authenticated pages set neither.
 */
@Component({
  selector: 'app-landing-page',
  standalone: true,
  imports: [IconComponent, LandingDashboardPreviewComponent, LandingHeaderComponent, NgTemplateOutlet, RouterLink],
  templateUrl: './landing-page.component.html'
})
export class LandingPageComponent implements OnInit, OnDestroy {
  readonly sections = LANDING_SECTIONS;
  readonly features = LANDING_FEATURES;
  readonly corePoints = CORE_INVENTORY_POINTS;
  readonly nayaxPoints = NAYAX_POINTS;
  readonly integrationFacts = INTEGRATION_FACTS;
  readonly steps = LANDING_STEPS;
  readonly faqs = LANDING_FAQS;
  readonly appEntry = APP_ENTRY_URL;
  readonly year = new Date().getFullYear();

  isSignedIn = false;

  private previousTitle = '';
  private previousDescription: string | null = null;
  private readonly destroying$ = new Subject<void>();

  constructor(
    private readonly msal: MsalService,
    private readonly router: Router,
    private readonly title: Title,
    private readonly meta: Meta,
    @Inject(DOCUMENT) private readonly document: Document
  ) {}

  ngOnInit(): void {
    this.previousTitle = this.title.getTitle();
    this.previousDescription = this.meta.getTag('name="description"')?.content ?? null;
    this.title.setTitle(LANDING_TITLE);
    this.meta.updateTag({ name: 'description', content: LANDING_DESCRIPTION });

    // Whether to offer Open app rather than Sign in and Get started. It is read once any redirect
    // in progress has been handled, as the guards read it, so a person returning from sign-in is
    // not offered Sign in again.
    this.isSignedIn = this.hasAccount();
    this.msal
      .handleRedirectObservable()
      .pipe(
        catchError(() => of(null)),
        takeUntil(this.destroying$)
      )
      .subscribe(() => (this.isSignedIn = this.hasAccount()));
  }

  ngOnDestroy(): void {
    this.destroying$.next();
    this.destroying$.complete();
    this.title.setTitle(this.previousTitle);
    if (this.previousDescription === null) {
      this.meta.removeTag('name="description"');
    } else {
      this.meta.updateTag({ name: 'description', content: this.previousDescription });
    }
  }

  signIn(): void {
    if (isExternalIdConfigured()) {
      this.router.navigateByUrl(`/${SIGN_IN_ROUTE}`);
      return;
    }

    this.msal.loginRedirect({ ...loginRequest, redirectStartPage: APP_ENTRY_URL });
  }

  getStarted(): void {
    this.router.navigateByUrl(`/${BUSINESS_APPLICATION_ROUTE}`);
  }

  /**
   * Scrolls to a section and moves keyboard focus to it, so the next Tab continues from there. The
   * scroll is instant for a visitor who asked the system for reduced motion.
   */
  scrollToSection(id: string, event?: Event): void {
    event?.preventDefault();
    const section = this.document.getElementById(id);
    if (!section) {
      return;
    }

    section.scrollIntoView({ behavior: this.prefersReducedMotion() ? 'auto' : 'smooth', block: 'start' });
    section.focus({ preventScroll: true });
  }

  skipToMain(event: Event): void {
    event.preventDefault();
    this.document.getElementById('main-content')?.focus();
  }

  private hasAccount(): boolean {
    return this.msal.instance.getAllAccounts().length > 0;
  }

  private prefersReducedMotion(): boolean {
    return this.document.defaultView?.matchMedia?.('(prefers-reduced-motion: reduce)').matches ?? false;
  }
}
