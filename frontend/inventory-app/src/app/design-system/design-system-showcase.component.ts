import { Component } from '@angular/core';

/**
 * Rendering fixture for the shared Material Dashboard classes defined in `src/styles.scss`
 * (issue #410).
 *
 * It is deliberately not routed and not linked from navigation. Two jobs:
 *
 *  1. it is the single place that renders every shared class, so the showcase spec can assert
 *     they all still render and the pull request screenshots can show the whole visual language
 *     on one page at 1440px and 390px;
 *  2. it is what puts the shared classes into the generated `src/styles.css`, because Tailwind
 *     only emits an `@layer components` rule when it finds the class name in a scanned
 *     template.
 *
 * The content is neutral sample text, never real business data. Keep it declarative: no
 * services, inputs, outputs or state.
 */
@Component({
  selector: 'app-design-system-showcase',
  standalone: true,
  templateUrl: './design-system-showcase.component.html',
})
export class DesignSystemShowcaseComponent {}
