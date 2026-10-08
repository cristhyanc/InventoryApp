import { Component, Input } from '@angular/core';
import { IconShapes, IconVariant, iconShapes } from './icon-paths';

/**
 * Renders one inline SVG icon from the bundled Material Icons path sets (issues #411 and #456,
 * licence in `THIRD-PARTY-NOTICES.md`). An unknown `name`, or a `name` the requested `variant`
 * does not bundle, renders nothing and never throws, so a typo or a not-yet-added icon fails
 * silently rather than breaking the page or substituting the wrong glyph.
 *
 * Variants: `rounded` is the default and is the filled Rounded set every stat card, icon tile and
 * action button uses. `outlined` is the unfilled Outlined set, and the sidebar navigation is its
 * only caller — asking for it explicitly is what keeps the navigation's outlined look from
 * leaking into the rest of the application (issue #456).
 *
 * A glyph is a list of shapes rather than one path, because several Outlined glyphs are published
 * as more than one `<path>`, or as a `<path>` plus a `<circle>`.
 *
 * Accessibility: without `label` the icon is decorative (`aria-hidden="true"`, no role or
 * accessible name). With `label` it is `role="img"` with that label as its accessible name, and
 * `aria-hidden` is removed rather than set to `"false"`. The SVG is never focusable either way.
 */
@Component({
  selector: 'app-icon',
  standalone: true,
  template: `
    @if (shapes) {
      <svg
        [attr.width]="size"
        [attr.height]="size"
        viewBox="0 0 24 24"
        fill="currentColor"
        [attr.role]="label ? 'img' : null"
        [attr.aria-label]="label ? label : null"
        [attr.aria-hidden]="label ? null : 'true'"
      >
        @for (path of shapes.paths; track path) {
          <path [attr.d]="path" />
        }
        @for (circle of shapes.circles ?? []; track circle) {
          <circle [attr.cx]="circle.cx" [attr.cy]="circle.cy" [attr.r]="circle.r" />
        }
      </svg>
    }
  `
})
export class IconComponent {
  @Input({ required: true }) name = '';
  @Input() size = 24;
  @Input() label?: string;
  @Input() variant: IconVariant = 'rounded';

  get shapes(): IconShapes | undefined {
    return iconShapes(this.name, this.variant);
  }
}
