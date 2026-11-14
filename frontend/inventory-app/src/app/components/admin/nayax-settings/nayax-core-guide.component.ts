import { Component, Input } from '@angular/core';
import { CommonModule } from '@angular/common';

/** Nayax Core's own sign-in page, as Nayax's Lynx token guide links it. */
export const NAYAX_CORE_URL = 'https://my.nayax.com/core/LoginPage.aspx';

/** One screenshot from Nayax's official documentation, shown where it is published. */
export interface NayaxCoreScreenshot {
  /** The image exactly as Nayax's developer portal publishes it. */
  readonly src: string;
  readonly alt: string;
  readonly caption: string;
}

/** The official guide a step's instructions and screenshots come from. */
export interface NayaxCoreGuideSource {
  readonly title: string;
  readonly url: string;
}

/**
 * The **In Nayax Core** half of a Connect Nayax step (issue #1311): numbered text instructions, the
 * official Nayax screenshots that show them, an **Open Nayax Core** link and the source guide.
 *
 * **The text is the instruction; a screenshot only illustrates it.** Every step is written out, so
 * a screenshot that fails to load is replaced by a sentence pointing at the guide and nothing an
 * Owner needs goes with it.
 *
 * **The screenshots are Nayax's, shown from where Nayax publishes them.** Nayax's developer portal
 * states no terms that permit copying its images, so none is bundled or edited here: each is
 * displayed from the portal's own image address, credited to and linked with the guide it belongs
 * to, and requested with no referrer. Each one opens full size in a new tab, which is how a phone
 * enlarges it. Which screenshots are used, and why the token-revealing one is not, is the wizard's
 * choice; see docs/architecture.md § The Connect Nayax dialog.
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

      @for (shot of screenshots; track shot.src) {
        <figure class="m-0 min-w-0" data-testid="nayax-screenshot">
          @if (!failed.has(shot.src)) {
            <a
              class="block overflow-hidden rounded-lg border border-md-gray-200"
              target="_blank"
              rel="noopener noreferrer"
              [href]="shot.src"
              [attr.aria-label]="'Enlarge screenshot: ' + shot.caption + ' (opens in a new tab)'"
              data-testid="enlarge-screenshot"
            >
              <img
                class="block h-auto w-full"
                loading="lazy"
                referrerpolicy="no-referrer"
                [src]="shot.src"
                [alt]="shot.alt"
                (error)="failed.add(shot.src)"
              />
            </a>
          } @else {
            <p class="rounded-lg border border-dashed border-md-gray-200 p-3 text-sm value-muted" data-testid="screenshot-unavailable">
              The screenshot could not be loaded. The steps above are complete, and the Nayax guide
              below shows the same screen.
            </p>
          }
          <figcaption class="mt-1 text-xs value-muted">{{ shot.caption }}</figcaption>
        </figure>
      }

      <p class="text-xs value-muted" data-testid="nayax-guide-source">
        Screenshots and steps from Nayax's official documentation:
        @for (source of sources; track source.url; let last = $last) {
          <a class="btn-link" target="_blank" rel="noopener noreferrer" [href]="source.url">{{ source.title }}</a>{{ last ? '.' : ',' }}
        }
      </p>
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

  @Input() screenshots: readonly NayaxCoreScreenshot[] = [];

  /** The official guides the steps and screenshots come from, credited and linked under them. */
  @Input({ required: true }) sources: readonly NayaxCoreGuideSource[] = [];

  /** The screenshots that failed to load, replaced by a sentence rather than a broken image. */
  readonly failed = new Set<string>();
}
