/** @type {import('tailwindcss').Config} */
//
// Material Dashboard 3 design tokens (issue #410).
//
// This file is the single source of truth for the InventoryApp visual language described in
// docs/architecture.md § Interaction and presentation. `src/styles.scss` builds every shared
// component class from these tokens with `@apply`, and
// `src/app/design-system/design-tokens.contrast.spec.ts` reads the values back out of this file
// and asserts that every text/background pair the shared classes define still reaches the
// WCAG 2.2 AA 4.5:1 minimum, so a token edit cannot silently break contrast.
//
// Do not introduce a colour, shadow, radius or font value in a template, a component style or
// `styles.scss` that is not defined here.

// Material Dashboard 3 renders all of its gradients at the same 195deg angle.
const gradient = (from, to) => `linear-gradient(195deg, ${from}, ${to})`;

module.exports = {
  content: ['./src/**/*.{html,ts}'],
  theme: {
    extend: {
      colors: {
        // Decorative solid colours: icon tiles, the left border of an alert, the 15% tints
        // behind badges and alerts, the focus outline, and icons that sit next to a text
        // label. They are never a text colour, and never the background of white text.
        'md-dark': '#262626',
        'md-info': '#1A73E8',
        'md-success': '#4CAF50',
        'md-warning': '#FB8C00',
        'md-danger': '#F44335',

        // Text variants. All coloured text uses these, never the solid colours above.
        'md-dark-text': '#262626',
        'md-info-text': '#1557B0',
        'md-success-text': '#1B5E20',
        'md-warning-text': '#8A4B00',
        'md-danger-text': '#B71C1C',

        'md-gray': {
          100: '#F5F5F5', // page canvas, table header, table row hover
          200: '#E5E5E5', // card border and dividers
          300: '#D4D4D4', // secondary button border
          500: '#737373', // muted text on white only (4.74:1; only 4.35:1 on md-gray-100)
          600: '#525252', // body copy, and muted text on a gray surface (7.17:1)
          800: '#262626', // headings, values, alert body text
        },
        'md-input-border': '#D2D6DA', // form field borders
      },
      backgroundImage: {
        'md-dark-gradient': gradient('#42424a', '#191919'),
        'md-info-gradient': gradient('#49a3f1', '#1A73E8'),
        'md-success-gradient': gradient('#66BB6A', '#43A047'),
        'md-warning-gradient': gradient('#FFA726', '#FB8C00'),
        // Icon tiles only. A danger *button* uses the darker gradient below, because that is
        // the only coloured gradient that keeps white label text above 4.5:1.
        'md-danger-gradient': gradient('#EF5350', '#E53935'),
        'md-danger-button-gradient': gradient('#D32F2F', '#B71C1C'),
      },
      boxShadow: {
        'md-card': '0 1px 2px 0 rgb(0 0 0 / 0.05)',
        md: '0 4px 6px -1px rgba(0, 0, 0, 0.1), 0 2px 4px -1px rgba(0, 0, 0, 0.06)',
        'md-lg': '0 10px 15px -3px rgba(0, 0, 0, 0.1), 0 4px 6px -2px rgba(0, 0, 0, 0.05)',
        'md-tile-dark': '0 4px 20px 0 rgba(0, 0, 0, 0.14), 0 7px 10px -5px rgba(64, 64, 64, 0.4)',
        'md-tile-info': '0 4px 20px 0 rgba(0, 0, 0, 0.14), 0 7px 10px -5px rgba(0, 188, 212, 0.4)',
        'md-tile-success': '0 4px 20px 0 rgba(0, 0, 0, 0.14), 0 7px 10px -5px rgba(76, 175, 80, 0.4)',
        'md-tile-warning': '0 4px 20px 0 rgba(0, 0, 0, 0.14), 0 7px 10px -5px rgba(255, 152, 0, 0.4)',
        'md-tile-danger': '0 4px 20px 0 rgba(0, 0, 0, 0.14), 0 7px 10px -5px rgba(244, 67, 54, 0.4)',
      },
      borderRadius: {
        'md-control': '0.375rem', // buttons, inputs
        'md-card': '0.5rem', // cards, icon tiles, dropdowns
        'md-badge': '0.45rem',
        'md-dialog': '0.75rem',
      },
      fontFamily: {
        // Inter is bundled by @fontsource/inter (weights 400/500/600/700, loaded from
        // angular.json). The fallback is the system stack the application used before.
        sans: ['Inter', "'Segoe UI'", 'Roboto', 'Helvetica', 'Arial', 'sans-serif'],
      },
      fontSize: {
        // Sizes only: line height stays the Tailwind preflight 1.5 so these tokens do not
        // invent a value the visual specification does not give.
        'md-page-title': '1.25rem',
        'md-card-title': '1rem',
        'md-stat-value': '1.5rem',
        'md-body': '0.875rem',
        'md-badge': '0.75rem',
        'md-table-head': '0.65rem',
      },
    },
  },
  plugins: [],
};
