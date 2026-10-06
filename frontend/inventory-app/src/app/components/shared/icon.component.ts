import { Component, Input } from '@angular/core';
import { ICON_PATHS } from './icon-paths';

/**
 * Renders one inline SVG icon from the bundled Material Icons Rounded path set (issue #411,
 * licence in `THIRD-PARTY-NOTICES.md`). An unknown `name` renders nothing and never throws, so a
 * typo or a not-yet-added icon fails silently rather than breaking the page.
 *
 * Accessibility: without `label` the icon is decorative (`aria-hidden="true"`, no role or
 * accessible name). With `label` it is `role="img"` with that label as its accessible name, and
 * `aria-hidden` is removed rather than set to `"false"`. The SVG is never focusable either way.
 */
@Component({
  selector: 'app-icon',
  standalone: true,
  template: `
    @if (path) {
      <svg
        [attr.width]="size"
        [attr.height]="size"
        viewBox="0 0 24 24"
        fill="currentColor"
        [attr.role]="label ? 'img' : null"
        [attr.aria-label]="label ? label : null"
        [attr.aria-hidden]="label ? null : 'true'"
      >
        <path [attr.d]="path" />
      </svg>
    }
  `
})
export class IconComponent {
  @Input({ required: true }) name = '';
  @Input() size = 24;
  @Input() label?: string;

  get path(): string | undefined {
    return ICON_PATHS[this.name];
  }
}
