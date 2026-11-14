import { DOCUMENT, NgTemplateOutlet } from '@angular/common';
import { Component, Inject, Input } from '@angular/core';
import { RouterLink } from '@angular/router';
import { BrandLogoComponent } from '../../components/shared/brand-logo.component';
import { IconComponent } from '../../components/shared/icon.component';
import {
  LEGAL_CONTACT,
  LegalPageBlock,
  LegalPageContent,
  LegalPageList,
  LegalTextSegment,
  isLegalPageList,
  legalTextSegments
} from './legal-page-content';
import { PUBLIC_HOME_ROUTE } from '../landing/landing-routes';

/**
 * The shared layout both legal pages (issues #1190 and #1214) render their own
 * {@link LegalPageContent} through: a heading, an effective-date line, an optional introduction, a
 * table of contents linking to in-page section anchors, the numbered sections themselves (each a
 * run of paragraphs and bulleted lists, with email and web addresses rendered as links), the
 * business display details and a link back to the homepage and to the other legal page.
 *
 * Reachability, the public layout and the "no API request" guarantee are the route's concern
 * (`legal-routes.ts`), not this component's - it is a plain presentational component with no
 * injected service.
 */
@Component({
  selector: 'app-legal-page',
  standalone: true,
  imports: [BrandLogoComponent, IconComponent, NgTemplateOutlet, RouterLink],
  templateUrl: './legal-page.component.html'
})
export class LegalPageComponent {
  @Input({ required: true }) content!: LegalPageContent;
  @Input({ required: true }) crossLinkRoute = '';
  @Input({ required: true }) crossLinkLabel = '';

  readonly contact = LEGAL_CONTACT;
  readonly homeLink = `/${PUBLIC_HOME_ROUTE}`;
  readonly year = new Date().getFullYear();

  /** Splits a paragraph or list item into plain text and links. */
  segments(text: string): LegalTextSegment[] {
    return legalTextSegments(text);
  }

  /** Narrows a body block to a bulleted list in the template. */
  isList(block: LegalPageBlock): block is LegalPageList {
    return isLegalPageList(block);
  }

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
