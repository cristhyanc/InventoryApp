import { Component } from '@angular/core';
import { PRIVACY_POLICY_CONTENT } from './privacy-policy-content';
import { LegalPageComponent } from './legal-page.component';
import { TERMS_OF_USE_ROUTE } from './legal-routes';

/** The Privacy Policy page layout (issue #1190), reachable only in a development build. */
@Component({
  selector: 'app-privacy-policy-page',
  standalone: true,
  imports: [LegalPageComponent],
  template: `
    <app-legal-page [content]="content" [crossLinkRoute]="crossLinkRoute" crossLinkLabel="View Terms of Use" />
  `
})
export class PrivacyPolicyPageComponent {
  readonly content = PRIVACY_POLICY_CONTENT;
  readonly crossLinkRoute = TERMS_OF_USE_ROUTE;
}
