import { Component, ElementRef, EventEmitter, HostListener, Input, Output, ViewChild } from '@angular/core';
import { RouterLink } from '@angular/router';
import { IconComponent } from '../../components/shared/icon.component';
import { LANDING_SECTIONS, LandingSection } from './landing-content';
import { APP_ENTRY_URL, PUBLIC_HOME_ROUTE } from './landing-routes';

/**
 * The landing page's own header (issue #954): the Restockroo brand, the section links, and the
 * account actions - **Sign in** and **Get started** for a signed-out visitor, **Open app** for a
 * signed-in person.
 *
 * It decides nothing about authentication. It reports which action was chosen and which section
 * was asked for, and the page acts on it; **Open app** is an ordinary link to the application's
 * entry route.
 *
 * Below the `lg` breakpoint the section links and account actions move into a menu opened by one
 * button. The button exposes `aria-expanded` and names the panel it controls; the panel closes on
 * Escape (returning focus to the button), on a second press, and after a link in it is chosen.
 */
@Component({
  selector: 'app-landing-header',
  standalone: true,
  imports: [IconComponent, RouterLink],
  template: `
    <header class="sticky top-0 z-30 border-b border-rr-line bg-white/95 backdrop-blur">
      <div class="mx-auto flex h-16 max-w-7xl items-center gap-4 px-4 sm:px-8">
        <a
          [routerLink]="homeLink"
          class="flex min-h-11 items-center gap-2 rounded-lg text-lg font-bold text-rr-navy no-underline"
          data-testid="landing-brand"
        >
          <span class="flex h-8 w-8 items-center justify-center rounded-lg bg-rr-teal text-white" aria-hidden="true">
            <app-icon name="inventory_2" [size]="18" />
          </span>
          Restockroo
        </a>

        <nav class="ml-8 hidden items-center gap-1 lg:flex" aria-label="Page sections">
          @for (section of sections; track section.id) {
            <a
              [href]="'#' + section.id"
              class="flex min-h-11 items-center rounded-lg px-3 text-sm font-medium text-rr-muted no-underline hover:text-rr-navy"
              (click)="choose(section, $event)"
            >{{ section.label }}</a>
          }
        </nav>

        <div class="ml-auto flex items-center gap-2">
          @if (isSignedIn) {
            <a [routerLink]="appEntry" class="rr-button-primary" data-testid="landing-open-app">Open app</a>
          } @else {
            <button type="button" class="rr-button-ghost hidden lg:inline-flex" data-testid="landing-sign-in" (click)="signInRequested.emit()">
              Sign in
            </button>
            <button type="button" class="rr-button-primary" data-testid="landing-get-started" (click)="getStartedRequested.emit()">
              Get started
            </button>
          }
          <button
            #menuButton
            type="button"
            class="flex h-11 w-11 items-center justify-center rounded-lg border border-rr-line bg-white text-rr-navy lg:hidden"
            [attr.aria-expanded]="isMenuOpen"
            aria-controls="landing-mobile-menu"
            [attr.aria-label]="isMenuOpen ? 'Close menu' : 'Open menu'"
            data-testid="landing-menu-button"
            (click)="toggleMenu()"
          >
            <app-icon [name]="isMenuOpen ? 'close' : 'menu'" variant="outlined" [size]="22" />
          </button>
        </div>
      </div>

      <div id="landing-mobile-menu" class="border-t border-rr-line bg-white lg:hidden" [hidden]="!isMenuOpen" data-testid="landing-mobile-menu">
        <nav class="mx-auto flex max-w-7xl flex-col px-4 py-3 sm:px-8" aria-label="Page sections menu">
          @for (section of sections; track section.id) {
            <a
              [href]="'#' + section.id"
              class="flex min-h-11 items-center rounded-lg px-3 text-base font-medium text-rr-navy no-underline hover:bg-rr-teal-tint"
              (click)="choose(section, $event)"
            >{{ section.label }}</a>
          }
          <div class="mt-3 flex flex-col gap-2 border-t border-rr-line pt-3">
            @if (isSignedIn) {
              <a [routerLink]="appEntry" class="rr-button-primary w-full" (click)="closeMenu(false)">Open app</a>
            } @else {
              <button type="button" class="rr-button-secondary w-full" data-testid="landing-menu-sign-in" (click)="closeMenu(false); signInRequested.emit()">
                Sign in
              </button>
              <button type="button" class="rr-button-primary w-full" data-testid="landing-menu-get-started" (click)="closeMenu(false); getStartedRequested.emit()">
                Get started
              </button>
            }
          </div>
        </nav>
      </div>
    </header>
  `
})
export class LandingHeaderComponent {
  @Input() isSignedIn = false;

  @Output() readonly sectionRequested = new EventEmitter<string>();
  @Output() readonly signInRequested = new EventEmitter<void>();
  @Output() readonly getStartedRequested = new EventEmitter<void>();

  @ViewChild('menuButton') private readonly menuButton?: ElementRef<HTMLButtonElement>;

  readonly sections = LANDING_SECTIONS;
  readonly appEntry = APP_ENTRY_URL;
  readonly homeLink = `/${PUBLIC_HOME_ROUTE}`;

  isMenuOpen = false;

  toggleMenu(): void {
    this.isMenuOpen = !this.isMenuOpen;
  }

  /** Closes the menu; `returnFocus` puts focus back on its button, as dismissing it should. */
  closeMenu(returnFocus: boolean): void {
    this.isMenuOpen = false;
    if (returnFocus) {
      this.menuButton?.nativeElement.focus();
    }
  }

  /**
   * A section link stays a real in-page link (`href="#features"`) for what the browser offers on
   * a link, but a plain activation is handled here: the page scrolls to the section and moves focus
   * to it, which a fragment navigation through the router would do on neither route the page has.
   */
  choose(section: LandingSection, event: Event): void {
    event.preventDefault();
    this.isMenuOpen = false;
    this.sectionRequested.emit(section.id);
  }

  @HostListener('document:keydown.escape')
  onEscape(): void {
    if (this.isMenuOpen) {
      this.closeMenu(true);
    }
  }
}
