import { Component } from '@angular/core';
import { TERMS_OF_USE_CONTENT } from './terms-of-use-content';
import { LegalPageComponent } from './legal-page.component';
import { PRIVACY_POLICY_ROUTE } from './legal-routes';

/** The Terms of Use page layout (issue #1190), reachable only in a development build. */
@Component({
  selector: 'app-terms-of-use-page',
  standalone: true,
  imports: [LegalPageComponent],
  template: `
    <app-legal-page [content]="content" [crossLinkRoute]="crossLinkRoute" crossLinkLabel="View Privacy Policy" />
  `
})
export class TermsOfUsePageComponent {
  readonly content = TERMS_OF_USE_CONTENT;
  readonly crossLinkRoute = PRIVACY_POLICY_ROUTE;
}
