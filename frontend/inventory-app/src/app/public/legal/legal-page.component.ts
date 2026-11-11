import { DOCUMENT } from '@angular/common';
import { Component, Inject, Input } from '@angular/core';
import { RouterLink } from '@angular/router';
import { IconComponent } from '../../components/shared/icon.component';
import { LEGAL_CONTACT, LegalPageContent } from './legal-page-content';
import { PUBLIC_HOME_ROUTE } from '../landing/landing-routes';

/**
 * The shared layout both legal page layouts (issue #1190) render their own
 * {@link LegalPageContent} through: a heading, an effective-date line, an optional introduction, a
 * table of contents linking to in-page section anchors, the numbered sections themselves, the
 * business display details and a link back to the homepage and to the other legal page.
 *
 * Reachability, the public layout and the "no API request" guarantee are the route's concern
 * (`legal-routes.ts`), not this component's - it is a plain presentational component with no
 * injected service.
 */
@Component({
  selector: 'app-legal-page',
  standalone: true,
  imports: [IconComponent, RouterLink],
  templateUrl: './legal-page.component.html'
})
export class LegalPageComponent {
  @Input({ required: true }) content!: LegalPageContent;
  @Input({ required: true }) crossLinkRoute = '';
  @Input({ required: true }) crossLinkLabel = '';

  readonly contact = LEGAL_CONTACT;
  readonly homeLink = `/${PUBLIC_HOME_ROUTE}`;
  readonly year = new Date().getFullYear();

  constructor(@Inject(DOCUMENT) private readonly document: Document) {}

  /**
   * Scrolls to a section and moves keyboard focus to it, so the next Tab continues from there and
   * the section heading is in view - the same approach the landing page's own in-page navigation
   * uses (`landing-page.component.ts`).
   */
  scrollToSection(id: string, event: Event): void {
    event.preventDefault();
    const section = this.document.getElementById(id);
    if (!section) {
      return;
    }

    section.scrollIntoView({ behavior: this.prefersReducedMotion() ? 'auto' : 'smooth', block: 'start' });
    section.focus({ preventScroll: true });
  }

  private prefersReducedMotion(): boolean {
    return this.document.defaultView?.matchMedia?.('(prefers-reduced-motion: reduce)').matches ?? false;
  }
}
