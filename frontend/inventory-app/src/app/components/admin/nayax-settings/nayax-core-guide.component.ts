import { Component, Input } from '@angular/core';
import { CommonModule } from '@angular/common';

/** Nayax Core's own sign-in page, as Nayax's Lynx token guide links it. */
export const NAYAX_CORE_URL = 'https://my.nayax.com/core/LoginPage.aspx';

/** An official Nayax guide a step's instructions come from, and where its screenshots are. */
export interface NayaxCoreGuideSource {
  readonly title: string;
  readonly url: string;
}

/**
 * The **In Nayax Core** half of a Connect Nayax step (issue #1311): numbered text instructions, an
 * **Open Nayax Core** link, and the official Nayax guides they come from.
 *
 * **No Nayax screenshot is embedded.** Every screenshot Nayax publishes for these screens is taken in
 * its demo account and shows an account identifier - a user's name, an operator name or an operator
 * id - and the issue excludes account identifiers from what this page delivers. Nayax's portal also
 * states no terms that would allow a redacted copy. So, as the issue directs for that case, the
 * written steps carry the instruction on their own and each guide is linked, opening in a new tab,
 * as the place to see the screens. See docs/architecture.md § The Connect Nayax dialog.
 */
@Component({
  selector: 'app-nayax-core-guide',
  standalone: true,
  imports: [CommonModule],
  template: `
    <section class="flex min-w-0 flex-col gap-3" [attr.aria-labelledby]="headingId" data-testid="nayax-core-guide">
      <div class="flex flex-wrap items-center justify-between gap-2">
        <h3 class="text-sm font-semibold text-md-gray-800" [id]="headingId">In Nayax Core</h3>
        <a
          class="btn btn-secondary btn-sm"
          data-testid="open-nayax-core"
          target="_blank"
          rel="noopener noreferrer"
          [href]="nayaxCoreUrl"
        >Open Nayax Core<span class="sr-only"> (opens in a new tab)</span> <span aria-hidden="true">&#8599;</span></a>
      </div>

      <ol class="ml-5 list-decimal space-y-1 text-sm text-md-gray-800" data-testid="nayax-core-steps">
        @for (instruction of steps; track $index) {
          <li [innerHTML]="instruction"></li>
        }
      </ol>

      <div class="rounded-lg border border-md-gray-200 p-3" data-testid="nayax-guide-source">
        <p class="text-sm text-md-gray-800">See each screen in Nayax's official guide:</p>
        <ul class="mt-1 space-y-1 text-sm">
          @for (source of sources; track source.url) {
            <li>
              <a class="btn-link" target="_blank" rel="noopener noreferrer" [href]="source.url">
                {{ source.title }}<span class="sr-only"> (opens in a new tab)</span> <span aria-hidden="true">&#8599;</span>
              </a>
            </li>
          }
        </ul>
      </div>
    </section>
  `
})
export class NayaxCoreGuideComponent {
  private static instances = 0;

  readonly nayaxCoreUrl = NAYAX_CORE_URL;
  readonly headingId = `nayax-core-guide-${++NayaxCoreGuideComponent.instances}`;

  /**
   * The numbered instructions. Each is trusted, static markup written in the wizard (bold control
   * names only), never text that came from a server or an Owner.
   */
  @Input({ required: true }) steps: readonly string[] = [];

  /** The official guides the steps come from, linked so an Owner can see each screen there. */
  @Input({ required: true }) sources: readonly NayaxCoreGuideSource[] = [];
}
